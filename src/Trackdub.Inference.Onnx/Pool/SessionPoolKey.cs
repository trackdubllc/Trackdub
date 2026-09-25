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

    /// <summary>
    /// Conservative resident estimate when the caller does not supply
    /// <see cref="EstimatedVramMb"/>: 2× model file size (weights + init/activation slack)
    /// with a floor. Unknown/missing files get the floor so admission stays pessimistic.
    /// </summary>
    public const long DefaultEstimatedVramMb = 256;

    internal static long EstimateVramMb(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return DefaultEstimatedVramMb;
        }

        try
        {
            var info = new FileInfo(modelPath);
            if (info.Exists)
            {
                long sizeMb = info.Length / (1024L * 1024L);
                return Math.Max(64L, (sizeMb * 2L) + 128L);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // File is inaccessible or the path is malformed; fall back to the pessimistic default.
        }

        return DefaultEstimatedVramMb;
    }

    /// <summary>Builds a key for a single-session model (graph role = "default").</summary>
    public static SessionPoolKey ForSingle(
        string engineFamily,
        string modelPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null,
        string? optionsFingerprint = null) =>
        new(engineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "default", optionsFingerprint)
        {
            EstimatedVramMb = EstimateVramMb(modelPath),
        };

    /// <summary>Builds an encoder key for a dual-session model.</summary>
    public static SessionPoolKey ForEncoder(
        string engineFamily,
        string encoderPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null,
        string? optionsFingerprint = null) =>
        new(engineFamily, modelId, variant, provider, HashPath(encoderPath), deviceId, "encoder", optionsFingerprint)
        {
            EstimatedVramMb = EstimateVramMb(encoderPath),
        };

    /// <summary>Builds a decoder key for a dual-session model.</summary>
    public static SessionPoolKey ForDecoder(
        string engineFamily,
        string decoderPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null,
        string? optionsFingerprint = null) =>
        new(engineFamily, modelId, variant, provider, HashPath(decoderPath), deviceId, "decoder", optionsFingerprint)
        {
            EstimatedVramMb = EstimateVramMb(decoderPath),
        };

    public static SessionPoolKey ForDecoderInit(
        string engineFamily,
        string decoderInitPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null,
        string? optionsFingerprint = null) =>
        new(engineFamily, modelId, variant, provider, HashPath(decoderInitPath), deviceId, "decoder-init", optionsFingerprint)
        {
            EstimatedVramMb = EstimateVramMb(decoderInitPath),
        };

    public static SessionPoolKey ForDecoderStep(
        string engineFamily,
        string decoderStepPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null,
        string? optionsFingerprint = null) =>
        new(engineFamily, modelId, variant, provider, HashPath(decoderStepPath), deviceId, "decoder-step", optionsFingerprint)
        {
            EstimatedVramMb = EstimateVramMb(decoderStepPath),
        };

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
        new(ChatterboxEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "speech-encoder")
        {
            EstimatedVramMb = EstimateVramMb(modelPath),
        };

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
        new(ChatterboxEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "embed-tokens")
        {
            EstimatedVramMb = EstimateVramMb(modelPath),
        };

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
        new(ChatterboxEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "lm")
        {
            EstimatedVramMb = EstimateVramMb(modelPath),
        };

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
        new(ChatterboxEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "conditional-decoder")
        {
            EstimatedVramMb = EstimateVramMb(modelPath),
        };

    /// <summary>Engine-family constant used by the Chatterbox factory helpers.</summary>
    private const string ChatterboxEngineFamily = "chatterbox";

    // ── LatentSync four-graph helpers ─────────────────────────────────────────

    public static SessionPoolKey ForLatentSyncUNet(
        string modelPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null) =>
        new(LatentSyncEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "unet")
        {
            EstimatedVramMb = EstimateVramMb(modelPath),
        };

    public static SessionPoolKey ForLatentSyncVaeEncoder(
        string modelPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null) =>
        new(LatentSyncEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "vae-encoder")
        {
            EstimatedVramMb = EstimateVramMb(modelPath),
        };

    public static SessionPoolKey ForLatentSyncVaeDecoder(
        string modelPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null) =>
        new(LatentSyncEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "vae-decoder")
        {
            EstimatedVramMb = EstimateVramMb(modelPath),
        };

    public static SessionPoolKey ForLatentSyncWhisperEncoder(
        string modelPath,
        ExecutionProviderKind provider,
        string? modelId = null,
        string? variant = null,
        int? deviceId = null) =>
        new(LatentSyncEngineFamily, modelId, variant, provider, HashPath(modelPath), deviceId, "whisper-encoder")
        {
            EstimatedVramMb = EstimateVramMb(modelPath),
        };

    private const string LatentSyncEngineFamily = "latentsync-diffusion";

    /// <summary>
    /// Total order for multi-graph bundle acquisition. Callers must take bundle leases in
    /// this order or encoder/decoder pairs can deadlock against each other.
    /// </summary>
    public static IComparer<SessionPoolKey> StableComparer { get; } = new StableKeyComparer();

    private sealed class StableKeyComparer : IComparer<SessionPoolKey>
    {
        public int Compare(SessionPoolKey? x, SessionPoolKey? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            int c = CompareGraphIdentity(x, y);
            if (c != 0)
            {
                return c;
            }

            c = string.CompareOrdinal(x.OptionsFingerprint, y.OptionsFingerprint);
            if (c != 0)
            {
                return c;
            }

            c = x.Provider.CompareTo(y.Provider);
            return c != 0 ? c : (x.DeviceId ?? -1).CompareTo(y.DeviceId ?? -1);
        }

        private static int CompareGraphIdentity(SessionPoolKey x, SessionPoolKey y)
        {
            int c = string.CompareOrdinal(x.EngineFamily, y.EngineFamily);
            if (c != 0)
            {
                return c;
            }

            c = string.CompareOrdinal(x.GraphRole, y.GraphRole);
            if (c != 0)
            {
                return c;
            }

            c = string.CompareOrdinal(x.PathHash, y.PathHash);
            if (c != 0)
            {
                return c;
            }

            c = string.CompareOrdinal(x.ModelId ?? string.Empty, y.ModelId ?? string.Empty);
            if (c != 0)
            {
                return c;
            }

            c = string.CompareOrdinal(x.Variant ?? string.Empty, y.Variant ?? string.Empty);
            if (c != 0)
            {
                return c;
            }

            return c;
        }
    }

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
