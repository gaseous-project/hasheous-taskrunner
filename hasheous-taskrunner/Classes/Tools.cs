using System.Buffers;

namespace hasheous_taskrunner.Classes
{
    /// <summary>
    /// Utility class providing common helper methods and tools for the application.
    /// </summary>
    public static class Tools
    {
        /// <summary>
        /// Checks whether the localised string remains within the minimum allowed similarity
        /// compared to the original base string.
        /// </summary>
        /// <param name="original">The base language string (e.g., en-US or fr).</param>
        /// <param name="localised">The LLM-adapted regional string (e.g., en-AU or fr-CA).</param>
        /// <param name="minSimilarity">Minimum similarity ratio between 0.0 and 1.0 (e.g., 0.70 for 70%).</param>
        public static bool IsWithinSimilarityThreshold(
            string original,
            string localised,
            double minSimilarity = 0.70)
        {
            if (minSimilarity is < 0.0 or > 1.0)
                throw new ArgumentOutOfRangeException(nameof(minSimilarity), "Must be between 0.0 and 1.0.");

            // Identical strings are always valid
            if (string.Equals(original, localised, StringComparison.Ordinal))
                return true;

            // Reject if one is empty and the other is not
            if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(localised))
                return false;

            // override similarity based on string length
            double threshold = original.Length switch
            {
                <= 12 => 0.35, // Allows single-word swaps like "Trash" -> "Rubbish"
                <= 30 => 0.60, // Short labels and headings
                _ => 0.75  // Full sentences and error messages
            };

            int maxLength = Math.Max(original.Length, localised.Length);
            int distance = ComputeLevenshteinDistance(original.AsSpan(), localised.AsSpan());

            double similarity = 1.0 - ((double)distance / maxLength);
            return similarity >= Math.Max(minSimilarity, threshold);
        }

        /// <summary>
        /// Computes the Levenshtein distance between two character spans.
        /// </summary>
        /// <param name="source">The source character span.</param>
        /// <param name="target">The target character span.</param>
        /// <returns>The Levenshtein distance between the source and target spans.</returns>
        public static int ComputeLevenshteinDistance(ReadOnlySpan<char> source, ReadOnlySpan<char> target)
        {
            if (source.IsEmpty) return target.Length;
            if (target.IsEmpty) return source.Length;

            // Ensure 'target' is the shorter span to minimise array size
            if (source.Length < target.Length)
            {
                var temp = source;
                source = target;
                target = temp;
            }

            int targetLen = target.Length;
            int arrayLength = targetLen + 1;

            // Rent arrays from the shared pool to avoid heap allocations
            int[] previousRow = ArrayPool<int>.Shared.Rent(arrayLength);
            int[] currentRow = ArrayPool<int>.Shared.Rent(arrayLength);

            try
            {
                for (int j = 0; j <= targetLen; j++)
                    previousRow[j] = j;

                for (int i = 1; i <= source.Length; i++)
                {
                    currentRow[0] = i;
                    char sourceChar = source[i - 1];

                    for (int j = 1; j <= targetLen; j++)
                    {
                        int cost = (sourceChar == target[j - 1]) ? 0 : 1;

                        int deletion = previousRow[j] + 1;
                        int insertion = currentRow[j - 1] + 1;
                        int substitution = previousRow[j - 1] + cost;

                        currentRow[j] = Math.Min(Math.Min(deletion, insertion), substitution);
                    }

                    // Swap rows for the next iteration
                    var tempRow = previousRow;
                    previousRow = currentRow;
                    currentRow = tempRow;
                }

                return previousRow[targetLen];
            }
            finally
            {
                ArrayPool<int>.Shared.Return(previousRow);
                ArrayPool<int>.Shared.Return(currentRow);
            }
        }
    }
}