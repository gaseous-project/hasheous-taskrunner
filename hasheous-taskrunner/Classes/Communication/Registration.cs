using System.Net;
using hasheous_taskrunner.Classes;
using hasheous_taskrunner.Classes.Communication.Clients;

namespace hasheous_taskrunner.Classes.Communication
{
    public enum RegistrationHealthState
    {
        Healthy,
        Degraded,
        BlockingNewTasks
    }

    /// <summary>
    /// Provides registration utilities for the task runner communication subsystem.
    /// Add initialization and registration helpers here as needed.
    /// </summary>
    public static class Registration
    {
        private static DateTime lastRegistrationTime = DateTime.MinValue;
        private static readonly TimeSpan registrationInterval = TimeSpan.FromMinutes(30);
        private static readonly Random retryRandom = new Random();
        private static readonly object registrationStateLock = new object();
        private static readonly SemaphoreSlim recoveryLoopSemaphore = new SemaphoreSlim(1, 1);
        private const int MaxRetries = 10;
        private static RegistrationHealthState healthState = RegistrationHealthState.Degraded;
        private static Task? recoveryTask;

        /// <summary>
        /// Current registration health state.
        /// </summary>
        public static RegistrationHealthState HealthState
        {
            get
            {
                lock (registrationStateLock)
                {
                    return healthState;
                }
            }
        }

        /// <summary>
        /// Returns true when new task intake should be blocked.
        /// </summary>
        public static bool ShouldBlockNewTasks => HealthState == RegistrationHealthState.BlockingNewTasks;

        /// <summary>
        /// Initializes registration-related resources; implement registration logic here.
        /// </summary>
        /// <param name="parameters">The parameters required for registration.</param>
        /// <param name="terminateOnExhaustedRetries">If true, exits the process after max retries are exhausted.</param>
        /// <param name="forceHostRegistration">If true, performs a host registration call even when local auth state is already registered.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static async Task Initialize(
            Dictionary<string, object> parameters,
            bool terminateOnExhaustedRetries = true,
            bool forceHostRegistration = false)
        {
            if (!ShouldBlockNewTasks)
            {
                SetHealthState(RegistrationHealthState.Degraded, "Initializing registration.");
            }

            // attempt to register - keep trying until successful
            string registrationUrl = $"{Config.BaseUriPath}/clients?clientName={WebUtility.UrlEncode(Config.Configuration["ClientName"])}&clientVersion={WebUtility.UrlEncode(Config.ClientVersion.ToString())}";
            Console.WriteLine("Registering task worker with host...");
            Console.WriteLine("Registration URL: " + registrationUrl);
            if (Config.GetAuthValue("client_id") != null)
            {
                Console.WriteLine("Client is already registered with ID: " + Config.GetAuthValue("client_id"));
                if (!parameters.ContainsKey("client_id"))
                {
                    parameters.Add("client_id", Config.GetAuthValue("client_id"));
                }
            }

            // All registration calls (initial and re-registration) must use bootstrap auth.
            string apiKey = Config.Configuration["APIKey"];
            Common.HostApiClientInstance.SetBootstrapApiKey(apiKey);

            // start registration loop
            int retryCount = 0;
            while (true)
            {
                if (Common.IsRegistered() && !forceHostRegistration)
                {
                    break;
                }

                try
                {
                    retryCount++;
                    Dictionary<string, string>? registrationInfo = await Common.HostApiClientInstance.PostWithBootstrapAuthAsync<Dictionary<string, string>>(registrationUrl, parameters);
                    if (registrationInfo == null)
                    {
                        throw new InvalidOperationException("Registration response was null.");
                    }

                    // set registration info
                    Console.WriteLine("Registration completed, setting registration info...");
                    Console.WriteLine("Client ID: " + registrationInfo["client_id"]);
                    Common.SetRegistrationInfo(registrationInfo);

                    // checking registration requirements
                    if (registrationInfo.ContainsKey("required_capabilities"))
                    {
                        string requiredCapabilitiesJson = registrationInfo["required_capabilities"];
                        var requiredCapabilities = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(requiredCapabilitiesJson);
                        if (requiredCapabilities != null)
                        {
                            var capabilityResults = await Capabilities.Capabilities.CheckCapabilitiesAsync(requiredCapabilities);
                            Config.RegistrationParameters["capabilities"] = capabilityResults;
                            await UpdateRegistrationInfo();
                        }
                    }

                    SetHealthState(RegistrationHealthState.Healthy, "Registration completed successfully.");
                    lastRegistrationTime = DateTime.UtcNow;
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Registration failed: {ex.Message}");
                    if (!ShouldBlockNewTasks)
                    {
                        SetHealthState(RegistrationHealthState.Degraded, "Registration attempt failed.");
                    }
                    if (retryCount >= MaxRetries)
                    {
                        if (terminateOnExhaustedRetries)
                        {
                            Console.WriteLine($"[ERROR] Maximum retry attempts ({MaxRetries}) reached. Aborting.");
                            Environment.Exit(1);
                        }

                        throw new InvalidOperationException($"Maximum retry attempts ({MaxRetries}) reached.", ex);
                    }

                    // Exponential backoff with jitter: 1s → 2s → 4s → 8s → 16s → 32s → max 60s
                    int baseDelayMs = 1000;  // 1 second base
                    int maxDelayMs = 60000;  // 1 minute max
                    int exponentialDelay = baseDelayMs * (int)Math.Pow(2, Math.Min(retryCount - 1, 5));
                    int jitter = retryRandom.Next(0, 1000);  // Random 0-1000ms jitter
                    int delayMs = Math.Min(exponentialDelay, maxDelayMs) + jitter;

                    Console.WriteLine($"[INFO] Retrying in {delayMs}ms... (Attempt {retryCount}/{MaxRetries})");
                    await Task.Delay(delayMs);
                }
            }
        }

