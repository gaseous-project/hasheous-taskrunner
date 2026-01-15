using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace hasheous_taskrunner.Classes.Communication
{
    /// <summary>
    /// Handles automatic updates for the task runner by checking GitHub releases.
    /// </summary>
    public static class Updater
    {
        private static DateTime lastUpdateCheckTime = DateTime.MinValue;
        private static readonly TimeSpan updateCheckInterval = TimeSpan.FromHours(24);
        private static readonly string GitHubApiUrl = "https://api.github.com/repos/gaseous-project/hasheous-taskrunner/releases";
        private static readonly HttpClient httpClient = new HttpClient();

        static Updater()
        {
            // Set a user agent for GitHub API requests
            httpClient.DefaultRequestHeaders.Add("User-Agent", "hasheous-taskrunner");
        }

        /// <summary>
        /// Checks for updates at startup and once per day as a background task.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static async Task CheckForUpdateIfDue()
        {
            if (!IsAutoUpdateEnabled())
            {
                return;
            }

            if (DateTime.UtcNow - lastUpdateCheckTime >= updateCheckInterval)
            {
                await CheckForAndApplyUpdate();
                lastUpdateCheckTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Performs the update check at application startup.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static async Task CheckForUpdateAtStartup()
        {
            if (!IsAutoUpdateEnabled())
            {
                return;
            }

            lastUpdateCheckTime = DateTime.UtcNow;
            await CheckForAndApplyUpdate();
        }

        /// <summary>
        /// Checks for available updates and applies them if found.
        /// </summary>
        private static async Task CheckForAndApplyUpdate()
        {
            try
            {
                var latestRelease = await GetLatestRelease();
                if (latestRelease == null)
                {
                    return;
                }

                Version currentVersion = Config.ClientVersion;
                Version latestVersion = ParseVersionFromTag(latestRelease.Tag);

                if (latestVersion > currentVersion)
                {
                    Console.WriteLine($"Update available: {currentVersion} -> {latestVersion}");
                    await DownloadAndApplyUpdate(latestRelease);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking for updates: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the latest non-prerelease GitHub release.
        /// </summary>
        /// <returns>The latest release information, or null if not found.</returns>
        private static async Task<GitHubRelease?> GetLatestRelease()
        {
            try
            {
                var response = await httpClient.GetAsync(GitHubApiUrl);
                response.EnsureSuccessStatusCode();

                var jsonContent = await response.Content.ReadAsStringAsync();
                var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (releases == null || releases.Count == 0)
                {
                    return null;
                }

                // Filter out pre-releases and find the latest stable release
                var latestStableRelease = releases
                    .Where(r => !r.Prerelease)
                    .OrderByDescending(r => ParseVersionFromTag(r.Tag))
                    .FirstOrDefault();

                return latestStableRelease;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch releases from GitHub: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Parses a version string from a GitHub release tag.
        /// </summary>
        /// <param name="tag">The GitHub release tag (e.g., "v1.2.3")</param>
        /// <returns>The parsed version.</returns>
        private static Version ParseVersionFromTag(string tag)
        {
            // Remove 'v' prefix if present
            string versionString = tag.StartsWith("v") ? tag.Substring(1) : tag;

            try
            {
                return Version.Parse(versionString);
            }
            catch
            {
                // Return a version of 0.0.0 if parsing fails
                return new Version(0, 0, 0);
            }
        }

        /// <summary>
        /// Downloads and applies the update.
        /// </summary>
        /// <param name="release">The release to download.</param>
        private static async Task DownloadAndApplyUpdate(GitHubRelease release)
        {
            try
            {
                string executableName = GetExecutableName();

                // Find the asset matching the current platform and architecture
                var asset = FindMatchingAsset(release.Assets, executableName);
                if (asset == null)
                {
                    Console.WriteLine($"No compatible release found for {executableName}");
                    return;
                }

                Console.WriteLine($"Downloading update from {asset.BrowserDownloadUrl}");

                // Download the update
                byte[] updateData = await httpClient.GetByteArrayAsync(asset.BrowserDownloadUrl);

                // Get the current executable path
                string currentExecutablePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(currentExecutablePath))
                {
                    Console.WriteLine("Error: Unable to determine current executable path.");
                    return;
                }

                // Create a backup of the current executable
                string backupPath = currentExecutablePath + ".backup";
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                File.Copy(currentExecutablePath, backupPath);

                // Write the update
                File.WriteAllBytes(currentExecutablePath, updateData);
                Console.WriteLine($"Update installed successfully: {release.Tag}");
                Console.WriteLine("Restarting application...");

                // Restart the application
                RestartApplication(currentExecutablePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error applying update: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if auto-update is enabled in the configuration.
        /// </summary>
        /// <returns>True if auto-update is enabled, false otherwise.</returns>
        private static bool IsAutoUpdateEnabled()
        {
            string enableAutoUpdate = Config.Configuration.ContainsKey("EnableAutoUpdate")
                ? Config.Configuration["EnableAutoUpdate"]
                : "true";
            return enableAutoUpdate.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Finds the matching asset for the current platform and architecture.
        /// </summary>
        /// <param name="assets">List of available assets.</param>
        /// <param name="executableName">The name of the executable for the current platform.</param>
        /// <returns>The matching asset, or null if not found.</returns>
        private static GitHubAsset? FindMatchingAsset(List<GitHubAsset> assets, string executableName)
        {
            return assets.FirstOrDefault(a =>
                a.Name.Equals(executableName, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Contains(executableName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Gets the expected executable name for the current platform and architecture.
        /// </summary>
        /// <returns>The executable name.</returns>
        private static string GetExecutableName()
        {
            string baseName = "hasheous-taskrunner";
            string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLower();

            if (OperatingSystem.IsWindows())
            {
                return $"{baseName}-win-{arch}.exe";
            }
            else if (OperatingSystem.IsLinux())
            {
                return $"{baseName}-linux-{arch}";
            }
            else if (OperatingSystem.IsMacOS())
            {
                return $"{baseName}-osx-{arch}";
            }

            return baseName;
        }

        /// <summary>
        /// Restarts the application with the new version.
        /// </summary>
        /// <param name="executablePath">The path to the updated executable.</param>
        private static void RestartApplication(string executablePath)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = false
                };

                Process.Start(psi);

                // Exit the current process
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error restarting application: {ex.Message}");
            }
        }

        /// <summary>
        /// Represents a GitHub release.
        /// </summary>
        private class GitHubRelease
        {
            [JsonPropertyName("tag_name")]
            public string Tag { get; set; } = "";

            [JsonPropertyName("prerelease")]
            public bool Prerelease { get; set; }

            [JsonPropertyName("assets")]
            public List<GitHubAsset> Assets { get; set; } = new();
        }

        /// <summary>
        /// Represents a GitHub release asset.
        /// </summary>
        private class GitHubAsset
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("browser_download_url")]
            public string BrowserDownloadUrl { get; set; } = "";
        }
    }
}
