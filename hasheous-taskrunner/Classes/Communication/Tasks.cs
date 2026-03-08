using hasheous_taskrunner.Classes.Tasks;

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