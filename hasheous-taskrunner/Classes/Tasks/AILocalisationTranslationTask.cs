using hasheous_taskrunner.Classes.Capabilities;
using hasheous_taskrunner.Classes.Helpers;

namespace hasheous_taskrunner.Classes.Tasks
{
    public class AILanguageFileTranslationTask : ITask
    {
        public TaskType TaskType => TaskType.AILanguageFileTranslation;

        /// <inheritdoc />
        public async Task<TaskVerificationResult> VerifyAsync(Dictionary<string, string>? parameters, CancellationToken cancellationToken)
        {
            // check for required parameters - model and prompt
            TaskVerificationResult verificationResults = new TaskVerificationResult();

            if (parameters == null)
            {
                verificationResults.Status = TaskVerificationResult.VerificationStatus.Failure;
                verificationResults.Details["parameters"] = "Parameters cannot be null.";
                return await Task.FromResult(verificationResults);
            }

            if (!parameters.ContainsKey("model"))
            {
                verificationResults.Details["model"] = "Missing required parameter: model";
            }

            if (!parameters.ContainsKey("prompt"))
            {
                verificationResults.Details["prompt"] = "Missing required parameter: prompt";
            }

            if (!parameters.ContainsKey("language_name_in_english"))
            {
                verificationResults.Details["language_name_in_english"] = "Missing required parameter: language_name_in_english";
            }

            if (verificationResults.Details.Count > 0)
            {
                verificationResults.Status = TaskVerificationResult.VerificationStatus.Failure;
            }
            else
            {
                verificationResults.Status = TaskVerificationResult.VerificationStatus.Success;
            }

            return await Task.FromResult(verificationResults);

        }

        /// <inheritdoc />
        public async Task<Dictionary<string, object>> ExecuteAsync(Dictionary<string, string>? parameters, StatusUpdate statusUpdate, CancellationToken cancellationToken)
        {
            if (parameters == null)
            {
                return new Dictionary<string, object>
                {
                    { "result", false },
                    { "error", "Parameters cannot be null." }
                };
            }

            var verification = await VerifyAsync(parameters, cancellationToken);
            if (verification.Status != TaskVerificationResult.VerificationStatus.Success)
            {
                string verificationError = string.Join("; ", verification.Details.Select(d => $"{d.Key}: {d.Value}"));
                statusUpdate.AddStatus(StatusUpdate.StatusItem.StatusType.Error, "AITask verification failed before execution: " + verificationError);
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

            // get english language file from host
            string enuri = new Uri(new Uri(Config.Configuration["HostAddress"]), "/localisation/en.json").ToString();
            Dictionary<string, string>? englishLanguageFile = await TaskRunner.Classes.HttpHelper.Get<Dictionary<string, string>>(enuri);
            if (englishLanguageFile == null)
            {
                return new Dictionary<string, object>
                {
                    { "result", false },
                    { "error", "Failed to retrieve English language file from host." }
                };
            }

            if (englishLanguageFile == null)
            {
                return new Dictionary<string, object>
                {
                    { "result", false },
                    { "error", "Failed to deserialise English language file." }
                };
            }

            Dictionary<string, string> translatedLanguageFile = new Dictionary<string, string>();
            foreach (var kvp in englishLanguageFile)
            {
                string prompt = parameters["prompt"].Replace("<TEXT_TO_TRANSLATE>", kvp.Value);

                // call the AI capability to translate the text
                var translationResult = await ai.ExecuteAsync(new Dictionary<string, object>
                {
                    { "model", parameters["model"] },
                    { "prompt", prompt }
                }, statusUpdate);

                Dictionary<string, object> response = new Dictionary<string, object>();
                if (translationResult == null)
                {
                    response = new Dictionary<string, object>
                    {
                        { "result", false },
                        { "error", $"Translation failed for key: {kvp.Key}" }
                    };
                }

                if (translationResult != null && translationResult.ContainsKey("result") && !(bool)translationResult["result"])
                {
                    response = new Dictionary<string, object>
                    {
                        { "result", true },
                        { "error", translationResult.ContainsKey("error") ? translationResult["error"] : "Unknown error from AI capability." }
                    };

                    statusUpdate.AddStatus(StatusUpdate.StatusItem.StatusType.Error, "AITask: AI capability returned an error: " + (response.ContainsKey("error") ? response["error"] : "Unknown error."));
                }
                else if (translationResult != null && translationResult.ContainsKey("result") && (bool)translationResult["result"] && translationResult.ContainsKey("response"))
                {
                    translatedLanguageFile[kvp.Key] = translationResult["response"].ToString() ?? "";
                }
                else
                {
                    response = new Dictionary<string, object>
                    {
                        { "result", false },
                        { "error", $"Unexpected response from AI capability for key: {kvp.Key}" }
                    };

                    statusUpdate.AddStatus(StatusUpdate.StatusItem.StatusType.Error, "AITask: Unexpected response from AI capability for key: " + kvp.Key);
                }
            }

            // return the translated language file as a JSON string in the "response" key
            string translatedLanguageFileJson = System.Text.Json.JsonSerializer.Serialize(translatedLanguageFile);
            return new Dictionary<string, object>
            {
                { "result", true },
                { "response", translatedLanguageFileJson }
            };
        }
    }
}