        /// <summary>
        /// Updates the registration information on the host for the currently registered client.
        /// </summary>
        /// <returns>A task that represents the asynchronous update operation.</returns>
        public async static Task UpdateRegistrationInfo()
        {
            if (Common.IsRegistered())
            {
                string updateUrl = $"{Config.BaseUriPath}/clients/{Config.GetAuthValue("client_id")}";
                Console.WriteLine("Updating task worker registration info...");
                try
                {
                    await Common.Put<string?>(updateUrl, Config.RegistrationParameters);
                    Console.WriteLine("Registration info update successful.");
                    lastRegistrationTime = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to update registration info: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Unregisters the client from the host and cleans up registration-related resources.
        /// </summary>
        public async static Task Unregister()
        {
            if (Common.IsRegistered())
            {
                string unregisterUrl = $"{Config.BaseUriPath}/clients/{Config.GetAuthValue("client_id")}";
                Console.WriteLine("Unregistering task worker from host...");
                try
                {
                    await Common.Delete(unregisterUrl);
                    Console.WriteLine("Unregistration successful.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to unregister: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Re-registers the task worker if the registration interval has elapsed.
        /// </summary>
        public async static Task ReRegisterIfDue()
        {
            if (ShouldBlockNewTasks)
            {
                EnsureRecoveryLoop();
                return;
            }

            if (DateTime.UtcNow - lastRegistrationTime >= registrationInterval)
            {
                Console.WriteLine("Re-registering task worker with host...");
                try
                {
                    await Initialize(
                        Config.RegistrationParameters,
                        terminateOnExhaustedRetries: false,
                        forceHostRegistration: true);
                    SetHealthState(RegistrationHealthState.Healthy, "Re-registration successful.");
                    lastRegistrationTime = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    HandleRegistrationFailure(ex.Message);
                }
            }
        }

        private static void HandleRegistrationFailure(string reason)
        {
            SetHealthState(RegistrationHealthState.BlockingNewTasks, $"Registration health degraded: {reason}");
            EnsureRecoveryLoop();
        }

        private static void EnsureRecoveryLoop()
        {
            lock (registrationStateLock)
            {
                if (recoveryTask != null && !recoveryTask.IsCompleted)
                {
                    return;
                }

                recoveryTask = Task.Run(RecoveryLoopAsync);
            }
        }

        private static async Task RecoveryLoopAsync()
        {
            if (!await recoveryLoopSemaphore.WaitAsync(0))
            {
                return;
            }

            try
            {
                int attempt = 0;
                while (ShouldBlockNewTasks)
                {
                    attempt++;
                    Console.WriteLine($"[INFO] Registration recovery attempt {attempt} started.");

                    try
                    {
                        await Initialize(
                            Config.RegistrationParameters,
                            terminateOnExhaustedRetries: false,
                            forceHostRegistration: true);
                        if (Common.IsRegistered())
                        {
                            SetHealthState(RegistrationHealthState.Healthy, "Registration recovered; resuming new task intake.");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Registration recovery attempt {attempt} failed: {ex.Message}");
                    }

                    int baseDelayMs = 1000;
                    int maxDelayMs = 60000;
                    int exponentialDelay = baseDelayMs * (int)Math.Pow(2, Math.Min(attempt - 1, 5));
                    int jitter = retryRandom.Next(0, 1000);
                    int delayMs = Math.Min(exponentialDelay, maxDelayMs) + jitter;
                    Console.WriteLine($"[INFO] Registration recovery retrying in {delayMs}ms.");
                    await Task.Delay(delayMs);
                }
            }
            finally
            {
                recoveryLoopSemaphore.Release();
            }
        }

        private static void SetHealthState(RegistrationHealthState newState, string reason)
        {
            lock (registrationStateLock)
            {
                if (healthState == newState)
                {
                    return;
                }

                Console.WriteLine($"[INFO] Registration state transition: {healthState} -> {newState}. {reason}");
                healthState = newState;
            }
        }
    }
}