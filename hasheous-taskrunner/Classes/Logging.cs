using System.Net;
using System.Text.Json;

namespace hasheous_taskrunner.Classes
{
    public static class Logging
    {
        public static void Log(string message)
        {
            string clientName = GetEffectiveClientName();
            var originalColor = ConsoleColor.Gray;
            Console.ForegroundColor = GetClientColor(clientName);
            Console.Write($"[{clientName}] ");
            Console.ForegroundColor = originalColor;
            Console.WriteLine(message);
        }

        public static void WriteLine(string message)
        {
            Log(message);
        }

        private static ConsoleColor GetClientColor(string clientName)
        {
            ConsoleColor[] colors =
            {
                ConsoleColor.Cyan,
                ConsoleColor.Green,
                ConsoleColor.Yellow,
                ConsoleColor.Magenta,
                ConsoleColor.Blue,
                ConsoleColor.DarkCyan,
                ConsoleColor.DarkGreen,
                ConsoleColor.DarkYellow,
                ConsoleColor.DarkMagenta,
                ConsoleColor.DarkBlue,
                ConsoleColor.White
            };

            int hash = clientName.GetHashCode();
            int index = Math.Abs(hash) % colors.Length;
            return colors[index];
        }

        private static string GetEffectiveClientName()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--ClientName" && i + 1 < args.Length)
                {
                    return args[i + 1];
                }
            }

            string? envValue = Environment.GetEnvironmentVariable("ClientName");
            if (!string.IsNullOrEmpty(envValue))
            {
                return envValue;
            }

            string baseClientName = Dns.GetHostName();
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
                    if (config != null && config.TryGetValue("ClientName", out string? configClientName))
                    {
                        if (!string.IsNullOrEmpty(configClientName))
                        {
                            return configClientName;
                        }
                    }
                }
                catch
                {
                    // If config file can't be read, fall back to hostname
                }
            }

            return baseClientName;
        }
    }
}