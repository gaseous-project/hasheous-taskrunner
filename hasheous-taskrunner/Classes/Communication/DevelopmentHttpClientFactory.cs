using System.Diagnostics;
using System.Net.Http;

namespace hasheous_taskrunner.Classes.Communication
{
    internal static class DevelopmentHttpClientFactory
    {
        internal static HttpClient Create(string? baseAddress = null, TimeSpan? timeout = null)
        {
            var httpClient = new HttpClient(CreateHandler(), disposeHandler: true);

            if (!string.IsNullOrWhiteSpace(baseAddress))
            {
                httpClient.BaseAddress = new Uri(baseAddress);
            }

            if (timeout.HasValue)
            {
                httpClient.Timeout = timeout.Value;
            }

            return httpClient;
        }

        internal static HttpMessageHandler CreateHandler()
        {
            if (!IsDevelopmentMode())
            {
                return new HttpClientHandler();
            }

            return new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
        }

        internal static bool IsDevelopmentMode()
        {
            return IsDevelopmentMode(
                Debugger.IsAttached,
                Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty);
        }

        internal static bool IsDevelopmentMode(
            bool debuggerAttached,
            string? dotnetEnvironment,
            string? aspnetcoreEnvironment,
            string? processPath)
        {
            if (debuggerAttached)
            {
                return true;
            }

            if (IsDevelopmentEnvironmentName(dotnetEnvironment) || IsDevelopmentEnvironmentName(aspnetcoreEnvironment))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(processPath))
            {
                return false;
            }

            return processPath.Contains("bin/Debug", StringComparison.OrdinalIgnoreCase)
                || processPath.Contains("bin\\Debug", StringComparison.OrdinalIgnoreCase)
                || processPath.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || processPath.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDevelopmentEnvironmentName(string? environmentName)
        {
            return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
        }
    }
}