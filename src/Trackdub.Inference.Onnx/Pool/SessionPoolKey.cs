using System.Security.Cryptography;
using System.Text;
using Trackdub.Domain;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Composite key that uniquely identifies a pooled ONNX <see cref="Microsoft.ML.OnnxRuntime.InferenceSession"/>.
/// All properties participate in equality and hash code so pool lookups are exact.
/// </summary>
/// <remarks>
/// <para><strong>EngineFamily:</strong>
/// Logical engine family (e.g. "kokoro", "whisper-onnx", "opus-mt", "chatterbox", "silero-vad").
/// Stored lowercase; any uppercase input is normalised on construction.</para>
/// <para><strong>ModelId:</strong>
/// Optional manifest model ID (e.g. "onnx-community/Kokoro-82M-v1.0-ONNX").
/// Stored lowercase; any uppercase input is normalised on construction.</para>
/// <para><strong>Variant:</strong>
/// Optional model variant / quantization tag (e.g. "q4", "fp16").
/// Stored lowercase; any uppercase input is normalised on construction.</para>
/// <para><strong>Provider:</strong> Execution provider used to create the session.</para>
/// <para><strong>PathHash:</strong>
/// Stable, lowercase hex SHA-256 of the model file path (not file content).
/// On Windows, casing is normalised before hashing; on macOS and Linux the path is hashed as-is
/// to handle case-sensitive APFS and ext4 volumes correctly.
/// Separators and relative segments are not canonicalised.
/// Use <see cref="HashPath"/> to derive this value from a model file path.</para>
/// <para><strong>DeviceId:</strong>
/// Device ordinal (0 for the default device). Used to distinguish sessions on different GPUs.</para>
/// <para><strong>GraphRole:</strong>
/// Role of this session within a multi-graph model. Stored lowercase; input is normalised on construction.
/// Conventional values: <c>"default"</c> (single-session models),
/// <c>"encoder"</c>, <c>"decoder"</c> (Whisper / Opus-MT / MADLAD),
/// <c>"speech-encoder"</c>, <c>"embed-tokens"</c>, <c>"lm"</c>, <c>"conditional-decoder"</c> (Chatterbox).
/// </para>
/// <para><strong>OptionsFingerprint:</strong>
/// Stable hash of session provider options that affect ONNX Runtime session construction.</para>
/// </remarks>
internal sealed record SessionPoolKey
{
    private const string DefaultOptionsFingerprint = "default";

    // Normalise string discriminators to lowercase so record equality is case-insensitive
    // and consistent with the case-insensitive matching in EvictModelAsync.
    private string _engineFamily = null!;
    private string? _modelId;
    private string? _variant;
    private string _pathHash = null!;
    private string _graphRole = null!;
    private string _optionsFingerprint = null!;

    public SessionPoolKey(
        string engineFamily,
        string? modelId,
        string? variant,
        ExecutionProviderKind provider,
        string pathHash,
        int? deviceId,
        string graphRole,
        string? optionsFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        ArgumentNullException.ThrowIfNull(pathHash);
        ArgumentNullException.ThrowIfNull(graphRole);

        _engineFamily = engineFamily.ToLowerInvariant();
        ModelId = modelId;
        Variant = variant;
        Provider = provider;
        _pathHash = pathHash.ToLowerInvariant();
        DeviceId = deviceId;
        _graphRole = graphRole.ToLowerInvariant();
        _optionsFingerprint = string.IsNullOrWhiteSpace(optionsFingerprint)
            ? DefaultOptionsFingerprint
            : optionsFingerprint.ToLowerInvariant();
    }

    public string EngineFamily
    {
        get => _engineFamily;
        init => _engineFamily = (value ?? throw new System.ArgumentNullException(nameof(EngineFamily))).ToLowerInvariant();
    }

    public string? ModelId
    {
        get => _modelId;
        init => _modelId = value?.ToLowerInvariant();
    }

    public string? Variant
    {
        get => _variant;
        init => _variant = value?.ToLowerInvariant();
    }

    public string PathHash
    {
        get => _pathHash;
        init => _pathHash = (value ?? throw new System.ArgumentNullException(nameof(PathHash))).ToLowerInvariant();
    }

    public string GraphRole
    {
        get => _graphRole;
        init => _graphRole = (value ?? throw new System.ArgumentNullException(nameof(GraphRole))).ToLowerInvariant();
    }

    public string OptionsFingerprint
    {
        get => _optionsFingerprint;
        init => _optionsFingerprint = string.IsNullOrWhiteSpace(value)
            ? DefaultOptionsFingerprint
            : value.ToLowerInvariant();
    }

    public ExecutionProviderKind Provider { get; init; }

    public int? DeviceId { get; init; }

    /// <summary>Estimated VRAM footprint of this session in MB. Used for VRAM-budget eviction.</summary>
    public long EstimatedVramMb { get; init; } = 0;

    /// <summary>Builds a key for a single-session model (graph role = "default").</summary>
    public static SessionPoolKey ForSingle(
        string engineFamily,
        string modelPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null,
        string? optionsFingerprint = null) =>
        new(engineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "default", optionsFingerprint);

    /// <summary>Builds an encoder key for a dual-session model.</summary>
    public static SessionPoolKey ForEncoder(
        string engineFamily,
        string encoderPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null,
        string? optionsFingerprint = null) =>
        new(engineFamily, modelId, variant, provider, HashPath(encoderPath), deviceId, "encoder", optionsFingerprint);

    /// <summary>Builds a decoder key for a dual-session model.</summary>
    public static SessionPoolKey ForDecoder(
        string engineFamily,
        string decoderPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null,
        string? optionsFingerprint = null) =>
        new(engineFamily, modelId, variant, provider, HashPath(decoderPath), deviceId, "decoder", optionsFingerprint);

