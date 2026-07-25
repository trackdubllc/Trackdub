using Trackdub.Contracts;

namespace Trackdub.Media.Process;

public sealed class FfmpegVideoEncoderCapabilityService : IFfmpegVideoEncoderCapabilities
{
    private static readonly ProcessRunOptions ProbeProcessOptions = new(Timeout: TimeSpan.FromSeconds(15));
    private static readonly string[] EncoderTokens =
    [
        "h264_nvenc",
        "hevc_nvenc",
        "h264_qsv",
        "hevc_qsv",
        "h264_amf",
        "hevc_amf",
        "h264_videotoolbox",
        "hevc_videotoolbox",
        "h264_vaapi",
        "hevc_vaapi",
        "libx264"
    ];

    private static readonly string[] HwAccelTokens =
    [
        "d3d11va",
        "dxva2",
        "cuda",
        "vaapi",
        "videotoolbox"
    ];

    private readonly IProcessRunner processRunner;
    private readonly FfmpegToolResolver toolResolver;
    private readonly Lock gate = new();
    private FfmpegVideoEncoderSnapshot cachedSnapshot = FfmpegVideoEncoderSnapshot.Empty;
    private string? cachedFfmpegPath;

    public FfmpegVideoEncoderCapabilityService(string? ffmpegPath = null)
        : this(new ProcessRunner(), ffmpegPath)
    {
    }

    internal FfmpegVideoEncoderCapabilityService(IProcessRunner processRunner, string? ffmpegPath = null)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        toolResolver = new FfmpegToolResolver(ffmpegPath);
    }

    public FfmpegVideoEncoderSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return cachedSnapshot.ProbedAtUtc != DateTimeOffset.MinValue
                ? cachedSnapshot
                : FfmpegVideoEncoderSnapshot.Empty;
        }
    }

    public async Task<FfmpegVideoEncoderSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        bool allowAutoDownload = OperatingSystem.IsWindows();
        string ffmpegPath = toolResolver.ResolveFfmpegPath(allowAutoDownload: allowAutoDownload);
        ProcessResult encodersResult = await processRunner.RunAsync(
            ffmpegPath,
            ["-hide_banner", "-encoders"],
            cancellationToken,
            ProbeProcessOptions).ConfigureAwait(false);
        ProcessResult hwaccelsResult = await processRunner.RunAsync(
            ffmpegPath,
            ["-hide_banner", "-hwaccels"],
            cancellationToken,
            ProbeProcessOptions).ConfigureAwait(false);

        var encoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hwaccels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (encodersResult.ExitCode == 0)
        {
            CollectTokens(encodersResult.StandardOutput, EncoderTokens, encoders);
        }

        if (hwaccelsResult.ExitCode == 0)
        {
            CollectTokens(hwaccelsResult.StandardOutput, HwAccelTokens, hwaccels);
        }

        FfmpegVideoEncoderSnapshot snapshot = new(
            encoders,
            hwaccels,
            DateTimeOffset.UtcNow);

        lock (gate)
        {
            cachedFfmpegPath = ffmpegPath;
            cachedSnapshot = snapshot;
        }

        return snapshot;
    }

    private static void CollectTokens(string output, IEnumerable<string> tokens, ISet<string> destination)
    {
        foreach (string token in tokens)
        {
            if (output.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                destination.Add(token);
            }
        }
    }
}
