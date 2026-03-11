using hasheous_taskrunner.Classes.Tasks;

namespace hasheous_taskrunner.Classes.Communication
{
    /// <summary>
    /// Verifies, executes, and reports back the results of tasks fetched from the configured host. This class manages the lifecycle of task runners and ensures that tasks are processed according to the defined fetch interval and concurrency limits.
    /// </summary>
    public class TaskExecutor : Helpers.StatusUpdate
    {
        /// <summary>
        /// Gets the task item associated with this task runner. The task item contains all the necessary information about the task to be executed, including its type, parameters, and identifiers.
        /// </summary>
        public TaskItem job { get; private set; }

        private ITask? handler { get; set; } = null;

        /// <summary>
        /// Gets the result of the task verification process. This property holds the outcome of the verification step, which determines whether the task is valid and can be executed. The verification result includes a status indicating success or failure, as well as any relevant details or error messages that may assist in diagnosing issues with the task.
        /// </summary>
        public TaskVerificationResult VerificationResult { get; private set; } = new TaskVerificationResult
        {
            Status = TaskVerificationResult.VerificationStatus.NotYetVerified
        };

        /// <summary>
        /// Gets the result of the task execution process. This property holds the outcome of the execution step, which includes any data or results produced by the task.
        /// </summary>
        public Dictionary<string, object> ExecutionResult { get; private set; } = new Dictionary<string, object>();

        /// <summary>
        /// Initializes a new instance of the TaskRunner class with the specified task item.
        /// </summary>
        /// <param name="job">
        /// The task item to be executed by this task executor.
        /// </param>
        public TaskExecutor(TaskItem job)
        {
            this.job = job;
            this.job.Status = QueueItemStatus.Assigned;

            // find the appropriate task handler based on job.TaskName = ITask.TaskType
            var taskType = typeof(ITask);
            var taskHandlers = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(s => s.GetTypes())
                .Where(p => taskType.IsAssignableFrom(p) && !p.IsInterface && !p.IsAbstract);

            foreach (var handlerType in taskHandlers)
            {
                var instance = Activator.CreateInstance(handlerType) as ITask;
                if (instance?.TaskType == job.TaskName)
                {
                    handler = instance;
                    break;
                }
            }

            if (handler == null)
            {
                job.Status = QueueItemStatus.Failed;
                throw new InvalidOperationException($"No task handler found for task type: {job.TaskName}");
            }
        }

        /// <summary>
        /// Verifies the task parameters asynchronously. This method checks whether the task parameters are valid and whether the task can be executed. If the verification fails, the task status is updated to Failed, and an error message is logged. If no handler is found for the task type, an exception is thrown, and the task status is also set to Failed.
        /// </summary>
        /// <param name="cancellationToken">A token to observe while waiting for the verification to complete.</param>
        /// <returns>A TaskVerificationResult indicating the result of the verification.</returns>
        /// <exception cref="InvalidOperationException">Thrown if no task handler is found for the task type.</exception>
        public async Task<TaskVerificationResult> VerifyTaskAsync(CancellationToken cancellationToken = default)
        {
            if (handler == null)
            {
                job.Status = QueueItemStatus.Failed;
                AddStatus(StatusItem.StatusType.Error, $"No task handler found for task type: {job.TaskName}");
                throw new InvalidOperationException($"No task handler found for task type: {job.TaskName}");
            }
            VerificationResult = await handler.VerifyAsync(job.Parameters, cancellationToken);

            return VerificationResult;
        }

        /// <summary>
        /// Executes the task asynchronously. This method performs the actual execution of the task using the appropriate handler. If the execution is successful, the task status is updated to WaitingForSubmission, indicating that the task has been executed and is waiting to be reported back to the host. If the execution is cancelled, the task status is updated to Cancelled, and an informational message is logged. If any other exception occurs during execution, the task status is updated to Failed, and an error message is logged with details of the exception.
        /// </summary>
        /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
        /// <returns>A dictionary containing the results of the task execution.</returns>
        /// <exception cref="InvalidOperationException">Thrown if no task handler is found for the task type.</exception>
        public async Task<Dictionary<string, object>> ExecuteTaskAsync(CancellationToken cancellationToken = default)
        {
            if (handler == null)
            {
                job.Status = QueueItemStatus.Failed;
                AddStatus(StatusItem.StatusType.Error, $"No task handler found for task type: {job.TaskName}");
                throw new InvalidOperationException($"No task handler found for task type: {job.TaskName}");
            }
            try
            {
                ExecutionResult = await handler.ExecuteAsync(job.Parameters, this, cancellationToken);

                // After execution, we set the status to WaitingForSubmission to indicate that the task has been executed and is waiting to be reported back to the host. The actual reporting back is handled in the main task loop, which will update the status to Submitted once the report is sent.
                job.Status = QueueItemStatus.WaitingForSubmission;
            }
            catch (OperationCanceledException)
            {
                job.Status = QueueItemStatus.Cancelled;
                AddStatus(StatusItem.StatusType.Info, $"Task ID {job.Id} cancelled.");
            }
            catch (Exception ex)
            {
                job.Status = QueueItemStatus.Failed;
                ExecutionResult = new Dictionary<string, object>
                {
                    { "error", ex.Message }
                };
                AddStatus(StatusItem.StatusType.Error, $"Execution of task ID {job.Id} failed: {ex.Message}");
            }

            return ExecutionResult;
        }
    }
}