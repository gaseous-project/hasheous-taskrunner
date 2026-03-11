using System.Globalization;
using hasheous_taskrunner.Classes.Tasks;
using Newtonsoft.Json;

namespace hasheous_taskrunner.Classes.Communication
{
    /// <summary>
    /// Represents a response payload posted back to the host for task lifecycle updates.
    /// </summary>
    public class ResponsePayload
    {
        /// <summary>
        /// Gets or sets the task identifier.
        /// </summary>
        [JsonProperty("task_id", NullValueHandling = NullValueHandling.Ignore)]
        public string? TaskId { get; set; }

        /// <summary>
        /// Gets or sets the task status.
        /// </summary>
        [JsonProperty("status")]
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the task result.
        /// </summary>
        [JsonProperty("result")]
        public string Result { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the error message.
        /// </summary>
        [JsonProperty("error_message")]
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>
        /// Creates a response payload while normalizing all values to strings.
        /// </summary>
        /// <param name="status">The queue item status to report.</param>
        /// <param name="result">The result payload, if any.</param>
        /// <param name="errorMessage">The error payload, if any.</param>
        /// <param name="taskId">The task identifier, if any.</param>
        /// <returns>A response payload with string-valued fields.</returns>
        public static ResponsePayload Create(QueueItemStatus status, object? result = null, object? errorMessage = null, long? taskId = null)
        {
            return new ResponsePayload
            {
                TaskId = taskId.HasValue ? taskId.Value.ToString(CultureInfo.InvariantCulture) : null,
                Status = status.ToString(),
                Result = ToPayloadString(result),
                ErrorMessage = ToPayloadString(errorMessage)
            };
        }

        private static string ToPayloadString(object? value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value is string stringValue)
            {
                return stringValue;
            }

            if (value is IFormattable formattable)
            {
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            }

            return JsonConvert.SerializeObject(value);
        }
    }
}