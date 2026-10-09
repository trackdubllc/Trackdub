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
        long? ArtifactExternalInitializersLastWriteUtcTicks = null,
        IReadOnlyList<ArtifactFile>? ArtifactFiles = null)
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

        /// <summary>
        /// Checks the engine files a non-embedded artifact loads from its own directory. Stamps
        /// written before this list existed carry <see langword="null"/> and check nothing here.
        /// </summary>
        public bool MatchesArtifactFiles(string artifactDirectory) =>
            ArtifactFiles is null ||
            ArtifactFiles.All(file =>
            {
                var info = new FileInfo(Path.Join(artifactDirectory, file.Name));
                return info.Exists && info.Length == file.LengthBytes;
            });

        private static string Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
    }

    /// <summary>A file published beside the EP-context model, such as a serialized engine.</summary>
    public sealed record ArtifactFile(string Name, long LengthBytes);

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
    /// Sibling external-weights file for <paramref name="sourceModelPath"/>: the
    /// <c>&lt;model&gt;.onnx.data</c> convention this codebase writes (see
    /// <c>OliveModelOptimizationService</c>), or the <c>&lt;model&gt;.onnx_data</c> convention of
    /// Optimum exports when only that exists. Not every model has one.
    /// </summary>
    public static string GetSourceExternalDataPath(string sourceModelPath)
    {
        string dotData = sourceModelPath + ".data";
        string underscoreData = sourceModelPath + "_data";
        return !File.Exists(dotData) && File.Exists(underscoreData) ? underscoreData : dotData;
    }

    /// <summary>Bytes of the model file plus its external weights, which the engine roughly mirrors.</summary>
    public static long GetSourceTotalBytes(string sourceModelPath)
    {
        var source = new FileInfo(sourceModelPath);
        var externalData = new FileInfo(GetSourceExternalDataPath(sourceModelPath));
        return (source.Exists ? source.Length : 0) + (externalData.Exists ? externalData.Length : 0);
    }

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

    /// <summary>
    /// Embeds the engine in the EP-context graph only while it fits protobuf's 2 GB limit. The
    /// engine roughly mirrors the weights, so models whose weights live in external data count
    /// those bytes too.
    /// </summary>
    public static bool ShouldEmbedEpContext(string sourceModelPath)
    {
        try
        {
            return GetSourceTotalBytes(sourceModelPath) < LargeModelEmbedThresholdBytes;
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
            // Stamps that predate ArtifactFiles cannot say what a non-embedded artifact needs, so
            // they keep requiring the external-initializers sidecar.
            return stamp.MatchesSource(new FileInfo(sourceModelPath), externalData) &&
                stamp.MatchesArtifactExternalInitializers(
                    sidecar,
                    sidecarRequired: stamp.ArtifactFiles is null && !ShouldEmbedEpContext(sourceModelPath)) &&
                stamp.MatchesArtifactFiles(Path.GetDirectoryName(epContextPath) ?? string.Empty)
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
        string? driverVersion,
        IReadOnlyList<ArtifactFile>? artifactFiles = null)
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
            ArtifactExternalInitializersLastWriteUtcTicks: artifactSidecar.Exists ? artifactSidecar.LastWriteTimeUtc.Ticks : null,
            ArtifactFiles: artifactFiles);
    }

    /// <summary>
    /// Moves a freshly compiled artifact from <paramref name="stagingDirectory"/> (where it was
    /// written under its final file names) next to <paramref name="sourceModelPath"/>, replacing
    /// any previous artifact. The old stamp goes first so no reader validates a half-replaced
    /// set, and the EP-context model goes last, after the engine files it references. Returns the
    /// published engine files (everything except the EP-context model and its
    /// external-initializers sidecar) for the new stamp.
    /// </summary>
    public static IReadOnlyList<ArtifactFile> PublishStagedArtifact(string stagingDirectory, string sourceModelPath)
    {
        string epContextPath = GetEpContextPath(sourceModelPath);
        string targetDirectory = Path.GetDirectoryName(Path.GetFullPath(epContextPath))!;
        string epContextName = Path.GetFileName(epContextPath);
        string sidecarName = Path.GetFileName(GetArtifactExternalInitializersPath(epContextPath));

        DeleteArtifact(sourceModelPath);

        var published = new List<ArtifactFile>();
        foreach (string stagedPath in Directory.EnumerateFiles(stagingDirectory))
        {
            string name = Path.GetFileName(stagedPath);
            if (name.Equals(epContextName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(sidecarName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long length = new FileInfo(stagedPath).Length;
            File.Move(stagedPath, Path.Join(targetDirectory, name), overwrite: true);
            published.Add(new ArtifactFile(name, length));
        }

        string stagedSidecar = Path.Join(stagingDirectory, sidecarName);
        if (File.Exists(stagedSidecar))
        {
            File.Move(stagedSidecar, Path.Join(targetDirectory, sidecarName), overwrite: true);
        }

        File.Move(Path.Join(stagingDirectory, epContextName), epContextPath, overwrite: true);
        return published;
    }

    /// <summary>Removes the artifact for <paramref name="sourceModelPath"/>: stamp first, then its files.</summary>
    public static void DeleteArtifact(string sourceModelPath)
    {
        string epContextPath = GetEpContextPath(sourceModelPath);
        string stampPath = GetStampPath(sourceModelPath);
        Stamp? stamp = File.Exists(stampPath) ? TryReadStamp(stampPath) : null;
        TryDeleteFile(stampPath);
        string directory = Path.GetDirectoryName(Path.GetFullPath(epContextPath))!;
        foreach (ArtifactFile file in stamp?.ArtifactFiles ?? [])
        {
            // Recorded names come from our own staging directory; never follow a path out of it.
            if (Path.GetFileName(file.Name) == file.Name)
            {
                TryDeleteFile(Path.Join(directory, file.Name));
            }
        }

        TryDeleteFile(GetArtifactExternalInitializersPath(epContextPath));
        TryDeleteFile(epContextPath);
    }

    public static void WriteStamp(string sourceModelPath, Stamp stamp)
    {
        string stampPath = GetStampPath(sourceModelPath);
        string? directory = Path.GetDirectoryName(stampPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true });
        string tempPath = stampPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, json);
            try
            {
                File.Move(tempPath, stampPath, overwrite: true);
            }
            catch (IOException)
            {
                if (File.Exists(stampPath))
                {
                    File.Delete(stampPath);
                }

                File.Move(tempPath, stampPath);
            }
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"EpContextArtifact: failed to delete temp file '{path}': {ex.Message}");
        }
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
