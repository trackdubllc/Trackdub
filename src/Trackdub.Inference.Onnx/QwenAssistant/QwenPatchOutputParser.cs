using System.Text.Json;
using Trackdub.Contracts.StarterPacks;

namespace Trackdub.Inference.Onnx.QwenAssistant;

internal static class QwenPatchOutputParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    public static bool TryParse(string modelOutput, out IReadOnlyList<StarterPackPatchOperation> operations)
    {
        operations = [];

        if (string.IsNullOrWhiteSpace(modelOutput))
        {
            return false;
        }

        string candidate = ExtractJsonArray(modelOutput);
        if (candidate.Length == 0)
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<RawPatchOperation>>(candidate, JsonOptions);
            if (parsed is null)
            {
                return false;
            }

            var results = new List<StarterPackPatchOperation>(parsed.Count);
            foreach (RawPatchOperation raw in parsed)
            {
                if (!Enum.TryParse<StarterPackPatchKind>(raw.Kind, ignoreCase: true, out var kind))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(raw.Reason))
                {
                    continue;
                }

                results.Add(new StarterPackPatchOperation(kind, raw.Stage, raw.Value, raw.Reason));
            }

            operations = results;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ExtractJsonArray(string text)
    {
        int start = text.IndexOf('[');
        int end = text.LastIndexOf(']');
        return start >= 0 && end > start ? text[start..(end + 1)] : string.Empty;
    }

    private sealed class RawPatchOperation
    {
        public string Kind { get; set; } = string.Empty;
        public string? Stage { get; set; }
        public string? Value { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
}
