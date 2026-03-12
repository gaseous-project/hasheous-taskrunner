using System.Collections.Concurrent;
using hasheous_taskrunner.Classes.Tasks;
using static hasheous_taskrunner.Classes.Helpers.StatusUpdate;

namespace hasheous_taskrunner.Classes.Communication
{
    /// <summary>
    /// Container for task-related communication helpers used by the task runner. This class is responsible for fetching tasks from the configured host, launching the background task to verify, execute, and report back the results of the task.
    /// </summary>
    public static class Tasks
    {
        private const string ResponseResultKey = "response";
        private const string ErrorResultKey = "error";

        private static DateTime lastTaskFetch = DateTime.MinValue;
        private static readonly TimeSpan taskFetchInterval = TimeSpan.FromSeconds(2);
        private static readonly SemaphoreSlim taskCycleSemaphore = new SemaphoreSlim(1, 1);
        private static DateTime lastBlockedIntakeLog = DateTime.MinValue;

        private static readonly ConcurrentDictionary<long, TaskExecutor> activeTaskExecutors = new ConcurrentDictionary<long, TaskExecutor>();

        /// <summary>
        /// Gets the collection of active task executors.
        /// </summary>
        public static IReadOnlyDictionary<long, TaskExecutor> ActiveTaskExecutors => activeTaskExecutors;

