using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using hasheous_taskrunner.Classes;
using Newtonsoft.Json;

namespace hasheous_taskrunner.Classes.Communication.Clients
{
    /// <summary>
    /// Authenticated HTTP client for communicating with the Hasheous host API.
    /// Manages request headers, auth tokens, and retry logic.
    /// Guarantees sensitive headers are never sent to non-host endpoints.
    /// </summary>
    public class HostApiClient : IHostApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly HeaderGuard _headerGuard;
        private readonly string _baseUri;

        // Auth state - updated as registration status changes
        private string? _bootstrapApiKey;
        private string? _clientId;
        private string? _clientApiKey;

        private const int MaxRetries = 5;

        /// <summary>
        /// Initializes a new HostApiClient.
        /// </summary>
        /// <param name="baseUri">The base URI of the host API (e.g., "https://hasheous.org").</param>
        public HostApiClient(string baseUri)
        {
            if (string.IsNullOrWhiteSpace(baseUri))
            {
                throw new ArgumentException("Base URI cannot be null or empty.", nameof(baseUri));
            }

            _baseUri = baseUri;
            _headerGuard = new HeaderGuard(baseUri);

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUri),
                Timeout = TimeSpan.FromSeconds(30)
            };

            // Set non-sensitive default headers
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "hasheous-taskrunner");
            _httpClient.DefaultRequestHeaders.Add("X-Client-Host", Config.Configuration["ClientName"] ?? "unknown-host");
            _httpClient.DefaultRequestHeaders.Add("X-Client-Version", Config.ClientVersion.ToString());
        }

        /// <summary>
        /// Sets the bootstrap API key used for initial registration requests.
        /// This header is only sent during the registration phase.
        /// </summary>
        public void SetBootstrapApiKey(string apiKey)
        {
            _bootstrapApiKey = apiKey;
        }

        /// <summary>
        /// Updates the registration state after successful registration.
        /// Switches from bootstrap X-API-Key to registered X-TaskWorker-API-Key.
        /// </summary>
        public void SetRegistrationInfo(string clientId, string clientApiKey)
        {
            _clientId = clientId;
            _clientApiKey = clientApiKey;
            _bootstrapApiKey = null;  // Clear bootstrap key once registered
        }

        /// <summary>
        /// Clears registration state (e.g., when re-registering or unregistering).
        /// </summary>
        public void ClearRegistration()
        {
            _clientId = null;
            _clientApiKey = null;
        }

        public async Task<T?> PostAsync<T>(string url, object content)
        {
            string serializedContent = JsonConvert.SerializeObject(content);
            return await ExecuteWithRetryAsync<T>(HttpMethod.Post, url, serializedContent);
        }

        public async Task<T?> PutAsync<T>(string url, object content)
        {
            string serializedContent = JsonConvert.SerializeObject(content);
            return await ExecuteWithRetryAsync<T>(HttpMethod.Put, url, serializedContent);
        }

        public async Task<T?> GetAsync<T>(string url)
        {
            return await ExecuteWithRetryAsync<T>(HttpMethod.Get, url);
        }

        public async Task DeleteAsync(string url)
        {
            await ExecuteWithRetryAsync(HttpMethod.Delete, url);
        }

        private async Task<T?> ExecuteWithRetryAsync<T>(HttpMethod method, string url, string? serializedContent = null)
        {
            int retryCount = 0;
            while (retryCount <= MaxRetries)
            {
                EnsureAuthorizedHostUrl(url, method.Method);

                try
                {
                    using var request = CreateRequest(method, url, serializedContent);
                    string authMode = ApplyAuthHeaders(request);
                    var response = await _httpClient.SendAsync(request);

                    if ((int)response.StatusCode >= 400)
                    {
                        Console.WriteLine($"[WARN] Host API {method.Method} {url} returned {(int)response.StatusCode} ({response.StatusCode}) using auth mode '{authMode}' (retry {retryCount}/{MaxRetries}).");
                    }

                    // Handle rate limiting
                    if ((int)response.StatusCode == 429)
                    {
                        Console.WriteLine($"[INFO] Host API rate-limited {method.Method} {url}; applying retry policy.");
                        if (retryCount < MaxRetries)
                        {
                            int waitSeconds = await GetRetryDelayAsync(response, retryCount);
                            await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
                            retryCount++;
                            continue;
                        }
                    }

                    response.EnsureSuccessStatusCode();

                    var resultStr = await response.Content.ReadAsStringAsync();
                    var resultObject = JsonConvert.DeserializeObject<T>(resultStr, new JsonSerializerSettings
                    {
                        Converters = { new SafeEnumConverter() }
                    });

                    return resultObject;
                }
                catch (HttpRequestException) when (retryCount < MaxRetries)
                {
                    Console.WriteLine($"[WARN] Host API transport failure for {method.Method} {url}; retrying (attempt {retryCount + 1}/{MaxRetries}).");
                    retryCount++;
                    await Task.Delay(TimeSpan.FromSeconds(1 << retryCount));  // Exponential backoff
                    continue;
                }
            }

            throw new HttpRequestException("Max retries exceeded");
        }

        private async Task ExecuteWithRetryAsync(HttpMethod method, string url, string? serializedContent = null)
        {
            int retryCount = 0;
            while (retryCount <= MaxRetries)
            {
                EnsureAuthorizedHostUrl(url, method.Method);

                try
                {
                    using var request = CreateRequest(method, url, serializedContent);
                    string authMode = ApplyAuthHeaders(request);
                    var response = await _httpClient.SendAsync(request);

                    if ((int)response.StatusCode >= 400)
                    {
                        Console.WriteLine($"[WARN] Host API {method.Method} {url} returned {(int)response.StatusCode} ({response.StatusCode}) using auth mode '{authMode}' (retry {retryCount}/{MaxRetries}).");
                    }

                    // Handle rate limiting
                    if ((int)response.StatusCode == 429)
                    {
                        Console.WriteLine($"[INFO] Host API rate-limited {method.Method} {url}; applying retry policy.");
                        if (retryCount < MaxRetries)
                        {
                            int waitSeconds = await GetRetryDelayAsync(response, retryCount);
                            await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
                            retryCount++;
                            continue;
                        }
                    }

                    response.EnsureSuccessStatusCode();
                    return;
                }
                catch (HttpRequestException) when (retryCount < MaxRetries)
                {
                    Console.WriteLine($"[WARN] Host API transport failure for {method.Method} {url}; retrying (attempt {retryCount + 1}/{MaxRetries}).");
                    retryCount++;
                    await Task.Delay(TimeSpan.FromSeconds(1 << retryCount));  // Exponential backoff
                    continue;
                }
            }

            throw new HttpRequestException("Max retries exceeded");
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string url, string? serializedContent)
        {
            var request = new HttpRequestMessage(method, url);
            if (serializedContent != null)
            {
                request.Content = new StringContent(serializedContent, Encoding.UTF8, "application/json");
            }

            return request;
        }

        private void EnsureAuthorizedHostUrl(string url, string method)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new InvalidOperationException($"HostApiClient received an empty URL for {method} request.");
            }

            // Relative URLs are always resolved against configured host base URI.
            if (Uri.TryCreate(url, UriKind.Relative, out _))
            {
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri))
            {
                throw new InvalidOperationException($"HostApiClient received an invalid URL for {method} request: {url}");
            }

            if (!_headerGuard.IsAuthorizedForSensitiveHeaders(absoluteUri.ToString()))
            {
                throw new InvalidOperationException(
                    $"Blocked {method} request to non-host endpoint: {absoluteUri}. " +
                    $"Configured host origin: {_baseUri}");
            }
        }

        private string ApplyAuthHeaders(HttpRequestMessage request)
        {
            // Add auth header per-request to avoid shared-header races across concurrent requests.
            if (!string.IsNullOrEmpty(_bootstrapApiKey))
            {
                request.Headers.Add("X-API-Key", _bootstrapApiKey);
                return "bootstrap";
            }
            else if (!string.IsNullOrEmpty(_clientApiKey))
            {
                request.Headers.Add("X-TaskWorker-API-Key", _clientApiKey);
                return "worker";
            }
            else
            {
                // This indicates a logic error - requests should not be made without auth
                throw new InvalidOperationException(
                    "HostApiClient cannot inject auth headers: no bootstrap API key or registered client API key set. " +
                    "This indicates registration was not completed successfully. " +
                    "Bootstrap key: " + (_bootstrapApiKey ?? "<null>") + ", " +
                    "Client API key: " + (_clientApiKey ?? "<null>") + ", " +
                    "Client ID: " + (_clientId ?? "<null>"));
            }
        }

        private async Task<int> GetRetryDelayAsync(HttpResponseMessage response, int retryCount)
        {
            int waitSeconds = 30;

            // Check for Retry-After header
            if (response.Headers.RetryAfter != null)
            {
                if (response.Headers.RetryAfter.Delta != null)
                {
                    waitSeconds = (int)response.Headers.RetryAfter.Delta.Value.TotalSeconds;
                }
                else if (response.Headers.RetryAfter.Date != null)
                {
                    waitSeconds = (int)(response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
                    if (waitSeconds < 0) waitSeconds = 30;
                }
            }
            else
            {
                // Exponential backoff: 30s, 60s, 120s, 240s, 480s
                waitSeconds = 30 * (int)Math.Pow(2, retryCount);
            }

            return waitSeconds;
        }

        /// <summary>
        /// Custom JSON converter for safe enum deserialization.
        /// </summary>
        private class SafeEnumConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                var t = Nullable.GetUnderlyingType(objectType) ?? objectType;
                return t.IsEnum;
            }

            public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
            {
                var isNullable = Nullable.GetUnderlyingType(objectType) != null;
                var enumType = Nullable.GetUnderlyingType(objectType) ?? objectType;
                try
                {
                    if (reader.TokenType == JsonToken.String)
                    {
                        var enumText = reader.Value?.ToString();
                        if (Enum.TryParse(enumType, enumText, true, out var enumValue))
                        {
                            return enumValue;
                        }
                    }
                    else if (reader.TokenType == JsonToken.Integer)
                    {
                        var intValue = Convert.ToInt32(reader.Value);
                        if (Enum.IsDefined(enumType, intValue))
                        {
                            return Enum.ToObject(enumType, intValue);
                        }
                    }
                }
                catch { }

                var names = Enum.GetNames(enumType);
                if (names.Contains("Unknown"))
                {
                    return Enum.Parse(enumType, "Unknown");
                }
                return isNullable ? null : Activator.CreateInstance(enumType);
            }

            public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
            {
                writer.WriteValue(value?.ToString());
            }
        }
    }
}
