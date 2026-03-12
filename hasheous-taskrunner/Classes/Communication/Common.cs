using hasheous_taskrunner.Classes;
using hasheous_taskrunner.Classes.Communication.Clients;

namespace hasheous_taskrunner.Classes.Communication
{
    /// <summary>
    /// Common communication utilities and helpers.
    /// Provides authentication-aware HTTP communication with the Hasheous host.
    /// Uses HostApiClient to isolate host API calls from external service communication.
    /// </summary>
    public static class Common
    {
        /// <summary>
        /// Static HostApiClient instance for all host API communication.
        /// Accessible to Registration for bootstrap phase and to callers for authenticated requests.
        /// </summary>
        private static HostApiClient _hostApiClient;

        /// <summary>
        /// Gets the static HostApiClient instance. Used for both bootstrap registration and authenticated requests.
        /// </summary>
        internal static HostApiClient HostApiClientInstance
        {
            get
            {
                if (_hostApiClient == null)
                {
                    // Lazy initialization in case Common is referenced before Config is loaded
                    string hostAddress = Config.Configuration["HostAddress"] ?? "http://localhost:5000";
                    _hostApiClient = new HostApiClient(hostAddress);
                }
                return _hostApiClient;
            }
        }

        static Common()
        {
            // Initialize HostApiClient with host address
            string hostAddress = Config.Configuration["HostAddress"] ?? "http://localhost:5000";
            _hostApiClient = new HostApiClient(hostAddress);
        }

        private static Dictionary<string, string> registrationInfo = new Dictionary<string, string>();

        /// <summary>
        /// Determines whether the task runner is registered with the host by checking for both
        /// the client identifier and the client API key in the registration info dictionary.
        /// </summary>
        public static bool IsRegistered()
        {
            return registrationInfo.ContainsKey("client_id") && registrationInfo.ContainsKey("client_api_key");
        }

        /// <summary>
        /// Sets registration information for the task runner host.
        /// Updates HostApiClient with the new API key.
        /// </summary>
        /// <param name="info">A dictionary containing registration keys and values (for example "client_id" and "client_api_key").</param>
        public static void SetRegistrationInfo(Dictionary<string, string> info)
        {
            registrationInfo = info;

            if (!registrationInfo.ContainsKey("client_id"))
            {
                throw new InvalidOperationException("Registration response missing required 'client_id' key. Available keys: " + string.Join(", ", registrationInfo.Keys));
            }

            Config.SetAuthValue("client_id", registrationInfo["client_id"]);

            // Update HostApiClient with registered API key
            // Try multiple key name variations to handle API versioning
            string? apiKey = null;
            foreach (var keyName in new[] { "client_api_key", "api_key", "token", "access_token" })
            {
                if (registrationInfo.ContainsKey(keyName))
                {
                    apiKey = registrationInfo[keyName];
                    break;
                }
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                throw new InvalidOperationException(
                    "Registration response missing API key. Expected one of: 'client_api_key', 'api_key', 'token', or 'access_token'. " +
                    "Available keys: " + string.Join(", ", registrationInfo.Keys));
            }

            HostApiClientInstance.SetRegistrationInfo(registrationInfo["client_id"], apiKey);
            Console.WriteLine($"[INFO] HostApiClient activated with client_id={registrationInfo["client_id"]} and api_key=(***)");
        }

        /// <summary>
        /// Clears registration information.
        /// </summary>
        public static void ClearRegistration()
        {
            registrationInfo.Clear();
            HostApiClientInstance.ClearRegistration();
        }

        /// <summary>
        /// Performs an HTTP POST to the specified URL with the provided content and deserializes the response to <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The expected response type.</typeparam>
        /// <param name="url">The request URL (relative or absolute).</param>
        /// <param name="contentValue">The object to send as the request body.</param>
        /// <returns>A task that returns the deserialized response of type <typeparamref name="T"/>.</returns>
        public static async Task<T> Post<T>(string url, object contentValue)
        {
            if (!IsRegistered())
            {
                throw new InvalidOperationException("Task runner is not registered. Cannot perform host API POST request.");
            }
            return await HostApiClientInstance.PostAsync<T>(url, contentValue);
        }

        /// <summary>
        /// Performs an HTTP PUT to the specified URL with the provided content and deserializes the response to <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The expected response type.</typeparam>
        /// <param name="url">The request URL (relative or absolute).</param>
        /// <param name="contentValue">The object to send as the request body.</param>
        /// <returns>A task that returns the deserialized response of type <typeparamref name="T"/>.</returns>
        public static async Task<T> Put<T>(string url, object contentValue)
        {
            if (!IsRegistered())
            {
                throw new InvalidOperationException("Task runner is not registered. Cannot perform host API PUT request.");
            }
            return await HostApiClientInstance.PutAsync<T>(url, contentValue);
        }

        /// <summary>
        /// Performs an HTTP GET to the specified URL and deserializes the response to <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The expected response type.</typeparam>
        /// <param name="url">The request URL (relative or absolute).</param>
        /// <returns>A task that returns the deserialized response of type <typeparamref name="T"/>.</returns>
        public static async Task<T> Get<T>(string url)
        {
            if (!IsRegistered())
            {
                throw new InvalidOperationException("Task runner is not registered. Cannot perform host API GET request.");
            }
            return await HostApiClientInstance.GetAsync<T>(url);
        }

        /// <summary>
        /// Performs an HTTP DELETE to the specified URL.
        /// </summary>
        /// <param name="url">The request URL (relative or absolute).</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static async Task Delete(string url)
        {
            if (!IsRegistered())
            {
                throw new InvalidOperationException("Task runner is not registered. Cannot perform host API DELETE request.");
            }
            await HostApiClientInstance.DeleteAsync(url);
        }

        /// <summary>
        /// Sets the bootstrap API key for initial registration.
        /// </summary>
        /// <param name="apiKey">The bootstrap API key.</param>
        public static void SetBootstrapApiKey(string apiKey)
        {
            HostApiClientInstance.SetBootstrapApiKey(apiKey);
        }
    }
}