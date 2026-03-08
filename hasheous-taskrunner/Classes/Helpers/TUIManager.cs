using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using hasheous_taskrunner.Classes.Capabilities;
using hasheous_taskrunner.Classes.Communication;

namespace hasheous_taskrunner.Classes.Helpers
{
    /// <summary>
    /// Manages the Terminal User Interface (TUI) display for task runner status.
    /// </summary>
    public class TUIManager
    {
        private static readonly object _lock = new object();
        private static Process? _currentProcess;
        private static DateTime _lastCpuCheck = DateTime.MinValue;
        private static double _cpuUsage = 0.0;
        private static TextWriter? _directOutput;
        private static bool _initialized;
        private static DateTime _lastFullRedraw = DateTime.MinValue;

        private const int StdOutputHandle = -11;
        private const uint EnableVirtualTerminalProcessing = 0x0004;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        public static bool TryEnableVirtualTerminal()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return true;
            }

            try
            {
                IntPtr handle = GetStdHandle(StdOutputHandle);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    return false;
                }

                if (!GetConsoleMode(handle, out uint mode))
                {
                    return false;
                }

                mode |= EnableVirtualTerminalProcessing;
                return SetConsoleMode(handle, mode);
            }
            catch
            {
                return false;
            }
        }

        public static void Initialize()
        {
            lock (_lock)
            {
                if (_initialized)
                {
                    return;
                }

                _directOutput = ConsoleCapture.Instance?.OriginalOutput ?? Console.Out;
                // Switch to alternate buffer to avoid polluting scrollback
                _directOutput.Write("\x1b[?1049h\x1b[H");
                _directOutput.Flush();
                _initialized = true;
            }
        }

        public static void Shutdown()
        {
            lock (_lock)
            {
                if (!_initialized || _directOutput == null)
                {
                    return;
                }

                // Restore main buffer and cursor
                _directOutput.Write("\x1b[0m\x1b[?25h\x1b[?1049l");
                _directOutput.Flush();
                _initialized = false;
            }
        }

        /// <summary>
        /// Renders the TUI to the console.
        /// </summary>
        public static void Render()
        {
            lock (_lock)
            {
                try
                {
                    if (!_initialized)
                    {
                        Initialize();
                    }

                    Console.CursorVisible = false;

                    int width = Console.WindowWidth;
                    int height = Console.WindowHeight;

                    // Full redraw every 2 seconds to keep static borders intact
                    var now = DateTime.UtcNow;
                    if (now - _lastFullRedraw >= TimeSpan.FromSeconds(2))
                    {
                        // Re-enter alternate buffer and clear to avoid scrollback pollution
                        _directOutput.Write("\x1b[?1049h\x1b[H\x1b[2J");
                        _lastFullRedraw = now;
                    }
                    else
                    {
                        _directOutput.Write("\x1b[H");
                    }

                    // Top border with title and status
                    DrawTopBorder(width);

                    // Handle tiny terminal heights without scrolling
                    if (height < 8)
                    {
                        DrawTopBorder(width);
                        if (height > 2)
                        {
                            int filler = height - 2;
                            for (int i = 0; i < filler - 1; i++)
                            {
                                DrawEmptyLine(width, "Window too small");
                            }
                        }
                        DrawBottomBorder(width);
                        _directOutput.Flush();
                        return;
                    }

                    int activeTaskCount = Communication.Tasks.ActiveTaskExecutors.Count;
                    int availableWithConsole = height - 3; // top + separator + bottom
                    int availableNoConsole = height - 2; // top + bottom
                    int maxLogsPerTask = 2;
                    int consoleLogHeight = 0;
                    bool showConsoleLog = false;
                    int taskPaneHeight = availableNoConsole;

                    foreach (int logsPerTask in new[] { 3, 2, 1, 0 })
                    {
                        int requiredTaskHeight = 1 + (activeTaskCount * (1 + logsPerTask));
                        int maxConsoleHeight = availableWithConsole - requiredTaskHeight;
                        int candidateConsole = Math.Min(7, Math.Max(0, maxConsoleHeight));
                        if (candidateConsole <= 2)
                        {
                            candidateConsole = 0;
                        }

                        int candidateTaskPaneHeight = candidateConsole > 0
                            ? availableWithConsole - candidateConsole
                            : availableNoConsole;

                        if (candidateTaskPaneHeight >= requiredTaskHeight || activeTaskCount == 0)
                        {
                            maxLogsPerTask = logsPerTask;
                            consoleLogHeight = candidateConsole;
                            showConsoleLog = candidateConsole > 0;
                            taskPaneHeight = Math.Max(2, candidateTaskPaneHeight);
                            break;
                        }
                    }

                    // Task list pane
                    DrawTaskPane(width, taskPaneHeight, maxLogsPerTask);

                    if (showConsoleLog)
                    {
                        // Separator border
                        DrawSeparatorBorder(width);

                        // Console log pane
                        DrawConsolePane(width, consoleLogHeight);
                    }

                    // Bottom border with task count and capabilities
                    DrawBottomBorder(width);

                    _directOutput.Flush();
                }
                catch (Exception)
                {
                    // Ignore rendering errors (e.g., if console is resized during render)
                }
            }
        }

        private static void DrawTopBorder(int width)
        {
            if (_directOutput == null) return;

            // App title
            string title = " Hasheous Task Runner ";

            // App version
            title += $"| Version: {Config.ClientVersion} ";

            // Server URL
            string serverUrl = Config.Configuration.ContainsKey("HostAddress") ? Config.Configuration["HostAddress"] : "unknown";
            title += $"| Connected to: {serverUrl} ";

            // Date, time, and CPU
            UpdateCpuUsage();
            string rightInfo = $" {DateTime.Now:yyyy-MM-dd HH:mm:ss} | CPU: {_cpuUsage:F1}% ";

            // Calculate padding
            int padding = width - title.Length - rightInfo.Length - 2;
            if (padding < 0) padding = 0;

            // Draw top border with cyan color
            _directOutput.Write("\x1b[36m╔");
            _directOutput.Write(title);
            _directOutput.Write(new string('═', padding));
            _directOutput.Write(rightInfo);
            _directOutput.WriteLine("╗\x1b[0m");
        }

        private static void DrawBottomBorder(int width)
        {
            if (_directOutput == null) return;

            // Get task count
            var activeTasks = Communication.Tasks.ActiveTaskExecutors;
            string leftInfo = $" Tasks: {activeTasks.Count}/{Communication.Tasks.MaxConcurrentTasks} ";

            // Get capabilities
            var capabilities = GetCapabilitiesString();
            string rightInfo = $" Capabilities: {capabilities} ";

            // Calculate padding
            int padding = width - leftInfo.Length - rightInfo.Length - 2;
            if (padding < 0)
            {
                // Truncate capabilities if needed
                int availableSpace = width - leftInfo.Length - 20;
                if (availableSpace > 0)
                {
                    rightInfo = $" Capabilities: {capabilities.Substring(0, Math.Min(capabilities.Length, availableSpace))}... ";
                    padding = width - leftInfo.Length - rightInfo.Length - 2;
                    if (padding < 0) padding = 0;
                }
                else
                {
                    rightInfo = " ";
                    padding = width - leftInfo.Length - 3;
                    if (padding < 0) padding = 0;
                }
            }

            _directOutput.Write("\x1b[36m╚");
            _directOutput.Write(leftInfo);
            _directOutput.Write(new string('═', padding));
            _directOutput.Write(rightInfo);
            _directOutput.Write("╝\x1b[0m");
        }

        private static void DrawTaskPane(int width, int paneHeight, int maxLogsPerTask)
        {
            if (_directOutput == null) return;

            int currentLine = 0;
            var activeTasks = Communication.Tasks.ActiveTaskExecutors
                .OrderBy(kvp => kvp.Key)
                .ToList();

            // Header
            DrawSectionHeader(width, "Active Tasks");
            currentLine++;

            if (activeTasks.Count == 0)
            {
                if (currentLine < paneHeight)
                {
                    DrawEmptyLine(width, "No active tasks");
                    currentLine++;
                }

                while (currentLine < paneHeight)
                {
                    DrawEmptyLine(width, "");
                    currentLine++;
                }
                return;
            }

            int remainingLines = paneHeight - currentLine;
            if (remainingLines <= 0)
            {
                return;
            }

            int taskCount = Math.Min(activeTasks.Count, remainingLines);
            int logLinesTotal = remainingLines - taskCount;
            int desiredPerTask = taskCount > 0 ? logLinesTotal / taskCount : 0;
            int baseLogsPerTask;
            if (desiredPerTask >= 3)
            {
                baseLogsPerTask = Math.Min(3, maxLogsPerTask);
            }
            else if (desiredPerTask >= 2)
            {
                baseLogsPerTask = Math.Min(2, maxLogsPerTask);
            }
            else
            {
                baseLogsPerTask = Math.Min(desiredPerTask, maxLogsPerTask);
            }

            int remainingForExtras = logLinesTotal - (baseLogsPerTask * taskCount);
            if (remainingForExtras < 0)
            {
                remainingForExtras = 0;
            }
            int extraLogs = baseLogsPerTask >= maxLogsPerTask ? 0 : Math.Min(taskCount, remainingForExtras);

            for (int i = 0; i < taskCount; i++)
            {
                if (currentLine >= paneHeight) break;

                var kvp = activeTasks[i];
                DrawTaskLine(width, kvp.Key, kvp.Value);
                currentLine++;

                int logLinesForTask = baseLogsPerTask + (i < extraLogs ? 1 : 0);
                if (logLinesForTask <= 0) continue;

                var statusLines = kvp.Value.CurrentStatus
                    .OrderByDescending(s => s.Timestamp)
                    .Take(logLinesForTask)
                    .OrderBy(s => s.Timestamp)
                    .ToList();

                foreach (var status in statusLines)
                {
                    if (currentLine >= paneHeight) break;
                    DrawTaskStatusLine(width, status);
                    currentLine++;
                }
            }

            if (activeTasks.Count > taskCount && currentLine < paneHeight)
            {
                DrawEmptyLine(width, $"... {activeTasks.Count - taskCount} more task(s)");
                currentLine++;
            }

            while (currentLine < paneHeight)
            {
                DrawEmptyLine(width, "");
                currentLine++;
            }
        }

        private static void DrawConsolePane(int width, int paneHeight)
        {
            if (_directOutput == null) return;

            int currentLine = 0;

            // Header
            DrawSectionHeader(width, "Console Log");
            currentLine++;

            // Get recent log lines
            int logLinesToShow = paneHeight - 1;
            var consoleLines = ConsoleCapture.Instance?.GetRecentLines(logLinesToShow) ?? new List<string>();

            foreach (var line in consoleLines)
            {
                if (currentLine >= paneHeight) break;
                DrawLogLine(width, line);
                currentLine++;
            }

            // Fill remaining space in console pane
            while (currentLine < paneHeight)
            {
                DrawEmptyLine(width, "");
                currentLine++;
            }
        }

        private static void DrawSeparatorBorder(int width)
        {
            if (_directOutput == null) return;
            _directOutput.WriteLine($"\x1b[36m╠{new string('═', width - 2)}╣\x1b[0m");
        }

        private static void DrawSectionHeader(int width, string title)
        {
            if (_directOutput == null) return;

            _directOutput.Write("\x1b[36m║ \x1b[37m");
            _directOutput.Write(title);

            int padding = width - title.Length - 4;
            if (padding > 0)
            {
                _directOutput.Write(new string(' ', padding));
            }

            _directOutput.WriteLine(" \x1b[36m║\x1b[0m");
        }

        private static void DrawTaskLine(int width, long taskId, TaskExecutor executor)
        {
            if (_directOutput == null) return;

            _directOutput.Write("\x1b[36m║ \x1b[36m");

            // Task ID (cyan)
            string taskIdStr = $"#{taskId}";
            _directOutput.Write(taskIdStr.PadRight(8));

            // Task Status (colored by status)
            string statusStr = executor.job.Status.ToString().PadRight(12);
            if (statusStr.Length > 12) statusStr = statusStr.Substring(0, 12);

            // Choose color based on status
            string statusColor = executor.job.Status switch
            {
                Tasks.QueueItemStatus.Pending => "\x1b[90m",      // Gray
                Tasks.QueueItemStatus.Assigned => "\x1b[90m",     // Gray
                Tasks.QueueItemStatus.Verifying => "\x1b[33m",    // Yellow
                Tasks.QueueItemStatus.InProgress => "\x1b[33m",   // Yellow
                Tasks.QueueItemStatus.Submitted => "\x1b[32m",    // Green
                Tasks.QueueItemStatus.Completed => "\x1b[32m",    // Green
                Tasks.QueueItemStatus.Failed => "\x1b[31m",       // Red
                Tasks.QueueItemStatus.Cancelled => "\x1b[90m",    // Gray
                _ => "\x1b[37m"                                   // White fallback
            };

            _directOutput.Write(statusColor);
            _directOutput.Write(statusStr);

            // Task Type (green)
            _directOutput.Write("\x1b[32m");
            string taskType = executor.job.TaskName.ToString().PadRight(20);
            if (taskType.Length > 20) taskType = taskType.Substring(0, 17) + "...";
            _directOutput.Write(taskType);

            string statusSummary = executor.CurrentStatus.LastOrDefault()?.Message ?? "";
            int availableWidth = width - 45;  // Adjusted for status field
            if (statusSummary.Length > availableWidth)
            {
                statusSummary = statusSummary.Substring(0, availableWidth - 3) + "...";
            }

            _directOutput.Write("\x1b[90m");
            _directOutput.Write(statusSummary);

            int usedWidth = 2 + 8 + 12 + 20 + statusSummary.Length;
            int padding = width - usedWidth - 2;
            if (padding > 0)
            {
                _directOutput.Write(new string(' ', padding));
            }

            _directOutput.WriteLine(" \x1b[36m║\x1b[0m");
        }

        private static void DrawTaskStatusLine(int width, StatusUpdate.StatusItem status)
        {
            if (_directOutput == null) return;

            _directOutput.Write("\x1b[36m║ \x1b[0m");

            switch (status.Type)
            {
                case StatusUpdate.StatusItem.StatusType.Error:
                    _directOutput.Write("\x1b[31m");
                    break;
                case StatusUpdate.StatusItem.StatusType.Warning:
                    _directOutput.Write("\x1b[33m");
                    break;
                default:
                    _directOutput.Write("\x1b[90m");
                    break;
            }

            string time = status.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            string displayLine = $"{time} {status.Message}";
            int availableWidth = width - 4;
            if (displayLine.Length > availableWidth)
            {
                displayLine = displayLine.Substring(0, availableWidth - 3) + "...";
            }

            _directOutput.Write(displayLine);

            int padding = width - displayLine.Length - 4;
            if (padding > 0)
            {
                _directOutput.Write(new string(' ', padding));
            }

            _directOutput.WriteLine(" \x1b[36m║\x1b[0m");
        }

        private static void DrawLogLine(int width, string line)
        {
            if (_directOutput == null) return;

            _directOutput.Write("\x1b[36m║ ");

            // Determine color based on content
            if (line.Contains("[ERROR]") || line.Contains("error") || line.Contains("failed"))
            {
                _directOutput.Write("\x1b[31m"); // Red
            }
            else if (line.Contains("[WARN]") || line.Contains("warning"))
            {
                _directOutput.Write("\x1b[33m"); // Yellow
            }
            else
            {
                _directOutput.Write("\x1b[90m"); // Gray  
            }

            int availableWidth = width - 4;
            string displayLine = line;
            if (displayLine.Length > availableWidth)
            {
                displayLine = displayLine.Substring(0, availableWidth - 3) + "...";
            }

            _directOutput.Write(displayLine);

            // Padding
            int padding = width - displayLine.Length - 4;
            if (padding > 0)
            {
                _directOutput.Write(new string(' ', padding));
            }

            _directOutput.WriteLine(" \x1b[36m║\x1b[0m");
        }

        private static void DrawEmptyLine(int width, string content)
        {
            if (_directOutput == null) return;

            _directOutput.Write("\x1b[36m║ \x1b[90m");
            _directOutput.Write(content);

            int padding = width - content.Length - 4;
            if (padding > 0)
            {
                _directOutput.Write(new string(' ', padding));
            }

            _directOutput.WriteLine(" \x1b[36m║\x1b[0m");
        }

        private static string GetCapabilitiesString()
        {
            var capabilities = new List<string>();

            // Check which capabilities are available
            foreach (var cap in Capabilities.Capabilities.CapabilityNames)
            {
                capabilities.Add(cap.Value);
            }

            return string.Join(", ", capabilities);
        }

        private static void UpdateCpuUsage()
        {
            try
            {
                // Update CPU every 2 seconds
                if (DateTime.Now - _lastCpuCheck > TimeSpan.FromSeconds(2))
                {
                    if (_currentProcess == null)
                    {
                        _currentProcess = Process.GetCurrentProcess();
                    }

                    // Simple approximation: current process CPU time as percentage
                    var totalCpuTime = _currentProcess.TotalProcessorTime.TotalMilliseconds;
                    var elapsedTime = (DateTime.Now - _currentProcess.StartTime).TotalMilliseconds;
                    _cpuUsage = (totalCpuTime / (Environment.ProcessorCount * elapsedTime)) * 100;

                    if (_cpuUsage > 100) _cpuUsage = 100;
                    if (_cpuUsage < 0) _cpuUsage = 0;

                    _lastCpuCheck = DateTime.Now;
                }
            }
            catch
            {
                _cpuUsage = 0.0;
            }
        }
    }
}
