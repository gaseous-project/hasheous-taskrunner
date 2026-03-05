using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using hasheous_taskrunner.Classes;
using hasheous_taskrunner.Classes.Communication;
using Console = hasheous_taskrunner.Classes.Logging;

// Multi-client helper functions
static string GetBaseClientName()
{
    var args = Environment.GetCommandLineArgs();
    for (int i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--ClientName", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            return args[i + 1];
        }
    }

    string? envValue = Environment.GetEnvironmentVariable("ClientName");
    if (!string.IsNullOrEmpty(envValue))
    {
        return envValue;
    }

    return Dns.GetHostName();
}

static int? DetectClientsCount()
{
    var args = Environment.GetCommandLineArgs();

    // Check command-line arguments first
    for (int i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--clients", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            if (int.TryParse(args[i + 1], out int count) && count > 1)
            {
                return count;
            }
        }
    }

    // Check config file for "Clients" key
    string baseClientName = GetBaseClientName();
    string configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".hasheous-taskrunner",
        baseClientName,
        "config.json"
    );

    if (File.Exists(configPath))
    {
        try
        {
            string configJson = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<Dictionary<string, string>>(configJson);
            if (config != null && config.ContainsKey("Clients"))
            {
                if (int.TryParse(config["Clients"], out int count) && count > 1)
                {
                    return count;
                }
            }
        }
        catch
        {
            // If config file can't be read, proceed with single client
        }
    }

    return null;
}

