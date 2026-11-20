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
    public class AILocalisationGenerationRegionalTask : ITask
    {
        /// <inheritdoc/>
        public TaskType TaskType => TaskType.AILocalisationGenerationRegional;

        /// <inheritdoc/>
        public bool RequiresExclusiveExecution => true;

        // dictionary item key consts
        private const string LanguageKey = "language";
        private const string ResultKey = "result";
        private const string ErrorKey = "error";
        private const string ResponseKey = "response";

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

        /// <inheritdoc/>
        public async Task<Dictionary<string, object>> ExecuteAsync(Dictionary<string, string>? parameters, Helpers.StatusUpdate statusUpdate, CancellationToken cancellationToken)
        {
            var verification = await VerifyAsync(parameters, cancellationToken);
            if (verification.Status != TaskVerificationResult.VerificationStatus.Success)
            {
                string verificationError = string.Join("; ", verification.Details.Select(d => $"{d.Key}: {d.Value}"));
                statusUpdate.AddStatus(StatusUpdate.StatusItem.StatusType.Error, "AILocalisationGenerationRegionalTask verification failed before execution: " + verificationError);
                return new Dictionary<string, object>
                {
                    { ResultKey, false },
                    { ErrorKey, "Verification failed: " + verificationError }
                };
            }

            // use the model and prompt parameters to call Ollama API
            var ai = Classes.Capabilities.Capabilities.GetCapabilityById<ICapability>(20); // AI capability
            if (ai == null)
            {
                throw new InvalidOperationException("AI capability is not available.");
            }

            // download the base language to to localise from the server
            var targetCulture = CultureInfo.GetCultureInfo(parameters[LanguageKey]);
            var enLocalisationItem = await Communication.Common.Get<LocalisationItem>($"/localisation/{targetCulture.TwoLetterISOLanguageName}.json");
            if (enLocalisationItem == null)
            {
                throw new InvalidOperationException($"Failed to download base language content for {targetCulture.TwoLetterISOLanguageName}");
            }

            // set the description for the current task execution
            statusUpdate.SetDescription("AI Localisation Generation Regional: " + targetCulture.EnglishName);

            // pull from the server first to compare each string with the base language content (each string has a last updated value)
            LocalisationItem? targetLocalisationItem;
            try
            {
                targetLocalisationItem = await Communication.Common.Get<LocalisationItem>($"/localisation/{targetCulture.Name}.json");
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

                bool performTranslation = false;

                // abort early if the string is empty or null
                if (string.IsNullOrWhiteSpace(kvp.Value.Value))
                {
                    performTranslation = true;
                }

                // check if the translation is missing in the target localisation item
                if (!targetLocalisationItem.Translations.ContainsKey(kvp.Key))
                {
                    performTranslation = true;
                }

                // check if the English translation is newer than the target translation
                if (targetLocalisationItem.Translations.ContainsKey(kvp.Key))
                {
                    if (kvp.Value.UpdatedAt > targetLocalisationItem.Translations[kvp.Key].UpdatedAt)
                    {
                        performTranslation = true;
                    }
                }

                if (performTranslation && !targetCulture.IsNeutralCulture)
                {
                    // begin translation
                    statusUpdate.AddStatus(StatusUpdate.StatusItem.StatusType.Info, "OllamaCapability: [" + translationCount + " / " + enLocalisationItem.Translations.Count + "] Localising " + kvp.Key + " for language " + targetCulture.EnglishName);

                    string jsonEncodedValue = Newtonsoft.Json.JsonConvert.SerializeObject(kvp.Value.Value.ToString().Trim());

                    string prompt = "You are a native " + targetCulture.EnglishName + " (" + targetCulture.Name + ") UI localisation editor. Evaluate the JSON-encoded UI string below for " + targetCulture.EnglishName + " usage.\n\nRules:\n- If spelling, vocabulary, or phrasing needs adaptation for " + targetCulture.Name + ", output ONLY the adapted string as a valid JSON string.\n- If the string is already natural and accurate for " + targetCulture.Name + ", output ONLY: \"NO_CHANGE\"\n- Preserve all HTML tags, Markdown syntax, and placeholders({var}, %s, %d) verbatim.\n- Do not include explanations, lists, or extra text.\n\n\n" + jsonEncodedValue;

                    try
                    {
                        var translatedString = await ai.ExecuteAsync(new Dictionary<string, object>
                        {
                            { "model", model },
                            { "prompt", prompt },
                        }, statusUpdate);

                        if (translatedString != null && translatedString.ContainsKey(ResponseKey) && !string.IsNullOrWhiteSpace(translatedString[ResponseKey].ToString()))
                        {
                            string translatedValue = Newtonsoft.Json.JsonConvert.DeserializeObject<string>(translatedString[ResponseKey]?.ToString()?.Trim() ?? "") ?? "";
                            bool aiTranslated = true;

                            if (translatedValue == "NO_CHANGE" || translatedValue == kvp.Value.Value || string.IsNullOrWhiteSpace(translatedValue) || Tools.IsWithinSimilarityThreshold(kvp.Value.Value, translatedValue, 0.80))
                            {
                                translatedValue = kvp.Value.Value;
                                aiTranslated = kvp.Value.AITranslated;
                            }

                            targetLocalisationItem.Translations[kvp.Key] = new LocalisationItem.TranslationItem
                            {
                                Value = translatedValue,
                                AITranslated = aiTranslated,
                                CreatedAt = kvp.Value.CreatedAt,
                                UpdatedAt = kvp.Value.UpdatedAt
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        statusUpdate.AddStatus(StatusUpdate.StatusItem.StatusType.Warning, "OllamaCapability: Error localising " + kvp.Key + " with exception: " + Newtonsoft.Json.JsonConvert.SerializeObject(ex));
                    }
                }
                else
                {
                    // insert dummy key - this is to prevent missing translations in the target localisation item from causing issues
                    targetLocalisationItem.Translations[kvp.Key] = new LocalisationItem.TranslationItem
                    {
                        Value = kvp.Value.Value,
                        AITranslated = kvp.Value.AITranslated,
                        CreatedAt = kvp.Value.CreatedAt,
                        UpdatedAt = kvp.Value.UpdatedAt
                    };
                }
            }

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

        private static string GetRecommendedModel(string localeCode)
        {
            var culture = new CultureInfo(localeCode);

            if (culture.TwoLetterISOLanguageName == "en")
                return "gemma3:12b";

            return IsLatinScript(localeCode)
                ? "gemma3:12b"
                : "gemma3:27b";
        }
    }
}