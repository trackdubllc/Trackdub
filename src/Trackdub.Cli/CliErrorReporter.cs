using System.Text.Json;
using System.Text.Json.Serialization;

using Trackdub.Sdk;

namespace Trackdub.Cli;

internal static class CliErrorReporter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Reports a validation error (e.g., invalid argument, missing media file) to stderr as single-line JSON.
    /// </summary>
    public static void ReportValidationError(ErrorCode code, string message, string? parameterName)
    {
        var error = new CliErrorObject
        {
            ErrorCode = code,
            Message = message,
            Parameter = parameterName,
        };

        WriteErrorJson(error);
    }

    /// <summary>
    /// Reports a pipeline stage failure to stderr as single-line JSON, including preserved artifact paths.
    /// </summary>
    public static void ReportStageFailure(ErrorCode code, string stageName, string message, IReadOnlyList<string>? artifactPaths)
    {
        var error = new CliErrorObject
        {
            ErrorCode = code,
            Message = message,
            Parameter = stageName,
            ArtifactPaths = artifactPaths is { Count: > 0 } ? artifactPaths : null,
        };

        WriteErrorJson(error);
    }

    /// <summary>
    /// Reports a generic error to stderr as single-line JSON.
    /// </summary>
    public static void ReportError(ErrorCode code, string message)
    {
        var error = new CliErrorObject
        {
            ErrorCode = code,
            Message = message,
        };

        WriteErrorJson(error);
    }

    private static void WriteErrorJson(CliErrorObject error)
    {
        string json = JsonSerializer.Serialize(error, s_jsonOptions);
        Console.Error.WriteLine(json);
    }

    private sealed class CliErrorObject
    {
        public required ErrorCode ErrorCode { get; init; }
        public required string Message { get; init; }
        public string? Parameter { get; init; }
        public IReadOnlyList<string>? ArtifactPaths { get; init; }
    }
}
