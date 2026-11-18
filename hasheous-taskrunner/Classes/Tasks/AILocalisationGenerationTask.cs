using System.Globalization;
using hasheous_taskrunner.Classes.Capabilities;
using hasheous_taskrunner.Classes.Helpers;
using hasheous_taskrunner.Models;
using static hasheous_taskrunner.Classes.Helpers.AITaskHelpers;

namespace hasheous_taskrunner.Classes.Tasks
{
    /// <summary>
    /// Represents an AI task that can be executed by the task runner.
    /// </summary>
    public class AILocalisationGenerationTask : ITask
    {
        private const int MaxSourceCount = 25;
        private const int MaxSourceItemChars = 100_000;
        private const int MaxTotalPayloadChars = 1_000_000;

        /// <inheritdoc/>
        public TaskType TaskType => TaskType.AILocalisationGeneration;

        /// <inheritdoc/>
        public bool RequiresExclusiveExecution => true;

        /// <inheritdoc/>
        public async Task<TaskVerificationResult> VerifyAsync(Dictionary<string, string>? parameters, CancellationToken cancellationToken)
        {
            // check for required parameters
            TaskVerificationResult verificationResults = new TaskVerificationResult();

            if (parameters == null)
            {
                verificationResults.Status = TaskVerificationResult.VerificationStatus.Failure;
                verificationResults.Details["parameters"] = "Parameters cannot be null.";
                return await Task.FromResult(verificationResults);
            }

            if (!parameters.ContainsKey("language"))
            {
                verificationResults.Details["language"] = "Missing required parameter: language";
            }

            // language must be a valid two character ISO language code - check against cultureinfo
            if (parameters.ContainsKey("language"))
            {
                try
                {
                    var culture = CultureInfo.GetCultureInfo(parameters["language"]);
                }
                catch (CultureNotFoundException)
                {
                    verificationResults.Details["language"] = "Invalid language code: " + parameters["language"];
                }
            }

            if (verificationResults.Details.Count > 0)
            {
                verificationResults.Status = TaskVerificationResult.VerificationStatus.Failure;
            }

            return await Task.FromResult(verificationResults);
        }

        /// <inheritdoc/>
        public async Task<Dictionary<string, object>> ExecuteAsync(Dictionary<string, string>? parameters, Helpers.StatusUpdate statusUpdate, CancellationToken cancellationToken)
        {
            var verification = await VerifyAsync(parameters, cancellationToken);
            if (verification.Status != TaskVerificationResult.VerificationStatus.Success)
            {
                string verificationError = string.Join("; ", verification.Details.Select(d => $"{d.Key}: {d.Value}"));
                statusUpdate.AddStatus(StatusUpdate.StatusItem.StatusType.Error, "AILocalisationGenerationTask verification failed before execution: " + verificationError);
                return new Dictionary<string, object>
                {
                    { "result", false },
                    { "error", "Verification failed: " + verificationError }
                };
            }

            // use the model and prompt parameters to call Ollama API
            var ai = Classes.Capabilities.Capabilities.GetCapabilityById<ICapability>(20); // AI capability
            if (ai == null)
            {
                throw new InvalidOperationException("AI capability is not available.");
            }

            // download the base language to translate from the server - this is always en
            var enLocalisationItem = await Communication.Common.Get<LocalisationItem>("/localisation/en.json");
            if (enLocalisationItem == null)
            {
                throw new InvalidOperationException($"Failed to download base language content for en");
            }

            // pull from the server first to compare each string with the base language content (each string has a last updated value)
            var targetCulture = CultureInfo.GetCultureInfo(parameters["language"]);
            LocalisationItem? targetLocalisationItem;
            try
            {
                targetLocalisationItem = await Communication.Common.Get<LocalisationItem>($"/localisation/{targetCulture.TwoLetterISOLanguageName}.json");
            }
            catch (HttpRequestException ex)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                    targetLocalisationItem = new LocalisationItem
                    {
                        FullCode = targetCulture.Name,
                        EnglishName = targetCulture.EnglishName,
                        NativeName = targetCulture.NativeName,
                        Direction = targetCulture.TextInfo.IsRightToLeft ? LocalisationItem.LanguageDirection.RightToLeft : LocalisationItem.LanguageDirection.LeftToRight,
                        Version = 1,
                        Translations = new Dictionary<string, LocalisationItem.TranslationItem>()
                    };
                else
                    throw;
            }

