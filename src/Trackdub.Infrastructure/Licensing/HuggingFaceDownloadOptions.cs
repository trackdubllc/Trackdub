namespace Trackdub.Infrastructure.Licensing;

/// <summary>
/// Hugging Face download acceleration settings. Reads Trackdub-specific env vars and
/// standard HF Hub variables so CLI/Python tooling and Trackdub share the same knobs.
/// </summary>
public sealed record HuggingFaceDownloadOptions
{
    public const string ParallelDownloadsEnv = "TRACKDUB_HF_PARALLEL_DOWNLOADS";
    public const string MaxConnectionsEnv = "TRACKDUB_HF_MAX_CONNECTIONS";
    public const string DisableXetEnv = "TRACKDUB_HF_DISABLE_XET";
    public const string UseCliEnv = "TRACKDUB_HF_USE_CLI";
    public const string ChunkSizeMbEnv = "TRACKDUB_HF_CHUNK_SIZE_MB";
    public const string CliTransferEnv = "TRACKDUB_HF_CLI_TRANSFER";

    public const string HubEnableTransferEnv = "HF_HUB_ENABLE_HF_TRANSFER";
    public const string HubDisableXetEnv = "HF_HUB_DISABLE_XET";
    public const string HubDisableProgressBarsEnv = "HF_HUB_DISABLE_PROGRESS_BARS";
    public const string PythonUtf8Env = "PYTHONUTF8";
    public const string PythonIoEncodingEnv = "PYTHONIOENCODING";

    public static HuggingFaceDownloadOptions Default { get; } = new();

    public bool ParallelDownloadsEnabled { get; init; } = true;

    public int MaxParallelConnections { get; init; } = 8;

    public long MinFileSizeForParallelBytes { get; init; } = 4 * 1024 * 1024;

    public long ChunkSizeBytes { get; init; } = 16 * 1024 * 1024;

    public bool DisableXet { get; init; } = true;

    /// <summary>
    /// When true, the HF CLI process receives <c>HF_HUB_ENABLE_HF_TRANSFER=1</c>.
    /// Off by default: native hf_transfer can OOM-kill the host process. Trackdub parallel
    /// HTTP downloads use <see cref="ParallelDownloadsEnabled"/> separately.
    /// </summary>
    public bool EnableCliTransfer { get; init; }

    /// <summary>
    /// <c>auto</c> uses the <c>hf</c> CLI when it is on PATH; <c>true</c> requires it; <c>false</c> never uses it.
    /// </summary>
    public HuggingFaceCliPreference CliPreference { get; init; } = HuggingFaceCliPreference.Auto;

    public static HuggingFaceDownloadOptions FromEnvironment() =>
        new()
        {
            ParallelDownloadsEnabled = ReadParallelEnabled(),
            MaxParallelConnections = ReadPositiveInt(MaxConnectionsEnv, defaultValue: 8, min: 1, max: 32),
            MinFileSizeForParallelBytes = 4 * 1024 * 1024,
            ChunkSizeBytes = ReadPositiveInt(ChunkSizeMbEnv, defaultValue: 16, min: 1, max: 256) * 1024L * 1024L,
            DisableXet = ReadDisableXet(),
            EnableCliTransfer = ReadCliTransferEnabled(),
            CliPreference = ReadCliPreference(),
        };

    public HuggingFaceDownloadDiagnostics Describe()
    {
        string? cliExecutable = HuggingFaceCliLocator.TryResolveExecutable();
        bool cliAvailable = CliPreference != HuggingFaceCliPreference.Never && cliExecutable is not null;
        return new(
            ParallelDownloadsEnabled,
            MaxParallelConnections,
            DisableXet,
            CliPreference,
            cliAvailable,
            cliExecutable,
            EnableCliTransfer);
    }

    public IReadOnlyDictionary<string, string> BuildHubCliEnvironmentVariables()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PythonUtf8Env] = "1",
            [PythonIoEncodingEnv] = "utf-8",
            [HubDisableProgressBarsEnv] = "1",
            // Always set explicitly so an inherited HF_HUB_ENABLE_HF_TRANSFER=1 cannot
            // override Trackdub's opt-in CLI transfer policy (default off).
            [HubEnableTransferEnv] = EnableCliTransfer ? "1" : "0",
        };

        if (DisableXet)
        {
            values[HubDisableXetEnv] = "1";
        }

        return values;
    }

    private static bool ReadParallelEnabled()
    {
        if (TryReadBool(ParallelDownloadsEnv, out bool trackdubValue))
        {
            return trackdubValue;
        }

        // Legacy: HF_HUB_ENABLE_HF_TRANSFER used to mean "prefer parallel downloads".
        // It no longer enables native CLI hf_transfer (see EnableCliTransfer / CliTransferEnv).
        if (TryReadBool(HubEnableTransferEnv, out bool hubValue))
        {
            return hubValue;
        }

        return true;
    }

    private static bool ReadCliTransferEnabled()
    {
        if (TryReadBool(CliTransferEnv, out bool trackdubValue))
        {
            return trackdubValue;
        }

        return false;
    }

    private static bool ReadDisableXet()
    {
        if (TryReadBool(DisableXetEnv, out bool trackdubValue))
        {
            return trackdubValue;
        }

        if (TryReadBool(HubDisableXetEnv, out bool hubValue))
        {
            return hubValue;
        }

        return true;
    }

    private static HuggingFaceCliPreference ReadCliPreference()
    {
        string? raw = Environment.GetEnvironmentVariable(UseCliEnv);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return HuggingFaceCliPreference.Auto;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" or "require" or "required" => HuggingFaceCliPreference.Required,
            "0" or "false" or "no" or "off" or "never" or "disabled" => HuggingFaceCliPreference.Never,
            _ => HuggingFaceCliPreference.Auto,
        };
    }

    private static int ReadPositiveInt(string name, int defaultValue, int min, int max)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int parsed) && parsed >= min
            ? Math.Min(parsed, max)
            : defaultValue;
    }

    private static bool TryReadBool(string name, out bool value)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = false;
            return false;
        }

        value = raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => false,
        };
        return true;
    }
}

public enum HuggingFaceCliPreference
{
    Auto,
    Required,
    Never,
}

public sealed record HuggingFaceDownloadDiagnostics(
    bool ParallelDownloadsEnabled,
    int MaxParallelConnections,
    bool DisableXet,
    HuggingFaceCliPreference CliPreference,
    bool CliAvailable,
    string? CliExecutable,
    bool EnableCliTransfer = false);