        /// <summary>
        /// Gets a snapshot of active task executors for thread-safe UI rendering.
        /// </summary>
        public static IReadOnlyDictionary<long, TaskExecutor> GetActiveTaskExecutorsSnapshot()
        {
            return new Dictionary<long, TaskExecutor>(activeTaskExecutors);
        }

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
        /// Fetches and executes tasks from the configured host if the fetch interval has elapsed.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static async Task FetchAndExecuteTasksIfDue(CancellationToken cancellationToken = default)
        {
            if (!await taskCycleSemaphore.WaitAsync(0, cancellationToken))
            {
                return;
            }

            try
            {
                if (DateTime.UtcNow - lastTaskFetch < taskFetchInterval)
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // progress existing tasks before fetching new ones
                foreach (var executor in activeTaskExecutors.Values)
                {
                    string acknowledgeUrl = $"{Config.BaseUriPath}/clients/{Config.GetAuthValue("client_id")}/job";

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
                            QueueItemStatus verificationTargetStatus;
                            ResponsePayload ackPayload;
                            if (executor.VerificationResult.Status == TaskVerificationResult.VerificationStatus.Success)
                            {
                                verificationTargetStatus = QueueItemStatus.WaitingToStart;
                                ackPayload = ResponsePayload.Create(
                                    QueueItemStatus.InProgress,
                                    taskId: executor.job.Id);
                            }
                            else
                            {
                                verificationTargetStatus = QueueItemStatus.VerificationFailure;
                                ackPayload = ResponsePayload.Create(
                                    QueueItemStatus.Failed,
                                    errorMessage: executor.VerificationResult.Details,
                                    taskId: executor.job.Id);
                            }

                            Console.WriteLine($"Acknowledging task ID {executor.job.Id} with status {ackPayload.Status}...");

                            try
                            {
                                await Common.Post<object>(acknowledgeUrl, ackPayload);
                            }
                            catch (Exception ex)
                            {
                                executor.job.Status = QueueItemStatus.CommsFailure;
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

                            ResponsePayload submissionPayload = ResponsePayload.Create(
                                QueueItemStatus.Submitted,
                                result: executor.ExecutionResult.ContainsKey(ResponseResultKey) ? executor.ExecutionResult[ResponseResultKey] : null,
                                errorMessage: executor.ExecutionResult.ContainsKey(ErrorResultKey) ? executor.ExecutionResult[ErrorResultKey] : null,
                                taskId: executor.job.Id);

                            try
                            {
                                await Common.Post<object>(acknowledgeUrl, submissionPayload);
                            }
                            catch (Exception ex)
                            {
                                executor.job.Status = QueueItemStatus.CommsFailure;
                                Console.WriteLine($"Submission failed for task ID {executor.job.Id}: {ex.Message}");
                            }

                            break;

                        case QueueItemStatus.Cancelled:
                            Console.WriteLine($"Task ID {executor.job.Id} has been cancelled. No further action will be taken.");
                            break;

                        case QueueItemStatus.Failed:
                            Console.WriteLine($"Task ID {executor.job.Id} has failed.");

                            ResponsePayload failurePayload = ResponsePayload.Create(
                                QueueItemStatus.Failed,
                                errorMessage: executor.ExecutionResult.ContainsKey(ErrorResultKey) ? executor.ExecutionResult[ErrorResultKey] : null,
                                taskId: executor.job.Id);

                            try
                            {
                                await Common.Post<object>(acknowledgeUrl, failurePayload);
                            }
                            catch (Exception ex)
                            {
                                executor.job.Status = QueueItemStatus.CommsFailure;
                                Console.WriteLine($"Submission failed for task ID {executor.job.Id}: {ex.Message}");
                            }
                            break;

                        case QueueItemStatus.InProgress:
                            // task is currently in progress, so we just wait for it to complete
                            break;

                        default:
                            Console.WriteLine($"Task {executor.job.Id} is in an unknown status: {executor.job.Status}");
                            executor.job.Status = QueueItemStatus.Failed;
                            break;
                    }
                }

                // clean up completed tasks to free up slots
                var completedJobIds = activeTaskExecutors
                    .Where(kvp => kvp.Value.job.Status == QueueItemStatus.Submitted || kvp.Value.job.Status == QueueItemStatus.Cancelled || kvp.Value.job.Status == QueueItemStatus.Failed || kvp.Value.job.Status == QueueItemStatus.VerificationFailure)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var jobId in completedJobIds)
                {
                    if (activeTaskExecutors.TryRemove(jobId, out _))
                    {
                        Console.WriteLine($"Task {jobId} completed and removed from active runners.");
                    }
                }

                // Fetch tasks from the server only if we have capacity to run them
                if (activeTaskExecutors.Count >= MaxConcurrentTasks)
                {
                    lastTaskFetch = DateTime.UtcNow;
                    return;
                }

                // Registration health gate: continue progressing active tasks, but block new intake.
                if (Registration.ShouldBlockNewTasks)
                {
                    if (DateTime.UtcNow - lastBlockedIntakeLog > TimeSpan.FromSeconds(30))
                    {
                        Console.WriteLine("[INFO] New task intake is currently blocked due to registration health state.");
                        lastBlockedIntakeLog = DateTime.UtcNow;
                    }

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
                        Console.WriteLine($"Fetched {jobs.Count} job(s) from server. Currently running: {activeTaskExecutors.Count}");

                        foreach (var job in jobs)
                        {
                            // Check if this job is already running
                            if (activeTaskExecutors.ContainsKey(job.Id))
                            {
                                continue;
                            }

                            // do another check to make sure we're not above the max concurrent tasks limit before adding new ones
                            if (activeTaskExecutors.Count >= MaxConcurrentTasks)
                            {
                                break;
                            }

                            // Create a new TaskExecutor for the job and add it to the active executors
                            var executor = new TaskExecutor(job);
                            if (activeTaskExecutors.TryAdd(job.Id, executor))
                            {
                                Console.WriteLine($"Added task ID {job.Id} to active executors. Total active tasks: {activeTaskExecutors.Count}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to fetch tasks: {ex.Message}");
                }

                lastTaskFetch = DateTime.UtcNow;
                Console.WriteLine($"Task processing cycle complete. Active tasks: {activeTaskExecutors.Count}. Waiting for next interval.");
            }
            finally
            {
                taskCycleSemaphore.Release();
            }
        }
    }
}