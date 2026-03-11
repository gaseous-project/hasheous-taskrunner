using hasheous_taskrunner.Classes.Tasks;
using static hasheous_taskrunner.Classes.Helpers.StatusUpdate;

namespace hasheous_taskrunner.Classes.Communication
{
    /// <summary>
    /// Container for task-related communication helpers used by the task runner. This class is responsible for fetching tasks from the configured host, launching the background task to verify, execute, and report back the results of the task.
    /// </summary>
    public static class Tasks
    {
        private static DateTime lastTaskFetch = DateTime.MinValue;
        private static readonly TimeSpan taskFetchInterval = TimeSpan.FromSeconds(5);

        private static Dictionary<long, Task> activeTaskRunners = new Dictionary<long, Task>();
        private static Dictionary<long, TaskExecutor> activeTaskExecutors = new Dictionary<long, TaskExecutor>();

        /// <summary>
        /// Gets the collection of active task executors.
        /// </summary>
        public static IReadOnlyDictionary<long, TaskExecutor> ActiveTaskExecutors => activeTaskExecutors;

        /// <summary>
        /// Gets or sets the maximum number of concurrent task runners allowed. Default is set to 1, meaning only one task will be executed at a time. This property can be adjusted to allow for more concurrent tasks based on the capabilities of the host machine and the requirements of the tasks being executed.
        /// </summary>
        public static int MaxConcurrentTasks
        {
            get => _MaxConcurrentTasks;
            set
            {
                if (value < 1)
                {
                    _MaxConcurrentTasks = 1;
                }
                else if (value > 20)
                {
                    _MaxConcurrentTasks = 20;
                }
                else
                {
                    _MaxConcurrentTasks = value;
                }
            }
        }
        private static int _MaxConcurrentTasks = 1;

        /// <summary>
        /// Indicates whether a task is currently being executed.
        /// </summary>
        public static bool IsRunningTask { get; set; } = false;

        /// <summary>
        /// Fetches and executes tasks from the configured host if the fetch interval has elapsed.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static async Task FetchAndExecuteTasksIfDue(CancellationToken cancellationToken = default)
        {
            if (DateTime.UtcNow - lastTaskFetch >= taskFetchInterval)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IsRunningTask = true;

                // First, clean up completed tasks to free up slots
                var completedJobIds = activeTaskRunners
                    .Where(kvp => kvp.Value.IsCompleted)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var jobId in completedJobIds)
                {
                    activeTaskRunners.Remove(jobId);
                    activeTaskExecutors.Remove(jobId);
                    Console.WriteLine($"Task {jobId} completed and removed from active runners.");
                }

