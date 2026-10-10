using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Worker;

/// <summary>
/// Wire format between the application and the ORT 1.30 inference worker (ADR-0017): each
/// message is a 4-byte little-endian length followed by that many bytes of UTF-8 JSON, over the
/// worker's standard input and output.
/// </summary>
public static class InferenceWorkerProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 256 * 1024 * 1024;

    public const string Hello = "hello";
    public const string TtsSynthesize = "tts.synthesize";
    public const string Cancel = "cancel";
    public const string Shutdown = "shutdown";
    public const string Result = "result";
    public const string Error = "error";

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Identifies the exact build; the worker must match the application.</summary>
    public static string BuildStamp { get; } =
        typeof(InferenceWorkerProtocol).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    public static async Task WriteAsync(Stream stream, WorkerMessage message, CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);

        // Cancellation is honoured only before the first byte: a frame cut between its length
        // prefix and its body would desynchronise every message after it on the shared pipe.
        cancellationToken.ThrowIfCancellationRequested();
        await stream.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Reads one message, or null when the peer closed the stream.</summary>
    public static async Task<WorkerMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException($"Inference worker message length {length} is out of range.");
        }

        byte[] body = new byte[length];
        if (!await ReadExactlyOrEndAsync(stream, body, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("Inference worker stream ended inside a message.");
        }

        return JsonSerializer.Deserialize<WorkerMessage>(body, JsonOptions)
            ?? throw new InvalidDataException("Inference worker sent an empty message.");
    }

    public static JsonElement ToPayload<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    public static T FromPayload<T>(JsonElement? payload) =>
        payload is { } element
            ? element.Deserialize<T>(JsonOptions) ?? throw new InvalidDataException($"Inference worker payload is not a {typeof(T).Name}.")
            : throw new InvalidDataException($"Inference worker message has no {typeof(T).Name} payload.");

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("Inference worker stream ended inside a message.");
            }

            read += n;
        }

        return true;
    }
}

/// <summary>One request, response or control message. Responses reuse the request id.</summary>
public sealed record WorkerMessage(
    long Id,
    string Kind,
    JsonElement? Payload = null,
    string? Error = null,
    string? ErrorType = null);

public sealed record WorkerHelloResult(
    int ProtocolVersion,
    string BuildStamp,
    string OnnxRuntimeVersion,
    bool CudaAvailable,
    string CudaDetail);

/// <summary>
/// The runtime plan the application's planner selected, including the model paths that
/// <see cref="StageRuntimePlan"/> keeps out of JSON.
/// </summary>
public sealed record WorkerPlan(
    RuntimeStage Stage,
    string? ModelId,
    string? ModelAlias,
    string? EngineFamily,
    string? Variant,
    ExecutionProviderKind ExecutionProvider,
    bool RequirePreferredExecutionProvider,
    string? ModelEntryPath,
    string? ModelRootPath,
    string? ModelEntryRelativePath,
    string? ModelRevisionHash,
    int? DeviceIndex)
{
    public static WorkerPlan From(StageRuntimePlan plan) =>
        new(
            plan.Stage,
            plan.ModelId,
            plan.ModelAlias,
            plan.EngineFamily,
            plan.Variant,
            plan.ExecutionProvider ?? throw new InvalidOperationException("A worker plan needs an execution provider."),
            plan.RequirePreferredExecutionProvider,
            plan.ModelEntryPath,
            plan.ModelRootPath,
            plan.ModelEntryRelativePath,
            plan.ModelRevisionHash,
            plan.DeviceIndex);

    public StageRuntimePlan ToStageRuntimePlan() =>
        new()
        {
            Stage = Stage,
            Status = StageRuntimePlanStatus.Ready,
            ModelId = ModelId,
            ModelAlias = ModelAlias,
            EngineFamily = EngineFamily,
            Variant = Variant,
            ExecutionProvider = ExecutionProvider,
            RequirePreferredExecutionProvider = RequirePreferredExecutionProvider,
            ModelEntryPath = ModelEntryPath,
            ModelRootPath = ModelRootPath,
            ModelEntryRelativePath = ModelEntryRelativePath,
            ModelRevisionHash = ModelRevisionHash,
            DeviceIndex = DeviceIndex,
        };
}

public sealed record WorkerTtsRequest(
    string Text,
    string LanguageCode,
    VoiceCatalogEntry Voice,
    float Speed,
    string? PhonemeOverride,
    double? TargetDurationSeconds,
    WorkerPlan Plan);

public sealed record WorkerTtsResult(
    string WavBase64,
    int DurationSamples,
    int SampleRate,
    string ModelId,
    string VoiceId,
    string Provider,
    StageRuntimeExecutionSummary? ExecutionSummary);
