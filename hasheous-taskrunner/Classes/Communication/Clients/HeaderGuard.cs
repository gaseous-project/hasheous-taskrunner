using System;
using System.Collections.Generic;
using System.Linq;

namespace hasheous_taskrunner.Classes.Communication.Clients
{
    /// <summary>
    /// Validates that sensitive security headers are only sent to authorized endpoints.
    /// Prevents accidental auth-credential leakage to external services like Ollama.
    /// </summary>
    public class HeaderGuard
    {
        private static readonly HashSet<string> SensitiveHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "X-API-Key",
            "X-TaskWorker-API-Key",
            "Authorization"
        };

        private readonly string _authorizedHostOrigin;

        /// <summary>
        /// Initializes a new HeaderGuard for a specific host origin.
        /// </summary>
        /// <param name="authorizedHostOrigin">The base URI of the authorized host (e.g., "https://hasheous.org").</param>
        public HeaderGuard(string authorizedHostOrigin)
        {
            if (string.IsNullOrWhiteSpace(authorizedHostOrigin))
            {
                throw new ArgumentException("Authorized host origin cannot be null or empty.", nameof(authorizedHostOrigin));
            }
            _authorizedHostOrigin = normalizeOrigin(authorizedHostOrigin);
        }

        /// <summary>
        /// Checks if a request to the given URL is allowed to include sensitive headers.
        /// Sensitive headers are only allowed when the target URL is on the authorized host origin.
        /// </summary>
        /// <param name="requestUrl">The full URL the request will be sent to.</param>
        /// <returns>True if the URL is on the authorized host origin; false otherwise.</returns>
        public bool IsAuthorizedForSensitiveHeaders(string requestUrl)
        {
            if (string.IsNullOrWhiteSpace(requestUrl))
            {
                return false;
            }

            try
            {
                var requestUri = new Uri(requestUrl, UriKind.Absolute);
                var requestOrigin = normalizeOrigin(requestUri.GetLeftPart(UriPartial.Authority));
                return requestOrigin == _authorizedHostOrigin;
            }
            catch
            {
                // If URL parsing fails, reject sensitive headers
                return false;
            }
        }

        /// <summary>
        /// Filters a header dictionary to remove sensitive headers if the target is not authorized.
        /// Returns a copy of the headers with sensitive ones stripped if needed.
        /// </summary>
        /// <param name="requestUrl">The target URL for the request.</param>
        /// <param name="headers">The headers to filter.</param>
        /// <returns>Filtered headers dictionary safe for the target URL.</returns>
        public Dictionary<string, string> FilterHeadersForUrl(string requestUrl, Dictionary<string, string> headers)
        {
            if (IsAuthorizedForSensitiveHeaders(requestUrl))
            {
                return new Dictionary<string, string>(headers);
            }

            var filtered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in headers)
            {
                if (!SensitiveHeaders.Contains(kvp.Key))
                {
                    filtered[kvp.Key] = kvp.Value;
                }
            }

            // Log if sensitive headers were stripped
            var stripped = headers.Keys.Where(k => SensitiveHeaders.Contains(k)).ToList();
            if (stripped.Count > 0)
            {
                Console.WriteLine($"[WARNING] Stripped sensitive headers {string.Join(", ", stripped)} from request to non-host endpoint: {requestUrl}");
            }

            return filtered;
        }

        private static string normalizeOrigin(string uriString)
        {
            try
            {
                var uri = new Uri(uriString, UriKind.Absolute);
                return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
            }
            catch
            {
                return uriString.ToLowerInvariant();
            }
        }
    }
}