                // progress existing tasks before fetching new ones
                foreach (var executor in activeTaskExecutors.Values)
                {
                    switch (executor.job.Status)
                    {
                        case QueueItemStatus.Assigned:
                            // task has been assigned and needs to be verified
                            try
                            {
                                executor.job.Status = QueueItemStatus.Verifying;
                                Console.WriteLine($"Verifying task ID {executor.job.Id}...");
                                await executor.VerifyTaskAsync(cancellationToken);
                                Console.WriteLine($"Verification result for task ID {executor.job.Id}: {executor.VerificationResult.Status}");
                                executor.job.Status = QueueItemStatus.Verified;
                                cancellationToken.ThrowIfCancellationRequested();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Task {executor.job.Id} verification failed: {ex.Message}");
                                executor.job.Status = QueueItemStatus.Failed;
                            }
                            break;

                        case QueueItemStatus.Verified:
                            // task has been verified and needs to be acknowledged
                            string acknowledgeUrl = $"{Config.BaseUriPath}/clients/{Config.GetAuthValue("client_id")}/job";

                            Dictionary<string, object> ackPayload = new Dictionary<string, object>
                            {
                                { "task_id", executor.job.Id }
                            };
                            QueueItemStatus verificationTargetStatus;
                            if (executor.VerificationResult.Status == TaskVerificationResult.VerificationStatus.Success)
                            {
                                verificationTargetStatus = QueueItemStatus.WaitingToStart;
                                ackPayload["status"] = QueueItemStatus.InProgress.ToString();
                                ackPayload["result"] = "";
                                ackPayload["error_message"] = "";
                            }
                            else
                            {
                                verificationTargetStatus = QueueItemStatus.Failed;
                                ackPayload["status"] = QueueItemStatus.Failed.ToString();
                                ackPayload["result"] = "";
                                ackPayload["error_message"] = executor.VerificationResult.Details;
                            }

                            Console.WriteLine($"Acknowledging task ID {executor.job.Id} with status {ackPayload["status"]}...");

                            try
                            {
                                await Common.Post<object>(acknowledgeUrl, ackPayload);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Acknowledgment failed for task ID {executor.job.Id}: {ex.Message}");
                            }

                            // Update the job status based on verification result
                            executor.job.Status = verificationTargetStatus;

                            // if verification failed, do not execute
                            if (executor.VerificationResult.Status != TaskVerificationResult.VerificationStatus.Success)
                            {
                                Console.WriteLine($"Skipping execution of task ID {executor.job.Id} due to verification failure.");
                                executor.job.Status = QueueItemStatus.Failed;
                            }

                            break;

                        case QueueItemStatus.WaitingToStart:
                            // task is waiting to start, so we can execute it
                            executor.job.Status = QueueItemStatus.InProgress;
                            Console.WriteLine($"Starting execution of task ID {executor.job.Id}...");
                            _ = Task.Run(() => executor.ExecuteTaskAsync(cancellationToken), cancellationToken);
                            break;

                        case QueueItemStatus.WaitingForSubmission:
                            // task is waiting for submission, so we can submit it
                            executor.job.Status = QueueItemStatus.Submitted;
                            Console.WriteLine($"Submitting task ID {executor.job.Id}...");

                            Dictionary<string, object> submissionPayload = new Dictionary<string, object>
                            {
                                { "status", QueueItemStatus.Submitted.ToString() },
                                { "result", executor.ExecutionResult.ContainsKey("response") ? executor.ExecutionResult["response"] : "" },
                                { "error_message", executor.ExecutionResult.ContainsKey("error") ? executor.ExecutionResult["error"] : "" }
                            };
                            break;

                        default:
                            Console.WriteLine($"Task {executor.job.Id} is in an unknown status: {executor.job.Status}");
                            executor.job.Status = QueueItemStatus.Failed;
                            break;
                    }
                }

                // report current status before fetching new tasks
                Console.WriteLine($"Checking for new tasks... Active tasks: {activeTaskRunners.Count}/{MaxConcurrentTasks}");

                // Fetch tasks from the server only if we have capacity to run them
                if (activeTaskRunners.Count >= MaxConcurrentTasks)
                {
                    IsRunningTask = false;
                    lastTaskFetch = DateTime.UtcNow;
                    return;
                }

                // Fetch new tasks from the server
                string fetchTasksUrl = $"{Config.BaseUriPath}/clients/{Config.GetAuthValue("client_id")}/job?numberOfTasks={MaxConcurrentTasks}";
                try
                {
                    var jobs = await Common.Get<List<TaskItem>?>(fetchTasksUrl);
                    if (jobs != null)
                    {
                        Console.WriteLine($"Fetched {jobs.Count} job(s) from server. Currently running: {activeTaskRunners.Count}");

                        foreach (var job in jobs)
                        {
                            // Check if this job is already running
                            if (activeTaskRunners.ContainsKey(job.Id))
                            {
                                continue;
                            }

                            // Wait for capacity if we're at the limit
                            while (activeTaskRunners.Count >= MaxConcurrentTasks)
                            {
                                Console.WriteLine($"Maximum concurrent tasks ({MaxConcurrentTasks}) reached. Waiting for a task to complete...");

                                // Wait for any of the active task runners to complete
                                var completedRunner = await Task.WhenAny(activeTaskRunners.Values);

                                // Find and remove the completed task
                                var completedEntry = activeTaskRunners.First(kvp => kvp.Value == completedRunner);
                                activeTaskRunners.Remove(completedEntry.Key);
                                activeTaskExecutors.Remove(completedEntry.Key);
                                Console.WriteLine($"Task {completedEntry.Key} completed and removed from active runners.");
                            }

                            // Start a new task executor for the fetched job
                            Console.WriteLine($"Starting new task executor for job {job.Id}");
                            var runner = new TaskExecutor(job);
                            var runnerTask = Task.Run(() => runner.RunTask(cancellationToken), cancellationToken);
                            activeTaskRunners.Add(job.Id, runnerTask);
                            activeTaskExecutors.Add(job.Id, runner);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to fetch tasks: {ex.Message}");
                }
                lastTaskFetch = DateTime.UtcNow;
                IsRunningTask = false;
                Console.WriteLine($"Task processing cycle complete. Active tasks: {activeTaskRunners.Count}. Waiting for next interval.");
            }
        }
    }
}