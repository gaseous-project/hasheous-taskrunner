using hasheous_taskrunner.Classes.Capabilities;
using hasheous_taskrunner.Classes.Helpers;
using static hasheous_taskrunner.Classes.Helpers.AITaskHelpers;

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
        public bool RequiresExclusiveExecution => false;

        // dictionary item key consts
        private const string SourcesKey = "sources";
        private const string ResultKey = "result";
        private const string ErrorKey = "error";
        private const string ResponseKey = "response";

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
            if (!parameters.ContainsKey(SourcesKey))
            {
                verificationResults.Details[SourcesKey] = "Missing required parameter: sources";
            }
            else
            {
                var sourceIds = ParseSourceIds(parameters[SourcesKey]);

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
                    { ResultKey, false },
                    { ErrorKey, "Parameters cannot be null." }
                };
            }

            var verification = await VerifyAsync(parameters, cancellationToken);
            if (verification.Status != TaskVerificationResult.VerificationStatus.Success)
            {
                string verificationError = string.Join("; ", verification.Details.Select(d => $"{d.Key}: {d.Value}"));
                statusUpdate.AddStatus(StatusUpdate.StatusItem.StatusType.Error, "AIDescriptionAndTaggingTask verification failed before execution: " + verificationError);
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

            // get sources from parameters
            List<string> sources = new List<string>();
            foreach (string sourceKey in ParseSourceIds(parameters[SourcesKey]))
            {
                string paramKey = "Source_" + sourceKey;
                if (parameters.ContainsKey(paramKey))
                {
                    sources.Add("# " + sourceKey + "\n\n" + parameters[paramKey] + "\n\n");
                }
            }

            // override model to use ollama - note: these overrides are for testing only and should not be used in production
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
            statusUpdate.SetProgress(50);

            var tagsResult = await ai.ExecuteAsync(new Dictionary<string, object>
            {
                { "model", modelTagOverride },
                { "prompt", parameters != null && parameters.ContainsKey("prompt_tags") ? parameters["prompt_tags"] : "" },
                { "embeddings", sources },
                { "isTagGeneration", "true" }
            }, statusUpdate);
            statusUpdate.SetProgress(100);

            if (descriptionResult == null || tagsResult == null)
            {
                const string noResponse = "No response from AI capability.";
                statusUpdate.AddStatus(StatusUpdate.StatusItem.StatusType.Error, "AIDescriptionAndTaggingTask: " + noResponse);
                return new Dictionary<string, object>
                {
                    { ResultKey, false },
                    { ErrorKey, noResponse }
                };
            }

            bool descOk = descriptionResult.TryGetValue(ResultKey, out var d) && d is true;
            bool tagsOk = tagsResult.TryGetValue(ResultKey, out var t) && t is true;

            if (!descOk || !tagsOk)
            {
                var failed = !descOk ? descriptionResult : tagsResult;
                object error = failed.TryGetValue(ErrorKey, out var e) && e != null
                    ? e
                    : "Unknown error from AI capability.";

                statusUpdate.AddStatus(StatusUpdate.StatusItem.StatusType.Error, "AIDescriptionAndTaggingTask: AI capability returned an error: " + error);
                return new Dictionary<string, object>
                {
                    { ResultKey, false },
                    { ErrorKey, error }
                };
            }

            // both succeeded, so merge
            string description = descriptionResult.TryGetValue(ResponseKey, out var dr) ? dr?.ToString() ?? "" : "";

            string tagsRaw = tagsResult.TryGetValue(ResponseKey, out var tr) ? tr?.ToString() ?? "" : "";
            string tagsText = StripCodeFence(ollamaPrune(tagsRaw));

            object tags = tagsText;
            try
            {
                var deserializedTags = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(tagsText);
                if (deserializedTags != null)
                {
                    tags = deserializedTags;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // keep the raw string if it isn't valid JSON
            }

            return new Dictionary<string, object>
            {
                { ResultKey, true },
                { ResponseKey, new Dictionary<string, object>
                    {
                        { "description", description },
                        { "tags", tags }
                    }
                }
            };
        }
    }
}