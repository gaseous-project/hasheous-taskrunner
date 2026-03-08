using System.Collections.Concurrent;

namespace hasheous_taskrunner.Classes.Helpers
{
    /// <summary>
    /// Captures Console.WriteLine output for display in TUI.
    /// </summary>
    public class ConsoleCapture : TextWriter
    {
        private readonly TextWriter _originalOutput;
        private readonly ConcurrentQueue<string> _capturedLines = new ConcurrentQueue<string>();
        private const int MaxLines = 100;
        private bool _suppressOutput = false;

        public ConsoleCapture(TextWriter originalOutput)
        {
            _originalOutput = originalOutput;
        }

        public override System.Text.Encoding Encoding => _originalOutput.Encoding;

        public void SetSuppressOutput(bool suppress)
        {
            _suppressOutput = suppress;
        }

        public override void WriteLine(string? value)
        {
            if (value != null)
            {
                _capturedLines.Enqueue($"[{DateTime.Now:HH:mm:ss}] {value}");
                while (_capturedLines.Count > MaxLines)
                {
                    _capturedLines.TryDequeue(out _);
                }
            }
            // Only write to original output if not suppressed (i.e., in --notui mode)
            if (!_suppressOutput)
            {
                _originalOutput.WriteLine(value);
            }
        }

        public override void Write(string? value)
        {
            // Only write to original output if not suppressed
            if (!_suppressOutput)
            {
                _originalOutput.Write(value);
            }
        }

        public List<string> GetRecentLines(int count)
        {
            return _capturedLines.Reverse().Take(count).Reverse().ToList();
        }

        public TextWriter OriginalOutput => _originalOutput;

        public static ConsoleCapture? Instance { get; private set; }

        public static void Install()
        {
            if (Instance == null)
            {
                Instance = new ConsoleCapture(Console.Out);
                Console.SetOut(Instance);
            }
        }
    }
}
