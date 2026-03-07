namespace hasheous_taskrunner.Classes.Helpers
{
    /// <summary>
    /// Provides a structured way to track and report the status of task execution. This class maintains a list of status updates that can be used to log informational messages, warnings, and errors throughout the lifecycle of a task, allowing for better tracking and debugging of tasks executed by the task runner.
    /// </summary>
    public class StatusUpdate
    {
        /// <summary>
        /// Contains the status updates for the task execution process. This list is updated at each significant step of the task lifecycle, providing a trace of the task's progress and any issues encountered during verification or execution.
        /// </summary>
        public List<StatusItem> CurrentStatus { get; private set; } = new List<StatusItem> { new StatusItem(StatusItem.StatusType.Info, "Initialized") };

        /// <summary>
        /// Adds a new status update to the current status list. This method is used to log informational messages, warnings, and errors throughout the task execution process, allowing for better tracking and debugging of tasks.
        /// </summary>
        /// <param name="type">The type of status update (Info, Warning, Error).</param>
        /// <param name="message">The message associated with the status update.</param>
        public void AddStatus(StatusItem.StatusType type, string message)
        {
            var statusUpdate = new StatusItem(type, message);
            CurrentStatus.Add(statusUpdate);
        }

        /// <summary>
        /// Represents the type of status update for a task. This enumeration is used to categorize status updates as informational, warnings, or errors, allowing for better tracking and reporting of the task's execution status.
        /// </summary>
        public class StatusItem
        {
            /// <summary>
            /// Initializes a new instance of the StatusUpdate class with the specified type and message. The timestamp is automatically set to the current UTC time when the status update is created.
            /// </summary>
            /// <param name="type">The type of status update (Info, Warning, Error).</param>
            /// <param name="message">The message associated with the status update.</param>
            public StatusItem(StatusType type, string message)
            {
                Type = type;
                Message = message;
                Timestamp = DateTime.UtcNow;
            }

            /// <summary>
            /// Gets or sets the type of status update (Info, Warning, Error).
            /// </summary>
            public StatusType Type { get; private set; }

            /// <summary>
            /// Gets or sets the timestamp of when the status update was created.
            /// </summary>
            /// <remarks>
            /// The timestamp is automatically set to the current UTC time when a new status update is created, providing a chronological record of the task's execution progress.
            /// </remarks>
            public DateTime Timestamp { get; private set; }

            /// <summary>
            /// Gets or sets the message associated with the status update.
            /// </summary>
            public string Message { get; private set; }

            /// <summary>
            /// Represents the type of status update.
            /// </summary>
            public enum StatusType
            {
                /// <summary> 
                /// Indicates a general informational update about the task's progress.
                /// </summary>
                Info,
                /// <summary>
                /// Indicates a warning about a potential issue or unexpected behavior during task execution.
                /// </summary>
                Warning,
                /// <summary>
                /// Indicates an error that occurred during task execution.
                /// </summary>
                Error
            }
        }
    }
}