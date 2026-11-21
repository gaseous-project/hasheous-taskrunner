using System.Globalization;
using hasheous_taskrunner.Classes.Tasks;
using hasheous_taskrunner.Models;

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

        // dictionary item key consts
        private const string LanguageKey = "language";
        private const string ResultKey = "result";
        private const string ErrorKey = "error";
        private const string ResponseKey = "response";

        /// <summary>
        /// Shared verification logic for AI translation tasks.
        /// </summary>
        /// <param name="parameters"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static async Task<TaskVerificationResult> VerifyTranslationAsync(Dictionary<string, string>? parameters, CancellationToken cancellationToken)
        {
            // check for required parameters
            TaskVerificationResult verificationResults = new TaskVerificationResult();

            if (parameters == null)
            {
                verificationResults.Status = TaskVerificationResult.VerificationStatus.Failure;
                verificationResults.Details["parameters"] = "Parameters cannot be null.";
                return await Task.FromResult(verificationResults);
            }

            if (!parameters.ContainsKey(LanguageKey))
            {
                verificationResults.Details[LanguageKey] = "Missing required parameter: language";
            }
            else
            {
                // language must be a valid two character ISO language code - check against cultureinfo
                if (!parameters.TryGetValue(LanguageKey, out var lang) || !ValidCultures.Contains(lang))
                {
                    verificationResults.Details[LanguageKey] = $"Invalid language code: {lang}";
                }
            }

            if (verificationResults.Details.Count > 0)
            {
                verificationResults.Status = TaskVerificationResult.VerificationStatus.Failure;
            }

            return await Task.FromResult(verificationResults);
        }

        /// <summary>
        /// Handles the result of an AI translation task and formats it into a standard response dictionary.
        /// </summary>
        /// <param name="targetLocalisationItem">
        /// The localisation item containing the translations to be processed.
        /// </param>
        /// <returns>
        /// A dictionary containing the result of the translation task, including the translated content and any error information.
        /// </returns>
        public static Dictionary<string, object> HandleTranslationResult(LocalisationItem targetLocalisationItem)
        {
            Dictionary<string, object> responseVars = new Dictionary<string, object>();
            responseVars["translated"] = Newtonsoft.Json.JsonConvert.SerializeObject(targetLocalisationItem);

            Dictionary<string, object> response = new Dictionary<string, object>
            {
                { ResultKey, true },
                { ResponseKey, responseVars }
            };

            if (response.ContainsKey(ResultKey) && response[ResultKey] is bool resultBool && resultBool)
            {
                // success
                // ollama sometimes returns the response wrapped in markdown code blocks, so strip those if present
                if (response.ContainsKey(ResponseKey) && response[ResponseKey] is string respStr)
                {
                    respStr = respStr.Trim();
                    if ((respStr.StartsWith("```") || respStr.StartsWith("```json")) && respStr.EndsWith("```"))
                    {
                        int firstLineEnd = respStr.IndexOf('\n');
                        int lastLineStart = respStr.LastIndexOf("```");
                        if (firstLineEnd >= 0 && lastLineStart > firstLineEnd)
                        {
                            respStr = respStr.Substring(firstLineEnd + 1, lastLineStart - firstLineEnd - 1).Trim();
                        }
                    }
                    response[ResponseKey] = respStr;
                }
            }
            else
            {
                // failure
                response.Add(ResultKey, "");
                response.Add(ErrorKey, response.ContainsKey(ErrorKey) ? response[ErrorKey] : "Unknown error from AI capability.");
            }

            return response;
        }


        public static string GetRecommendedModel(string localeCode, bool translate)
        {
            var culture = new CultureInfo(localeCode);

            if (culture.TwoLetterISOLanguageName == "en")
                return "gemma3:12b";

            if (translate)
            {
                return IsLatinScript(localeCode)
                    ? "translategemma:12b"
                    : "translategemma:27b";
            }
            else
            {
                return IsLatinScript(localeCode)
                ? "gemma3:12b"
                : "gemma3:27b";
            }
        }
    }
}