static async Task RunMultiClientSupervisor(int clientCount)
{
    string baseClientName = GetBaseClientName();
    var childProcesses = new List<Process>();
    var cts = new CancellationTokenSource();

    Console.WriteLine($"[INFO] Launching {clientCount} client instances with base name '{baseClientName}'...");
    Console.WriteLine("");

    try
    {
        // Spawn all child processes
        for (int i = 1; i <= clientCount; i++)
        {
            string childClientName = i == 1 ? baseClientName : $"{baseClientName}_{i}";
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = false
            };

            // If launched via `dotnet <app>.dll`, include the dll path for child processes.
            var originalArgs = Environment.GetCommandLineArgs();
            string? processPath = Environment.ProcessPath;
            string? processFileName = string.IsNullOrEmpty(processPath) ? null : Path.GetFileName(processPath);
            bool isDotnetHost = string.Equals(processFileName, "dotnet", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(processFileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase);
            string? dotnetAppDll = null;
            if (isDotnetHost)
            {
                string? entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(entryAssemblyPath) &&
                    entryAssemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    dotnetAppDll = entryAssemblyPath;
                }
                else
                {
                    dotnetAppDll = originalArgs.FirstOrDefault(arg =>
                        arg.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrEmpty(dotnetAppDll))
                {
                    psi.ArgumentList.Add(dotnetAppDll);
                }
            }

            // Pass --ClientName with numbered suffix
            psi.ArgumentList.Add("--ClientName");
            psi.ArgumentList.Add(childClientName);

            // Pass all other command-line arguments (except --clients)
            var args = Environment.GetCommandLineArgs();
            for (int j = 1; j < args.Length; j++)
            {
                if (string.Equals(args[j], "--clients", StringComparison.OrdinalIgnoreCase))
                {
                    j++; // Skip the count value
                    continue;
                }
                if (string.Equals(args[j], "--ClientName", StringComparison.OrdinalIgnoreCase))
                {
                    j++; // Skip the next value (original ClientName)
                    continue;
                }
                if (!string.IsNullOrEmpty(dotnetAppDll) &&
                    string.Equals(args[j], dotnetAppDll, StringComparison.OrdinalIgnoreCase))
                {
                    // Already added the .dll path for dotnet-hosted execution.
                    continue;
                }
                psi.ArgumentList.Add(args[j]);
            }

            // Set environment variable to mark this as a child process
            psi.Environment["HASHEOUS_IS_CHILD_CLIENT"] = "true";

            string? debugChildLaunch = Environment.GetEnvironmentVariable("HASHEOUS_DEBUG_CHILD_LAUNCH");
            if (string.Equals(debugChildLaunch, "true", StringComparison.OrdinalIgnoreCase) || debugChildLaunch == "1")
            {
                string launchArgs = string.Join(" ", psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                Console.WriteLine($"[DEBUG] Child launch: {psi.FileName} {launchArgs}");
            }

            try
            {
                var process = Process.Start(psi);
                if (process != null)
                {
                    childProcesses.Add(process);
                    Console.WriteLine($"[INFO] Spawned client instance: {childClientName} (PID: {process.Id})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to spawn client {i}: {ex.Message}");
            }
        }

        Console.WriteLine("");
        Console.WriteLine("All client instances launched. Press Ctrl+C to terminate all clients.");
        Console.WriteLine("");

        // Set up Ctrl+C handler for supervisor
        System.Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Wait for all child processes to complete or cancellation
        var waitTasks = childProcesses.Select(p => Task.Run(() => p.WaitForExit())).ToList();
        var completionTask = Task.WhenAll(waitTasks);

        // Create a task that monitors for cancellation
        var cancellationTask = Task.Delay(Timeout.Infinite, cts.Token);

        try
        {
            await Task.WhenAny(completionTask, cancellationTask);
        }
        catch (OperationCanceledException)
        {
            // Expected when Ctrl+C is pressed
        }

        // If cancellation was requested, terminate all child processes
        if (cts.Token.IsCancellationRequested)
        {
            Console.WriteLine("");
            Console.WriteLine("Terminating all client instances...");

            foreach (var process in childProcesses)
            {
                if (!process.HasExited)
                {
                    try
                    {
                        // Send termination signal
                        if (OperatingSystem.IsWindows())
                        {
                            // On Windows, use taskkill for graceful termination
                            using (var killProcess = Process.Start(new ProcessStartInfo
                            {
                                FileName = "taskkill",
                                Arguments = $"/PID {process.Id} /T",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            }))
                            {
                                killProcess?.WaitForExit(5000);
                            }
                        }
                        else
                        {
                            // On Linux/macOS, use kill command
                            using (var killProcess = Process.Start(new ProcessStartInfo
                            {
                                FileName = "kill",
                                Arguments = $"-TERM {process.Id}",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            }))
                            {
                                killProcess?.WaitForExit(5000);
                            }
                        }

                        // Wait a bit for graceful shutdown
                        if (!process.WaitForExit(3000))
                        {
                            // Force kill if still running
                            process.Kill();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Failed to terminate client process {process.Id}: {ex.Message}");
                    }
                }
            }

            Console.WriteLine("All client instances terminated.");
        }
        else
        {
            // All child processes exited naturally
            Console.WriteLine("");
            Console.WriteLine("All client instances have exited.");
        }
    }
    finally
    {
        cts.Dispose();
        foreach (var process in childProcesses)
        {
            process?.Dispose();
        }
    }
}

// Set up global exception handler for unhandled exceptions
AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
{
    var exception = eventArgs.ExceptionObject as Exception;
    Console.WriteLine("");
    Console.WriteLine("═══════════════════════════════════════════════════════");
    Console.WriteLine("[FATAL] Unhandled exception caught by global handler:");
    Console.WriteLine("═══════════════════════════════════════════════════════");
    Console.WriteLine($"Exception Type: {exception?.GetType().Name ?? "Unknown"}");
    Console.WriteLine($"Message: {exception?.Message ?? "No message available"}");
    Console.WriteLine($"Stack Trace:\n{exception?.StackTrace ?? "No stack trace available"}");

    if (exception?.InnerException != null)
    {
        Console.WriteLine($"\nInner Exception: {exception.InnerException.GetType().Name}");
        Console.WriteLine($"Inner Message: {exception.InnerException.Message}");
    }

    Console.WriteLine($"\nIs Terminating: {eventArgs.IsTerminating}");
    Console.WriteLine("═══════════════════════════════════════════════════════");

    // Attempt cleanup if registered
    if (eventArgs.IsTerminating && hasheous_taskrunner.Classes.Communication.Common.IsRegistered())
    {
        try
        {
            Console.WriteLine("[INFO] Attempting emergency unregistration...");
            // Use synchronous version to avoid async issues in unhandled exception handler
            hasheous_taskrunner.Classes.Communication.Registration.Unregister().GetAwaiter().GetResult();
            Console.WriteLine("[INFO] Emergency unregistration completed.");
        }
        catch (Exception cleanupEx)
        {
            Console.WriteLine($"[ERROR] Failed to unregister during cleanup: {cleanupEx.Message}");
        }
    }

    Console.WriteLine("");
    Console.WriteLine("Application will now terminate.");
};

// Multi-client supervisor mode (if --clients or config specifies multiple clients)
int? clientCount = DetectClientsCount();
if (clientCount.HasValue && clientCount.Value > 1)
{
    await RunMultiClientSupervisor(clientCount.Value);
    return;
}

// Load configuration
Config.LoadConfiguration();

// Check for updates at startup (skip if this is a child client)
bool isChildClient = Environment.GetEnvironmentVariable("HASHEOUS_IS_CHILD_CLIENT") == "true";
if (!isChildClient)
{
    Console.WriteLine("Checking for updates...");
    await hasheous_taskrunner.Classes.Communication.Updater.CheckForUpdateAtStartup();
}

// Register the task runner with the service host
await hasheous_taskrunner.Classes.Communication.Registration.Initialize(Config.RegistrationParameters);

if (hasheous_taskrunner.Classes.Communication.Common.IsRegistered())
{
    Console.WriteLine("");
    Console.WriteLine("Task worker is now registered and ready to receive tasks.");

    // Start the task processing loop
    Console.WriteLine("");
    Console.WriteLine("Starting task processing loop... (press Ctrl+C to exit)");

    var cts = new CancellationTokenSource();
    System.Console.CancelKeyPress += (sender, e) =>
    {
        e.Cancel = true;  // Prevent immediate termination
        cts.Cancel();     // Signal the loop to break
    };

    // Main processing loop - each task run in the loop should be run as a background task
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            // Ensure registration is up to date if due
            try
            {
                await hasheous_taskrunner.Classes.Communication.Registration.ReRegisterIfDue();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Re-registration failed: {ex.Message}");
            }

            // Send heartbeat if due
            try
            {
                await hasheous_taskrunner.Classes.Communication.Heartbeat.SendHeartbeatIfDue();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Heartbeat failed: {ex.Message}");
            }

            // Check for updates if due (skip if this is a child client)
            if (!isChildClient)
            {
                try
                {
                    await hasheous_taskrunner.Classes.Communication.Updater.CheckForUpdateIfDue();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Update check failed: {ex.Message}");
                }
            }

            // Fetch and execute tasks if due
            if (!hasheous_taskrunner.Classes.Communication.Tasks.IsRunningTask)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await hasheous_taskrunner.Classes.Communication.Tasks.FetchAndExecuteTasksIfDue(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("[INFO] Task execution was cancelled.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Error in background task: {ex.Message}");
                    }
                });
            }

            try
            {
                // Wait 10 seconds before next iteration
                await Task.Delay(10000, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Unexpected error in main loop: {ex.Message}");
            // Continue running despite the error
        }
    }

    // Cleanup: OS task kill commands
    Console.WriteLine("Cancellation requested. Cleaning up...");

    if (OperatingSystem.IsWindows())
    {
        // Windows: kill any spawned processes (example: taskkill /F /IM processname.exe)
        // Customize with actual process names or PIDs as needed
    }
    else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
    {
        // Linux/macOS: kill any spawned processes (example: kill -9 <PID>)
        // Customize with actual PIDs as needed
    }

    Console.WriteLine("Task worker is shutting down...");
    await hasheous_taskrunner.Classes.Communication.Registration.Unregister();
}
else
{
    Console.WriteLine("Task worker registration failed. Exiting.");
    return;
}

// Keep the console window open
Console.WriteLine("Task worker has stopped.");
