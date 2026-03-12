using System.Collections.Generic;
using System.Threading.Tasks;

namespace hasheous_taskrunner.Classes.Communication.Clients
{
    /// <summary>
    /// Interface for HTTP communication with external services (e.g., Ollama, future job integrations).
    /// Each external service implementation handles its own auth headers and request logic.
    /// </summary>
    public interface IExternalServiceClient
    {
        /// <summary>
        /// Sends a GET request to the external service.
        /// </summary>
        /// <typeparam name="T">The response type to deserialize.</typeparam>
        /// <param name="endpoint">The service endpoint path (e.g., "/api/version").</param>
        /// <returns>The deserialized response.</returns>
        Task<T?> GetAsync<T>(string endpoint);

        /// <summary>
        /// Sends a POST request to the external service.
        /// </summary>
        /// <typeparam name="T">The response type to deserialize.</typeparam>
        /// <param name="endpoint">The service endpoint path (e.g., "/api/generate").</param>
        /// <param name="content">The request body content.</param>
        /// <returns>The deserialized response.</returns>
        Task<T?> PostAsync<T>(string endpoint, object content);
    }
}