            Dictionary<string, object> responseVars = new Dictionary<string, object>();

            // loop through all of the Translations in the english base language and compare with the variant - force translation if the date on the english translation is newer than the variant, or the translation doesn't exist in the variant
            string model = GetRecommendedModel(targetCulture.Name);
            int translationCount = 0;
            foreach (var kvp in enLocalisationItem.Translations)
            {
                translationCount++;

                // Update progress - this allows tracking of task completion percentage
                int progressPercentage = (int)((double)translationCount / enLocalisationItem.Translations.Count * 100);
                if (progressPercentage > 100) progressPercentage = 100;
                statusUpdate.SetProgress(progressPercentage);

                if ((!targetLocalisationItem.Translations.ContainsKey(kvp.Key) || kvp.Value.UpdatedAt > targetLocalisationItem.Translations[kvp.Key].UpdatedAt) && !string.IsNullOrWhiteSpace(kvp.Value.Value))
                {
                    // begin translation
                    statusUpdate.AddStatus(StatusUpdate.StatusItem.StatusType.Info, "OllamaCapability: Translating " + kvp.Key + " for language " + targetCulture.EnglishName);
                    string prompt = "Translate the following English (en) text to " + targetCulture.EnglishName + " (" + targetCulture.Name + "). Preserve all Markdown and HTML syntax, and keep variable placeholders like {var} or %s intact. Output ONLY the translated text and nothing else.\n\n\n" + kvp.Value.Value.ToString().Trim();
                    var translatedString = await ai.ExecuteAsync(new Dictionary<string, object>
                        {
                            { "model", model },
                            { "prompt", prompt },
                        }, statusUpdate);

                    if (translatedString != null && translatedString.ContainsKey("response") && !string.IsNullOrWhiteSpace(translatedString["response"].ToString()))
                    {
                        targetLocalisationItem.Translations[kvp.Key] = new LocalisationItem.TranslationItem
                        {
                            Value = translatedString["response"].ToString().Trim(),
                            AITranslated = true,
                            CreatedAt = kvp.Value.CreatedAt,
                            UpdatedAt = kvp.Value.UpdatedAt
                        };
                    }
                }
                else
                {
                    // insert dummy key - this is to prevent missing translations in the target localisation item from causing issues
                    targetLocalisationItem.Translations[kvp.Key] = new LocalisationItem.TranslationItem
                    {
                        Value = kvp.Value.Value,
                        AITranslated = true,
                        CreatedAt = kvp.Value.CreatedAt,
                        UpdatedAt = kvp.Value.UpdatedAt
                    };
                }
            }

            responseVars["translated"] = Newtonsoft.Json.JsonConvert.SerializeObject(targetLocalisationItem);

            Dictionary<string, object> response = new Dictionary<string, object>();
            response = new Dictionary<string, object>
            {
                { "result", true },
                { "response", responseVars }
            };

            if (response.ContainsKey("result") && response["result"] is bool resultBool && resultBool)
            {
                // success
                // ollama sometimes returns the response wrapped in markdown code blocks, so strip those if present
                if (response.ContainsKey("response") && response["response"] is string respStr)
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
                    response["response"] = respStr;
                }
            }
            else
            {
                // failure
                response.Add("result", "");
                response.Add("error", response.ContainsKey("error") ? response["error"] : "Unknown error from AI capability.");
            }

            return await Task.FromResult(response ?? new Dictionary<string, object>());
        }

        private static string GetRecommendedModel(string localeCode)
        {
            var culture = new CultureInfo(localeCode);

            if (culture.TwoLetterISOLanguageName == "en")
                return "gemma3:12b";

            return IsLatinScript(localeCode)
                ? "translategemma:12b"
                : "translategemma:27b";
        }
    }
}