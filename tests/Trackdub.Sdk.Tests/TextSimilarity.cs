namespace Trackdub.Sdk.Tests;

/// <summary>
/// Word-level text similarity using normalized longest common subsequence (LCS).
/// </summary>
internal static class TextSimilarity
{
    /// <summary>
    /// Computes word-level similarity between two texts.
    /// Returns a value between 0.0 (no overlap) and 1.0 (identical words).
    /// Uses normalized LCS of whitespace-split, lowercased words.
    /// </summary>
    public static double WordLevel(string expected, string actual)
    {
        string[] expectedWords = Tokenize(expected);
        string[] actualWords = Tokenize(actual);

        if (expectedWords.Length == 0 && actualWords.Length == 0) return 1.0;
        if (expectedWords.Length == 0 || actualWords.Length == 0) return 0.0;

        int lcsLength = LongestCommonSubsequenceLength(expectedWords, actualWords);
        int maxLength = Math.Max(expectedWords.Length, actualWords.Length);
        return (double)lcsLength / maxLength;
    }

    private static string[] Tokenize(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant().Trim(',', '.', '!', '?', ';', ':'))
            .Where(w => w.Length > 0)
            .ToArray();

    private static int LongestCommonSubsequenceLength(string[] a, string[] b)
    {
        int m = a.Length;
        int n = b.Length;
        int[] prev = new int[n + 1];
        int[] curr = new int[n + 1];

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                if (string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal))
                {
                    curr[j] = prev[j - 1] + 1;
                }
                else
                {
                    curr[j] = Math.Max(prev[j], curr[j - 1]);
                }
            }

            // Swap rows
            (prev, curr) = (curr, prev);
            Array.Clear(curr, 0, curr.Length);
        }

        return prev[n];
    }
}
