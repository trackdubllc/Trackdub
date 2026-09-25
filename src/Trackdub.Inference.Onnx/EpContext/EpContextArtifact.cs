using System.Text.Json;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.EpContext;

/// <summary>
/// EP-context artifact paths and machine stamp. A stamp is valid only for the model bytes,
/// GPU architecture, driver, and TRT RTX EP version it was compiled against — the same
/// invalidation triggers as <c>SmokeVerdictKey</c> and the residual engine cache.
/// Load-path checks use size + mtime (cheap) rather than hashing multi-hundred-MB graphs.
/// </summary>
public static class EpContextArtifact
{
    public const string EpContextSuffix = ".epc";
    private const int LargeModelEmbedThresholdBytes = 2_000_000_000;

    public sealed record Stamp(
        int SchemaVersion,
        string SourceFileName,
        long SourceLengthBytes,
        long SourceLastWriteUtcTicks,
        string? SourceSha256,
        string GpuArchitecture,
        string? DriverVersion,
        string? TrtRtxEpVersion,
        DateTimeOffset CreatedAtUtc)
    {
        public string EnvironmentFingerprint =>
            $"{GpuArchitecture}|{Normalize(DriverVersion)}|{Normalize(TrtRtxEpVersion)}";

        public bool MatchesSource(FileInfo source) =>
            source.Length == SourceLengthBytes &&
            source.LastWriteTimeUtc.Ticks == SourceLastWriteUtcTicks;

        private static string Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
    }

    public static bool IsEpContextPath(string path) =>
        path.EndsWith(EpContextSuffix + ".onnx", StringComparison.OrdinalIgnoreCase);

    public static string GetEpContextPath(string sourceModelPath)
    {
        string? directory = Path.GetDirectoryName(sourceModelPath);
        string name = Path.GetFileNameWithoutExtension(sourceModelPath);
        return directory is null
            ? name + EpContextSuffix + ".onnx"
            : Path.Join(directory, name + EpContextSuffix + ".onnx");
    }

    public static string GetStampPath(string sourceModelPath)
    {
        string epContextPath = GetEpContextPath(sourceModelPath);
        return epContextPath[..^".onnx".Length] + ".stamp.json";
    }

    public static bool ShouldEmbedEpContext(string sourceModelPath)
    {
        try
        {
            return new FileInfo(sourceModelPath).Length < LargeModelEmbedThresholdBytes;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Returns a loadable EP-context path when an artifact exists and its stamp matches the
    /// current machine fingerprint and source identity; otherwise <see langword="null"/>.
    /// </summary>
    public static string? TryResolveValidLoadPath(string sourceModelPath, string currentEnvironmentFingerprint)
    {
        if (IsEpContextPath(sourceModelPath) || !File.Exists(sourceModelPath))
        {
            return null;
        }

        string epContextPath = GetEpContextPath(sourceModelPath);
        string stampPath = GetStampPath(sourceModelPath);
        if (!File.Exists(epContextPath) || !File.Exists(stampPath))
        {
            return null;
        }

        Stamp? stamp = TryReadStamp(stampPath);
        if (stamp is null ||
            !stamp.EnvironmentFingerprint.Equals(currentEnvironmentFingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return stamp.MatchesSource(new FileInfo(sourceModelPath)) ? epContextPath : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static Stamp CreateStamp(
        string sourceModelPath,
        FileInfo source,
        string? sourceSha256,
        string gpuArchitecture,
        string? driverVersion) =>
        new(
            SchemaVersion: 1,
            SourceFileName: Path.GetFileName(sourceModelPath),
            SourceLengthBytes: source.Length,
            SourceLastWriteUtcTicks: source.LastWriteTimeUtc.Ticks,
            SourceSha256: sourceSha256,
            GpuArchitecture: gpuArchitecture,
            DriverVersion: driverVersion,
            TrtRtxEpVersion: TensorRtRtxProviderConstants.BundledFingerprintVersion,
            CreatedAtUtc: DateTimeOffset.UtcNow);

    public static void WriteStamp(string sourceModelPath, Stamp stamp)
    {
        string stampPath = GetStampPath(sourceModelPath);
        string? directory = Path.GetDirectoryName(stampPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(stampPath, JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Stamp? TryReadStamp(string stampPath)
    {
        try
        {
            return JsonSerializer.Deserialize<Stamp>(File.ReadAllText(stampPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static string ComputeSha256(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }
}
