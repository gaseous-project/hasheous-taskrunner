using hasheous_taskrunner.Classes.Capabilities;
using hasheous_taskrunner.Classes.Helpers;

namespace hasheous_taskrunner.Classes.Tasks
{
    /// <summary>
    /// Represents an AI task that can be executed by the task runner.
    /// </summary>
    public class AIDescriptionAndTaggingTask : ITask
    {
        private const int MaxSourceCount = 25;
        private const int MaxSourceItemChars = 100_000;
        private const int MaxTotalPayloadChars = 1_000_000;

        /// <inheritdoc/>
        public TaskType TaskType => TaskType.AIDescriptionAndTagging;

        /// <inheritdoc/>
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

            if (!parameters.ContainsKey("model_description") && !parameters.ContainsKey("model_tags"))
            {
                verificationResults.Details["model"] = "Missing required parameter: model_description or model_tags";
            }

            if (!parameters.ContainsKey("prompt_description") && !parameters.ContainsKey("prompt_tags"))
            {
                verificationResults.Details["prompt"] = "Missing required parameter: prompt_description or prompt_tags";
            }

            // Require sources key for RAG-style AI task payload compatibility.
            if (!parameters.ContainsKey("sources"))
            {
                verificationResults.Details["sources"] = "Missing required parameter: sources";
            }
            else
            {
                var sourceIds = ParseSourceIds(parameters["sources"]);

                if (sourceIds.Count > MaxSourceCount)
                {
                    verificationResults.Details["sources_count"] =
                        $"Too many sources ({sourceIds.Count}). Maximum allowed is {MaxSourceCount}.";
                }

                foreach (string sourceId in sourceIds)
                {
                    string sourceKey = "Source_" + sourceId;
                    if (!parameters.ContainsKey(sourceKey))
                    {
                        verificationResults.Details[sourceKey] =
                            $"Source item referenced in sources list is missing: {sourceKey}";
                        continue;
                    }

                    int sourceLength = parameters[sourceKey]?.Length ?? 0;
                    if (sourceLength > MaxSourceItemChars)
                    {
                        verificationResults.Details[sourceKey] =
                            $"Source item exceeds max size ({sourceLength} chars). Limit is {MaxSourceItemChars}.";
                    }
                }
            }

            int payloadChars = GetTotalPayloadChars(parameters);
            if (payloadChars > MaxTotalPayloadChars)
            {
                verificationResults.Details["payload"] =
                    $"Task payload exceeds max size ({payloadChars} chars). Limit is {MaxTotalPayloadChars}.";
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

            // get sources from parameters
            List<string> sources = new List<string>();
            foreach (string sourceKey in ParseSourceIds(parameters["sources"]))
            {
                string paramKey = "Source_" + sourceKey;
                if (parameters.ContainsKey(paramKey))
                {
                    sources.Add("# " + sourceKey + "\n\n" + parameters[paramKey] + "\n\n");
                }
            }

            // override model to use ollama
            string modelDescriptionOverride = "gemma3:12b";
            string modelTagOverride = "qwen3:8b";
            bool applyDescriptionOverride = false;
            bool applyTagOverride = false;
            if (applyDescriptionOverride == false)
            {
                modelDescriptionOverride = parameters != null && parameters.ContainsKey("model_description") ? parameters["model_description"] : "";
            }
            if (applyTagOverride == false)
            {
                modelTagOverride = parameters != null && parameters.ContainsKey("model_tags") ? parameters["model_tags"] : "";
            }

            // generate the description
            var descriptionResult = await ai.ExecuteAsync(new Dictionary<string, object>
            {
                { "model", modelDescriptionOverride },
                { "prompt", parameters != null && parameters.ContainsKey("prompt_description") ? parameters["prompt_description"] : "" },
                { "embeddings", sources }
            }, statusUpdate);

            var tagsResult = await ai.ExecuteAsync(new Dictionary<string, object>
            {
                { "model", modelTagOverride },
                { "prompt", parameters != null && parameters.ContainsKey("prompt_tags") ? parameters["prompt_tags"] : "" },
                { "embeddings", sources },
                { "isTagGeneration", "true" }
            }, statusUpdate);

            Dictionary<string, object> response = new Dictionary<string, object>();
            if (descriptionResult == null || tagsResult == null)
            {
                response = new Dictionary<string, object>
                {
                    { "result", false },
                    { "error", "No response from AI capability." }
                };
            }

            if ((descriptionResult != null && descriptionResult.ContainsKey("result") && !(bool)descriptionResult["result"]) || (tagsResult != null && tagsResult.ContainsKey("result") && !(bool)tagsResult["result"]))
            {
                response = new Dictionary<string, object>
                {
                    { "result", false },
                    { "error", descriptionResult.ContainsKey("error") ? descriptionResult["error"] : "Unknown error from AI capability." }
                };

                statusUpdate.AddStatus(StatusUpdate.StatusItem.StatusType.Error, "AITask: AI capability returned an error: " + (response.ContainsKey("error") ? response["error"] : "Unknown error."));
                return response;
            }

            // merge results
            Dictionary<string, object> responseVars = new Dictionary<string, object>();
            if (descriptionResult != null && descriptionResult.ContainsKey("result") && (bool)descriptionResult["result"])
            {
                responseVars["description"] = descriptionResult.ContainsKey("response") ? descriptionResult["response"] : "";
            }
            else
            {
                responseVars["description"] = "";
            }
            if (tagsResult != null && tagsResult.ContainsKey("result") && (bool)tagsResult["result"])
            {
                responseVars["tags"] = tagsResult.ContainsKey("response") ? tagsResult["response"].ToString() : "";
                responseVars["tags"] = ollamaPrune(responseVars["tags"].ToString());
                // deserialise tags into a dictionary<string, string[]> if possible
                try
                {
                    var deserializedTags = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(responseVars["tags"].ToString() ?? "");
                    if (deserializedTags != null)
                    {
                        responseVars["tags"] = deserializedTags;
                    }
                }
                catch
                {
                    // ignore deserialization errors
                }
            }
            else
            {
                responseVars["tags"] = "";
            }
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

        private static int GetTotalPayloadChars(Dictionary<string, string> parameters)
        {
            int totalChars = 0;
            foreach (var kvp in parameters)
            {
                totalChars += kvp.Key?.Length ?? 0;
                totalChars += kvp.Value?.Length ?? 0;
            }

            return totalChars;
        }

        private static List<string> ParseSourceIds(string sourcesValue)
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

        private string ollamaPrune(string input)
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
    }
}