    public static SessionPoolKey ForDecoderInit(
        string engineFamily,
        string decoderInitPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null,
        string? optionsFingerprint = null) =>
        new(engineFamily, modelId, variant, provider, HashPath(decoderInitPath), deviceId, "decoder-init", optionsFingerprint);

    public static SessionPoolKey ForDecoderStep(
        string engineFamily,
        string decoderStepPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null,
        string? optionsFingerprint = null) =>
        new(engineFamily, modelId, variant, provider, HashPath(decoderStepPath), deviceId, "decoder-step", optionsFingerprint);

    // ── Chatterbox four-graph helpers ─────────────────────────────────────────

    /// <summary>
    /// Builds a speech-encoder key for the Chatterbox model
    /// (graph role = <c>"speech-encoder"</c>).
    /// The speech encoder processes the reference audio clip to produce conditioning vectors.
    /// </summary>
    public static SessionPoolKey ForChatterboxSpeechEncoder(
        string modelPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null) =>
        new(ChatterboxEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "speech-encoder");

    /// <summary>
    /// Builds an embed-tokens key for the Chatterbox model
    /// (graph role = <c>"embed-tokens"</c>).
    /// The embedding graph converts text token IDs into dense embedding vectors.
    /// </summary>
    public static SessionPoolKey ForChatterboxEmbedTokens(
        string modelPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null) =>
        new(ChatterboxEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "embed-tokens");

    /// <summary>
    /// Builds a language-model key for the Chatterbox model
    /// (graph role = <c>"lm"</c>).
    /// The LM auto-regressively generates speech tokens from text and conditioning.
    /// </summary>
    public static SessionPoolKey ForChatterboxLanguageModel(
        string modelPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null) =>
        new(ChatterboxEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "lm");

    /// <summary>
    /// Builds a conditional-decoder key for the Chatterbox model
    /// (graph role = <c>"conditional-decoder"</c>).
    /// The conditional decoder converts speech tokens into audio waveform samples.
    /// </summary>
    public static SessionPoolKey ForChatterboxConditionalDecoder(
        string modelPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null) =>
        new(ChatterboxEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "conditional-decoder");

    /// <summary>Engine-family constant used by the Chatterbox factory helpers.</summary>
    private const string ChatterboxEngineFamily = "chatterbox";

    // ── LatentSync four-graph helpers ─────────────────────────────────────────

    public static SessionPoolKey ForLatentSyncUNet(
        string modelPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null) =>
        new(LatentSyncEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "unet");

    public static SessionPoolKey ForLatentSyncVaeEncoder(
        string modelPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null) =>
        new(LatentSyncEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "vae-encoder");

    public static SessionPoolKey ForLatentSyncVaeDecoder(
        string modelPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null) =>
        new(LatentSyncEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "vae-decoder");

    public static SessionPoolKey ForLatentSyncWhisperEncoder(
        string modelPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null) =>
        new(LatentSyncEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "whisper-encoder");

    private const string LatentSyncEngineFamily = "latentsync-diffusion";

    /// <summary>
    /// Computes a stable, lowercase hex SHA-256 hash of the given model file path.
    /// On Windows, paths are typically case-insensitive, so casing is normalised before
    /// hashing by default; <c>C:\Models\Model.onnx</c> and <c>c:\models\model.onnx</c>
    /// resolve to the same pool slot.
    /// Windows also supports per-directory case sensitivity (via
    /// <c>fsutil file setCaseSensitiveInfo</c> or WSL2 volume mounts). Set the
    /// <c>Trackdub.Inference.Onnx.SessionPoolKey.PreserveWindowsPathCase</c>
    /// <see cref="AppContext"/> switch to <see langword="true"/> before hashing to preserve
    /// Windows path casing in deployments that rely on distinct case-sensitive paths.
    /// On macOS and Linux the path is hashed as-is to preserve case-sensitive path
    /// identity: macOS APFS volumes may be formatted as either case-sensitive or
    /// case-insensitive, so normalising on macOS could conflate distinct files on
    /// case-sensitive volumes.
    /// Note: only casing may be normalised; path separators and relative segments are not
    /// canonicalised.
    /// </summary>
    public static string HashPath(string modelPath)
    {
        ArgumentNullException.ThrowIfNull(modelPath);
        // Normalise case on Windows by default because most Windows model paths are
        // case-insensitive. Deployments using case-sensitive Windows directories can opt out.
        // On macOS and Linux, paths are hashed as provided.
        string normalised = OperatingSystem.IsWindows() && !PreserveWindowsPathCase()
            ? modelPath.ToUpperInvariant()
            : modelPath;
        byte[] bytes = Encoding.UTF8.GetBytes(normalised);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Computes a stable lowercase hash for provider options. Dictionary enumeration order
    /// does not affect the result.
    /// </summary>
    public static string HashOptions(IReadOnlyDictionary<string, string>? options)
    {
        if (options is null || options.Count == 0)
        {
            return DefaultOptionsFingerprint;
        }

        var builder = new StringBuilder();
        foreach ((string key, string value) in options.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder
                .Append(key.Length)
                .Append(':')
                .Append(key)
                .Append('=')
                .Append(value.Length)
                .Append(':')
                .Append(value)
                .Append(';');
        }

        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool PreserveWindowsPathCase()
    {
        AppContext.TryGetSwitch(
            "Trackdub.Inference.Onnx.SessionPoolKey.PreserveWindowsPathCase",
            out bool preserveWindowsPathCase);
        return preserveWindowsPathCase;
    }
}
