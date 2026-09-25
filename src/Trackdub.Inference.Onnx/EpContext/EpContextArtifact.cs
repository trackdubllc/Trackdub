using System.Text.Json;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.EpContext;

/// <summary>
/// EP-context artifact paths and machine stamp. A stamp is valid only for the model bytes,
/// GPU architecture, driver, and TRT RTX EP version it was compiled against — the same
/// invalidation triggers as <c>SmokeVerdictKey</c> and the residual engine cache.
/// Load-path checks use size + mtime (cheap) rather than hashing multi-hundred-MB graphs;
/// optional external-data and artifact-sidecar identities are checked the same way.
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
        DateTimeOffset CreatedAtUtc,
        long? ExternalDataLengthBytes = null,
        long? ExternalDataLastWriteUtcTicks = null,
        long? ArtifactExternalInitializersLengthBytes = null,
        long? ArtifactExternalInitializersLastWriteUtcTicks = null)
    {
        public string EnvironmentFingerprint =>
            $"{GpuArchitecture}|{Normalize(DriverVersion)}|{Normalize(TrtRtxEpVersion)}";

        /// <summary>
        /// Validates the compiled artifact's optional external-initializers sidecar identity.
        /// A missing sidecar or a replaced sidecar invalidates the artifact even when the
        /// main ONNX file is unchanged.
        /// </summary>
        public bool MatchesArtifactExternalInitializers(FileInfo? sidecar, bool sidecarRequired = false) =>
            (!sidecarRequired || (sidecar?.Exists ?? false)) &&
            (sidecar?.Exists ?? false) == ArtifactExternalInitializersLengthBytes.HasValue &&
            (!ArtifactExternalInitializersLengthBytes.HasValue ||
                (sidecar is not null &&
                 sidecar.Length == ArtifactExternalInitializersLengthBytes.Value &&
                 sidecar.LastWriteTimeUtc.Ticks == ArtifactExternalInitializersLastWriteUtcTicks));

        /// <summary>
        /// Compares the source model file and, when present, its external-weights sibling
        /// (audit: replacing external data must invalidate the artifact even though the
        /// .onnx container's length and mtime do not change).
        /// </summary>
        public bool MatchesSource(FileInfo source, FileInfo? externalData = null) =>
            source.Length == SourceLengthBytes &&
            source.LastWriteTimeUtc.Ticks == SourceLastWriteUtcTicks &&
            (externalData?.Exists ?? false) == ExternalDataLengthBytes.HasValue &&
            (!ExternalDataLengthBytes.HasValue ||
                (externalData is not null &&
                 externalData.Length == ExternalDataLengthBytes.Value &&
                 externalData.LastWriteTimeUtc.Ticks == ExternalDataLastWriteUtcTicks));

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

    /// <summary>
    /// Sibling external-weights file for <paramref name="sourceModelPath"/>, matching the
    /// <c>&lt;model&gt;.onnx.data</c> convention this codebase writes (see
    /// <c>OliveModelOptimizationService</c>). Not every model has one.
    /// </summary>
    public static string GetSourceExternalDataPath(string sourceModelPath) => sourceModelPath + ".data";

    /// <summary>
    /// External-initializers sidecar ORT writes next to a compiled EP-context artifact when
    /// the artifact is too large to embed (see <see cref="EpContextCompiler"/>). Not every
    /// artifact has one — only models compiled with embedding disabled.
    /// </summary>
    public static string GetArtifactExternalInitializersPath(string epContextPath)
    {
        string? directory = Path.GetDirectoryName(epContextPath);
        string sidecarName = Path.GetFileNameWithoutExtension(epContextPath) + ".ext_init";
        return string.IsNullOrEmpty(directory) ? sidecarName : Path.Join(directory, sidecarName);
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

        return HasMatchingSourceStamp(sourceModelPath, epContextPath, stampPath, currentEnvironmentFingerprint);
    }

    private static string? HasMatchingSourceStamp(
        string sourceModelPath,
        string epContextPath,
        string stampPath,
        string currentEnvironmentFingerprint)
    {
        Stamp? stamp = TryReadStamp(stampPath);
        if (stamp is null ||
            !stamp.EnvironmentFingerprint.Equals(currentEnvironmentFingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            string externalDataPath = GetSourceExternalDataPath(sourceModelPath);
            FileInfo? externalData = File.Exists(externalDataPath) ? new FileInfo(externalDataPath) : null;
            string sidecarPath = GetArtifactExternalInitializersPath(epContextPath);
            FileInfo? sidecar = File.Exists(sidecarPath) ? new FileInfo(sidecarPath) : null;
            return stamp.MatchesSource(new FileInfo(sourceModelPath), externalData) &&
                stamp.MatchesArtifactExternalInitializers(
                    sidecar,
                    sidecarRequired: !ShouldEmbedEpContext(sourceModelPath))
                ? epContextPath
                : null;
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
        string? driverVersion)
    {
        string externalDataPath = GetSourceExternalDataPath(sourceModelPath);
        var externalData = new FileInfo(externalDataPath);
        string epContextPath = GetEpContextPath(sourceModelPath);
        var artifactSidecar = new FileInfo(GetArtifactExternalInitializersPath(epContextPath));
        return new(
            SchemaVersion: 1,
            SourceFileName: Path.GetFileName(sourceModelPath),
            SourceLengthBytes: source.Length,
            SourceLastWriteUtcTicks: source.LastWriteTimeUtc.Ticks,
            SourceSha256: sourceSha256,
            GpuArchitecture: gpuArchitecture,
            DriverVersion: driverVersion,
            TrtRtxEpVersion: TensorRtRtxProviderConstants.BundledFingerprintVersion,
            ExternalDataLengthBytes: externalData.Exists ? externalData.Length : null,
            ExternalDataLastWriteUtcTicks: externalData.Exists ? externalData.LastWriteTimeUtc.Ticks : null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ArtifactExternalInitializersLengthBytes: artifactSidecar.Exists ? artifactSidecar.Length : null,
            ArtifactExternalInitializersLastWriteUtcTicks: artifactSidecar.Exists ? artifactSidecar.LastWriteTimeUtc.Ticks : null);
    }

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
