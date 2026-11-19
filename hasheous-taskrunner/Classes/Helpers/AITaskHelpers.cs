using System.Globalization;

namespace hasheous_taskrunner.Classes.Helpers
{
    public static class AITaskHelpers
    {
        public static readonly HashSet<string> ValidCultures = new(
            CultureInfo.GetCultures(CultureTypes.AllCultures).Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        public static int GetTotalPayloadChars(Dictionary<string, string> parameters)
        {
            int totalChars = 0;
            foreach (var kvp in parameters)
            {
                totalChars += kvp.Key?.Length ?? 0;
                totalChars += kvp.Value?.Length ?? 0;
            }

            return totalChars;
        }

        public static List<string> ParseSourceIds(string sourcesValue)
        {
            if (string.IsNullOrWhiteSpace(sourcesValue))
            {
                return new List<string>();
            }

            return sourcesValue
                .Split(';')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string ollamaPrune(string input)
        {
            input = input.Trim();

            // Look for markdown code blocks anywhere in the text
            int codeBlockStart = input.IndexOf("```");
            if (codeBlockStart >= 0)
            {
                // Skip past the opening ``` and optional language identifier (e.g., ```json)
                int firstLineEnd = input.IndexOf('\n', codeBlockStart);
                if (firstLineEnd < 0) return input; // malformed, return as-is

                // Find the closing ```
                int codeBlockEnd = input.IndexOf("```", firstLineEnd + 1);
                if (codeBlockEnd < 0) return input; // malformed, return as-is

                // Extract content between markers
                input = input.Substring(firstLineEnd + 1, codeBlockEnd - firstLineEnd - 1).Trim();
            }

            // Validate JSON
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(input))
                {
                    // Valid JSON, return it
                    return input;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Invalid JSON - try to find JSON object boundaries
                int jsonStart = input.IndexOf('{');
                int jsonEnd = input.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    string extracted = input.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    try
                    {
                        using (var doc = System.Text.Json.JsonDocument.Parse(extracted))
                        {
                            return extracted;
                        }
                    }
                    catch
                    {
                        // Still invalid, return original
                        return input;
                    }
                }
            }

            return input;
        }

        public static string StripCodeFence(string s)
        {
            s = s.Trim();
            if (s.Length > 6 && s.StartsWith("```") && s.EndsWith("```"))
            {
                int firstLineEnd = s.IndexOf('\n');
                int lastFence = s.LastIndexOf("```");
                if (firstLineEnd >= 0 && lastFence > firstLineEnd)
                {
                    return s.Substring(firstLineEnd + 1, lastFence - firstLineEnd - 1).Trim();
                }
            }
            return s;
        }

        public static bool IsLatinScript(string localeCode)
        {
            var culture = new CultureInfo(localeCode);

            // Quick exit for right-to-left languages (Arabic, Hebrew, etc.)
            if (culture.TextInfo.IsRightToLeft)
                return false;

            // Inspect all letter characters in the native language name
            var letters = culture.NativeName.Where(char.IsLetter).ToList();

            if (letters.Count == 0)
                return false;

            // Check if all letters fall within Unicode Latin blocks:
            // Basic Latin (0x0000-0x007F)
            // Latin-1 Supplement (0x0080-0x00FF)
            // Latin Extended-A & Extended-B (0x0100-0x024F)
            // Latin Extended Additional (0x1E00-0x1EFF) - covers Vietnamese, etc.
            return letters.All(c =>
                c <= 0x024F || (c >= 0x1E00 && c <= 0x1EFF)
            );
        }
    }
}