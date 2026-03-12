using System.Threading.Tasks;

namespace hasheous_taskrunner.Classes.Communication.Clients
{
    /// <summary>
    /// Interface for authenticated host API communication.
    /// All requests made through this client are guaranteed to include appropriate auth headers
    /// and never leak credentials to non-host endpoints.
    /// </summary>
    public interface IHostApiClient
    {
        /// <summary>
        /// Sends an authenticated POST request to the host API.
        /// </summary>
        /// <typeparam name="T">The response type to deserialize.</typeparam>
        /// <param name="url">The API endpoint URL (relative or absolute).</param>
        /// <param name="content">The request body content.</param>
        /// <returns>The deserialized response.</returns>
        Task<T?> PostAsync<T>(string url, object content);

        /// <summary>
        /// Sends an authenticated PUT request to the host API.
        /// </summary>
        /// <typeparam name="T">The response type to deserialize.</typeparam>
        /// <param name="url">The API endpoint URL (relative or absolute).</param>
        /// <param name="content">The request body content.</param>
        /// <returns>The deserialized response.</returns>
        Task<T?> PutAsync<T>(string url, object content);

        /// <summary>
        /// Sends an authenticated GET request to the host API.
        /// </summary>
        /// <typeparam name="T">The response type to deserialize.</typeparam>
        /// <param name="url">The API endpoint URL (relative or absolute).</param>
        /// <returns>The deserialized response.</returns>
        Task<T?> GetAsync<T>(string url);

        /// <summary>
        /// Sends an authenticated DELETE request to the host API.
        /// </summary>
        /// <param name="url">The API endpoint URL (relative or absolute).</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task DeleteAsync(string url);
    }
}
