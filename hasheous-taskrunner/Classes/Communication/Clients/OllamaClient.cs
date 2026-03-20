using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using hasheous_taskrunner.Classes.Communication;

namespace hasheous_taskrunner.Classes.Communication.Clients
{
    /// <summary>
    /// HTTP client for Ollama service (external AI service).
    /// Implements IOllamaClient to isolate external service calls from host API credentials.
    /// No auth headers are sent to Ollama—only supports basic GET/POST operations.
    /// </summary>
    public class OllamaClient : IOllamaClient
    {
        private readonly HttpClient _httpClient;

        /// <summary>
        /// Creates a new OllamaClient for the given base URL.
        /// </summary>
        /// <param name="baseUrl">The Ollama service base URL (e.g., "http://localhost:11434").</param>
        public OllamaClient(string baseUrl)
        {
            _httpClient = DevelopmentHttpClientFactory.Create(baseUrl, TimeSpan.FromMinutes(10));
        }

        /// <inheritdoc/>
        public async Task<T?> GetAsync<T>(string endpoint)
        {
            try
            {
                var response = await _httpClient.GetAsync(endpoint);
                if (!response.IsSuccessStatusCode)
                {
                    return default;
                }

                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<T>(content);
            }
            catch
            {
                return default;
            }
        }

        /// <inheritdoc/>
        public async Task<T?> PostAsync<T>(string endpoint, object content)
        {
            try
            {
                var json = JsonSerializer.Serialize(content);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(endpoint, httpContent);

                if (!response.IsSuccessStatusCode)
                {
                    return default;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<T>(responseContent);
            }
            catch
            {
                return default;
            }
        }

        /// <summary>
        /// Disposes the underlying HTTP client.
        /// </summary>
        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
