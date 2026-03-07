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

        /// <summary>
        /// Initializes a new instance of the TaskRunner class with the specified task item.
        /// </summary>
        /// <param name="job">
        /// The task item to be executed by this task executor.
        /// </param>
        public TaskExecutor(TaskItem job)
        {
            this.job = job;
        }

        /// <summary>
        /// Runs the task by verifying it, executing it if verification is successful, and reporting back the results to the configured host. This method handles the entire lifecycle of a task, including error handling and status reporting.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no task handler is found for the specified task type.</exception>
        public async Task RunTask(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddStatus(StatusItem.StatusType.Info, $"Fetched task ID {job.Id} of type {job.TaskName}.");

            // find the appropriate task handler based on job.TaskName = ITask.TaskType
            var taskType = typeof(ITask);
            var taskHandlers = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(s => s.GetTypes())
                .Where(p => taskType.IsAssignableFrom(p) && !p.IsInterface && !p.IsAbstract);

            ITask? handler = null;
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
                AddStatus(StatusItem.StatusType.Error, $"No task handler found for task type: {job.TaskName}");
                throw new InvalidOperationException($"No task handler found for task type: {job.TaskName}");
            }

            // verify the task
            AddStatus(StatusItem.StatusType.Info, $"Verifying task ID {job.Id}...");
            TaskVerificationResult verificationResult = await handler.VerifyAsync(job.Parameters, cancellationToken);
            AddStatus(StatusItem.StatusType.Info, $"Verification result for task ID {job.Id}: {verificationResult.Status}");
            cancellationToken.ThrowIfCancellationRequested();

            // acknowledge receipt of the task
            string acknowledgeUrl = $"{Config.BaseUriPath}/clients/{Config.GetAuthValue("client_id")}/job";
            Dictionary<string, object> ackPayload = new Dictionary<string, object>
            {
                { "task_id", job.Id }
            };
            if (verificationResult.Status == TaskVerificationResult.VerificationStatus.Success)
            {
                ackPayload["status"] = QueueItemStatus.InProgress.ToString();
                ackPayload["result"] = "";
                ackPayload["error_message"] = "";
            }
            else
            {
                ackPayload["status"] = QueueItemStatus.Failed.ToString();
                ackPayload["result"] = "";
                ackPayload["error_message"] = verificationResult.Details;
            }
            AddStatus(StatusItem.StatusType.Info, $"Acknowledging task ID {job.Id} with status {ackPayload["status"]}...");
            await Common.Post<object>(acknowledgeUrl, ackPayload);
            AddStatus(StatusItem.StatusType.Info, "Acknowledgment done.");
            cancellationToken.ThrowIfCancellationRequested();

            // if verification failed, do not execute
            if (verificationResult.Status != TaskVerificationResult.VerificationStatus.Success)
            {
                AddStatus(StatusItem.StatusType.Info, $"Skipping execution of task ID {job.Id} due to verification failure.");
                return;
            }

            // execute the task
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddStatus(StatusItem.StatusType.Info, $"Executing task ID {job.Id}...");
                Dictionary<string, object> executionResult = await handler.ExecuteAsync(job.Parameters, this, cancellationToken);
                // report task completion
                ackPayload["status"] = QueueItemStatus.Submitted.ToString();
                ackPayload["result"] = executionResult.ContainsKey("response") ? executionResult["response"] : "";
                ackPayload["error_message"] = executionResult.ContainsKey("error") ? executionResult["error"] : "";
                AddStatus(StatusItem.StatusType.Info, $"Task ID {job.Id} complete.");
            }
            catch (OperationCanceledException)
            {
                AddStatus(StatusItem.StatusType.Info, $"Task ID {job.Id} cancelled.");
                return;
            }
            catch (Exception execEx)
            {
                // report task failure
                ackPayload["status"] = QueueItemStatus.Failed.ToString();
                ackPayload["result"] = "";
                ackPayload["error_message"] = execEx.Message;
                AddStatus(StatusItem.StatusType.Error, $"Task ID {job.Id} failed: {execEx.Message}");
            }
            AddStatus(StatusItem.StatusType.Info, $"Reporting completion of task ID {job.Id} with status {ackPayload["status"]}...");
            await Common.Post<object>(acknowledgeUrl, ackPayload);
        }
    }
}