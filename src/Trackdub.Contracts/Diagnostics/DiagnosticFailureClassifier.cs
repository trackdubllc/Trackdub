namespace Trackdub.Contracts.Diagnostics;

/// <summary>
/// Shared heuristics for assigning broad support-diagnostics failure categories.
/// </summary>
public static class DiagnosticFailureClassifier
{
    public static FailureCategory Classify(Exception exception, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string normalizedContext = context ?? string.Empty;
        string typeName = exception.GetType().Name;

        if (IsModelLoadFailure(exception))
        {
            return FailureCategory.ModelLoadFailure;
        }

        if (IsInferenceFailure(exception, typeName))
        {
            return FailureCategory.InferenceFailure;
        }

        if (IsPersistenceFailure(exception, typeName, normalizedContext))
        {
            return FailureCategory.PersistenceFailure;
        }

        if (IsMediaDecodeFailure(exception, normalizedContext))
        {
            return FailureCategory.MediaDecodeFailure;
        }

        if (IsUiFailure(exception, normalizedContext))
        {
            return FailureCategory.UiCrash;
        }

        return FailureCategory.UnknownError;
    }

    private static bool IsModelLoadFailure(Exception exception)
    {
        if (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return true;
        }

        if (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return IsModelPath(exception);
        }

        return exception is InvalidOperationException &&
               ContainsWordOrPhrase(exception.Message, "model", "onnx", "session");
    }

    private static bool IsInferenceFailure(Exception exception, string typeName)
    {
        return typeName.Contains("OnnxRuntimeException", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Inference", StringComparison.OrdinalIgnoreCase) ||
               exception is InvalidOperationException &&
               ContainsWordOrPhrase(
                   exception.Message,
                   "inference",
                   "tensor",
                   "shape",
                   "output",
                   "infer",
                   "transcription",
                   "transcript",
                   "translation",
                   "translate",
                   "tts");
    }

    private static bool IsPersistenceFailure(Exception exception, string typeName, string context)
    {
        if (exception is System.Data.Common.DbException ||
            typeName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Database", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (exception is not IOException and not UnauthorizedAccessException)
        {
            return false;
        }

        return ContainsPersistenceSignal(exception.Message) || ContainsPersistenceSignal(context);
    }

    private static bool IsMediaDecodeFailure(Exception exception, string context)
    {
        if (exception is InvalidDataException)
        {
            return true;
        }

        if (exception is not InvalidOperationException and
            not NotSupportedException and
            not IOException and
            not UnauthorizedAccessException)
        {
            return false;
        }

        return ContainsMediaSignal(exception.Message) || ContainsMediaContextSignal(context);
    }

    private static bool IsUiFailure(Exception exception, string context) =>
        exception is InvalidOperationException &&
        (ContainsWordOrPhrase(exception.Message, "ui", "xaml", "dispatcher", "window") ||
         ContainsWordOrPhrase(context, "ui", "xaml", "dispatcher", "window"));

    private static bool IsModelPath(Exception exception)
    {
        string? fileName = exception is FileNotFoundException fileNotFound ? fileNotFound.FileName : null;
        string searchTarget = fileName is null
            ? exception.Message
            : string.Concat(exception.Message, " ", fileName);
        return ContainsWordOrPhrase(searchTarget, "model", "onnx", ".ort", "models/", "model-cache");
    }

    private static bool ContainsPersistenceSignal(string text) =>
        ContainsWordOrPhrase(text, "database", "sqlite", "db", ".bs", "project", "save", "saving", "persist", "persistence");

    private static bool ContainsMediaSignal(string text) =>
        ContainsWordOrPhrase(text, "audio", "video", "media", "decode", "ffmpeg", "codec", "sample", "format");

    private static bool ContainsMediaContextSignal(string text) =>
        ContainsWordOrPhrase(text, "decode", "decoding", "ffmpeg", "codec");

    private static bool ContainsWordOrPhrase(string text, params string[] keywords)
    {
        foreach (string keyword in keywords)
        {
            if (ContainsWordOrPhrase(text, keyword))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsWordOrPhrase(string text, string keyword)
    {
        if (keyword.Any(static character => !char.IsLetterOrDigit(character)))
        {
            return text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }

        int startIndex = 0;
        while (startIndex < text.Length)
        {
            int index = text.IndexOf(keyword, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            int before = index - 1;
            int after = index + keyword.Length;
            if ((before < 0 || !char.IsLetterOrDigit(text[before])) &&
                (after >= text.Length || !char.IsLetterOrDigit(text[after])))
            {
                return true;
            }

            startIndex = index + keyword.Length;
        }

        return false;
    }
}
