using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
            // Set timeout to prevent hanging
            httpClient.Timeout = TimeSpan.FromSeconds(120);
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

                    // Check if running in a development environment or docker container
                    if (IsRunningInDevelopmentEnvironment())
                    {
                        Console.WriteLine("[INFO] Running in development environment. Update skipped (auto-update disabled).");
                        return;
                    }

                    if (IsRunningInDockerContainer())
                    {
                        Console.WriteLine("[INFO] Running in Docker container. Update skipped (auto-update disabled).");
                        return;
                    }

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
        /// Downloads and applies the update with security verification.
        /// </summary>
        /// <param name="release">The release to download.</param>
        private static async Task DownloadAndApplyUpdate(GitHubRelease release)
        {
            string? tempFilePath = null;
            string? backupPath = null;

            try
            {
                // filename format is hasheous-taskrunner-<platform>-<version>-<arch>[.exe]
                string executableName = "hasheous-taskrunner-";
                if (OperatingSystem.IsWindows())
                {
                    executableName += $"windows-{release.Tag.TrimStart('v')}-{RuntimeInformation.ProcessArchitecture.ToString().ToLower()}.exe";
                }
                else if (OperatingSystem.IsLinux())
                {
                    executableName += $"linux-{release.Tag.TrimStart('v')}-{RuntimeInformation.ProcessArchitecture.ToString().ToLower()}";
                }
                else if (OperatingSystem.IsMacOS())
                {
                    executableName += $"macos-{release.Tag.TrimStart('v')}-{RuntimeInformation.ProcessArchitecture.ToString().ToLower()}";
                }
                else
                {
                    Console.WriteLine("Unsupported operating system for auto-update.");
                    return;
                }

                // Find the asset matching the current platform and architecture
                var asset = FindMatchingAsset(release.Assets, executableName);
                if (asset == null)
                {
                    Console.WriteLine($"No compatible release found for {executableName}");
                    return;
                }

                // Find the checksum file
                var checksumAsset = FindMatchingAsset(release.Assets, executableName + ".sha256");
                string? expectedChecksum = null;

                if (checksumAsset != null)
                {
                    Console.WriteLine("[INFO] Checksum file found, will verify download integrity.");
                    try
                    {
                        var checksumContent = await httpClient.GetStringAsync(checksumAsset.BrowserDownloadUrl);
                        expectedChecksum = checksumContent.Split(' ', '\t', '\n', '\r')[0].Trim();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARNING] Failed to download checksum file: {ex.Message}");
                        Console.WriteLine("[WARNING] Proceeding without checksum verification (NOT RECOMMENDED).");
                    }
                }
                else
                {
                    Console.WriteLine("[WARNING] No checksum file found for this release.");
                    Console.WriteLine("[WARNING] Cannot verify download integrity (NOT RECOMMENDED).");
                }

                Console.WriteLine($"Downloading update from {asset.BrowserDownloadUrl}");

                // Download to a temporary file first
                tempFilePath = Path.Combine(Path.GetTempPath(), $"hasheous-update-{Guid.NewGuid()}");
                byte[] updateData = await httpClient.GetByteArrayAsync(asset.BrowserDownloadUrl);
                await File.WriteAllBytesAsync(tempFilePath, updateData);

                // Verify checksum if available
                if (!string.IsNullOrEmpty(expectedChecksum))
                {
                    string actualChecksum = CalculateSHA256(tempFilePath);
                    if (!actualChecksum.Equals(expectedChecksum, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[ERROR] Checksum verification FAILED!");
                        Console.WriteLine($"[ERROR] Expected: {expectedChecksum}");
                        Console.WriteLine($"[ERROR] Actual:   {actualChecksum}");
                        Console.WriteLine($"[ERROR] Update aborted for security reasons.");
                        File.Delete(tempFilePath);
                        return;
                    }
                    Console.WriteLine("[INFO] Checksum verification PASSED.");
                }

                // Get the current executable path
                string currentExecutablePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(currentExecutablePath))
                {
                    Console.WriteLine("[ERROR] Unable to determine current executable path.");
                    if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
                    return;
                }

                // Create a backup of the current executable
                backupPath = currentExecutablePath + ".backup";
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                File.Copy(currentExecutablePath, backupPath, true);
                Console.WriteLine($"[INFO] Backup created: {backupPath}");

                // On Windows, we can't overwrite a running executable directly
                // We need to use a different approach
                if (OperatingSystem.IsWindows())
                {
                    // Move the new file to a .new extension
                    string newPath = currentExecutablePath + ".new";
                    if (File.Exists(newPath))
                    {
                        File.Delete(newPath);
                    }
                    File.Move(tempFilePath, newPath);

                    // Create a batch script to replace the executable after exit
                    string batchScript = Path.Combine(Path.GetTempPath(), "hasheous-update.bat");
                    string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => $"\"{a}\""));
                    var batchLines = new[]
                    {
                        "@echo off",
                        "echo Applying update...",
                        "timeout /t 2 /nobreak > nul",
                        $"move /Y \"{newPath}\" \"{currentExecutablePath}\"",
                        "if errorlevel 1 (",
                        "    echo Update failed, restoring backup...",
                        $"    move /Y \"{backupPath}\" \"{currentExecutablePath}\"",
                        "    echo Update rolled back.",
                        "    pause",
                        "    exit /b 1",
                        ")",
                        "echo Update applied successfully.",
                        $"start \"\" \"{currentExecutablePath}\" {args}",
                        $"del \"{backupPath}\"",
                        "del \"%~f0\""
                    };
                    await File.WriteAllLinesAsync(batchScript, batchLines);

                    Console.WriteLine($"[INFO] Update staged. Restarting...");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{batchScript}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });

                    Environment.Exit(0);
                }
                else
                {
                    // On Unix-like systems, we can replace the executable while it's running
                    // The OS will keep the old file in memory for the running process
                    File.Move(tempFilePath, currentExecutablePath, true);

                    // Make it executable on Unix-like systems
                    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    {
                        Process.Start("chmod", $"+x \"{currentExecutablePath}\"")?.WaitForExit();
                    }

                    Console.WriteLine($"[INFO] Update installed successfully: {release.Tag}");
                    Console.WriteLine("[INFO] Restarting application...");

                    RestartApplication(currentExecutablePath, Environment.GetCommandLineArgs().Skip(1).ToArray());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to apply update: {ex.Message}");
                Console.WriteLine($"[ERROR] Stack trace: {ex.StackTrace}");

                // Attempt to restore backup if update failed
                if (!string.IsNullOrEmpty(backupPath) && File.Exists(backupPath))
                {
                    try
                    {
                        string currentExecutablePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                        if (!string.IsNullOrEmpty(currentExecutablePath) && File.Exists(currentExecutablePath))
                        {
                            File.Delete(currentExecutablePath);
                            File.Move(backupPath, currentExecutablePath);
                            Console.WriteLine("[INFO] Backup restored successfully.");
                        }
                    }
                    catch (Exception restoreEx)
                    {
                        Console.WriteLine($"[ERROR] Failed to restore backup: {restoreEx.Message}");
                        Console.WriteLine($"[ERROR] Manual intervention may be required.");
                        Console.WriteLine($"[ERROR] Backup location: {backupPath}");
                    }
                }
            }
            finally
            {
                // Clean up temporary file
                if (!string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                    }
                    catch { /* Best effort cleanup */ }
                }
            }
        }

        /// <summary>
        /// Calculates the SHA256 hash of a file.
        /// </summary>
        /// <param name="filePath">The path to the file.</param>
        /// <returns>The SHA256 hash as a hexadecimal string.</returns>
        private static string CalculateSHA256(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
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
        /// Checks if the application is running in a development environment.
        /// </summary>
        /// <returns>True if running in development environment, false otherwise.</returns>
        private static bool IsRunningInDevelopmentEnvironment()
        {
            // Check if debugger is attached
            if (System.Diagnostics.Debugger.IsAttached)
            {
                return true;
            }

            // Check for development environment variable
            string environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "";
            if (environment.Equals("Development", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Check if running from a project directory (bin/Debug or obj folder in path)
            string processPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (processPath.Contains("bin/Debug") || processPath.Contains("bin\\Debug") || processPath.Contains("/obj/"))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if the application is running in a Docker container.
        /// </summary>
        /// <returns>True if running in Docker container, false otherwise.</returns>
        private static bool IsRunningInDockerContainer()
        {
            // Check for /.dockerenv file (present in Docker containers)
            if (File.Exists("/.dockerenv"))
            {
                return true;
            }

            // Check for DOCKER_CONTAINER environment variable
            string dockerContainer = Environment.GetEnvironmentVariable("DOCKER_CONTAINER") ?? "";
            if (!string.IsNullOrEmpty(dockerContainer))
            {
                return true;
            }

            // Check /proc/self/cgroup for docker (Linux only)
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    string cgroupFile = "/proc/self/cgroup";
                    if (File.Exists(cgroupFile))
                    {
                        string content = File.ReadAllText(cgroupFile);
                        if (content.Contains("docker") || content.Contains("containerd") || content.Contains("kubernetes"))
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                    // If we can't read the file, assume we're not in a container
                }
            }

            return false;
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
                return $"{baseName}-windows-{arch}.exe";
            }
            else if (OperatingSystem.IsLinux())
            {
                return $"{baseName}-linux-{arch}";
            }
            else if (OperatingSystem.IsMacOS())
            {
                return $"{baseName}-macos-{arch}";
            }

            return baseName;
        }

        /// <summary>
        /// Restarts the application with the new version.
        /// </summary>
        /// <param name="executablePath">The path to the updated executable.</param>
        /// <param name="args">Command-line arguments to pass to the new process.</param>
        private static void RestartApplication(string executablePath, string[]? args = null)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = false
                };

                // Add command-line arguments if provided
                if (args != null && args.Length > 0)
                {
                    foreach (var arg in args)
                    {
                        psi.ArgumentList.Add(arg);
                    }
                }

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
