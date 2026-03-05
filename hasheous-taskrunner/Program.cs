using System.Diagnostics;
using System.Net;
using hasheous_taskrunner.Classes;
using hasheous_taskrunner.Classes.Communication;

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

// Load configuration
Config.LoadConfiguration();

// Check for updates at startup
Console.WriteLine("Checking for updates...");
await hasheous_taskrunner.Classes.Communication.Updater.CheckForUpdateAtStartup();

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
    Console.CancelKeyPress += (sender, e) =>
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

            // Check for updates if due
            try
            {
                await hasheous_taskrunner.Classes.Communication.Updater.CheckForUpdateIfDue();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Update check failed: {ex.Message}");
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
