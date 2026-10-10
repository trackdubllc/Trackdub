using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.EpContext;

/// <summary>
/// Captures an EP-context artifact while a TensorRT RTX session builds its engines from the source
/// graph, so later processes deserialize the engines instead of rebuilding them. The capture rides
/// on the build the session performs anyway; only writing the engine is extra. Measured on
/// MADLAD-400 3B fp16 (RTX 5070): the encoder session takes ~48 s to build from source and 3.6 s to
/// load from its EP-context artifact.
/// </summary>
internal static class EpContextCapture
{
    /// <summary>Below this many model bytes, engine builds are short and an artifact saves little.</summary>
    internal const long MinimumSourceBytes = 256L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, string> FailedSources = new(StringComparer.OrdinalIgnoreCase);

    internal static bool ShouldCapture(string sourceModelPath)
    {
        string fullPath = Path.GetFullPath(sourceModelPath);
        if (EpContextArtifact.IsEpContextPath(fullPath) || FailedSources.ContainsKey(fullPath))
        {
            return false;
        }

        long sourceBytes = EpContextArtifact.GetSourceTotalBytes(fullPath);
        // The engine roughly mirrors the weights; keep at least that much free again afterwards.
        return sourceBytes >= MinimumSourceBytes && HasFreeSpace(fullPath, sourceBytes * 2);
    }

    /// <summary>
    /// Creates the session with EP-context generation enabled, then publishes the artifact next to
    /// <paramref name="sourceModelPath"/>. A capture problem never costs the session: creation is
    /// retried once without capture for capture I/O failures; native engine failures propagate.
    /// </summary>
    internal static InferenceSession CreateSession(
        string sourceModelPath,
        SessionOptions options,
        Func<string, SessionOptions, InferenceSession> createSession,
        IReadOnlyDictionary<string, string>? providerOptions = null)
    {
        string fullSource = Path.GetFullPath(sourceModelPath);
        string epContextPath = EpContextArtifact.GetEpContextPath(fullSource);
        string stagingDirectory = Path.Join(
            Path.GetDirectoryName(epContextPath)!,
            ".epc-capture-" + Guid.NewGuid().ToString("N"));

        InferenceSession session;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            options.AddSessionConfigEntry("ep.context_enable", "1");
            options.AddSessionConfigEntry("ep.context_file_path", Path.Join(stagingDirectory, Path.GetFileName(epContextPath)));
            options.AddSessionConfigEntry("ep.context_embed_mode", EpContextArtifact.ShouldEmbedEpContext(fullSource) ? "1" : "0");
            session = createSession(sourceModelPath, options);
        }
        catch (Exception ex) when (ShouldRetryWithoutCapture(ex))
        {
            MarkFailed(fullSource, ex.Message);
            EpContextCompiler.TryDeleteDirectory(stagingDirectory);
            options.AddSessionConfigEntry("ep.context_enable", "0");
            return createSession(sourceModelPath, options);
        }
        catch (OnnxRuntimeException ex)
        {
            MarkFailed(fullSource, ex.Message);
            EpContextCompiler.TryDeleteDirectory(stagingDirectory);
            throw;
        }

        try
        {
            Publish(fullSource, stagingDirectory, providerOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MarkFailed(fullSource, ex.Message);
        }
        finally
        {
            EpContextCompiler.TryDeleteDirectory(stagingDirectory);
        }

        return session;
    }

    private static void Publish(
        string sourceModelPath,
        string stagingDirectory,
        IReadOnlyDictionary<string, string>? providerOptions)
    {
        string stagedModel = Path.Join(stagingDirectory, Path.GetFileName(EpContextArtifact.GetEpContextPath(sourceModelPath)));
        // A graph the EP did not claim yields a plain reserialized model that would not skip the
        // engine build; keep nothing rather than an artifact that only adds parse overhead.
        if (!File.Exists(stagedModel) || !EpContextCompiler.TryContainsEpContextNodes(stagedModel))
        {
            MarkFailed(sourceModelPath, "Session creation produced no EP-context nodes.");
            return;
        }

        (string gpuArchitecture, string? driverVersion) = EpContextLoadPathResolver.CurrentHardware;
        string compileOptionsIdentity = EpContextArtifact.BuildCompileOptionsIdentity(
            providerOptions ?? EpContextTrtProfiles.Resolve(sourceModelPath),
            EpContextArtifact.ShouldEmbedEpContext(sourceModelPath));
        EpContextArtifact.PublishStagedArtifact(stagingDirectory, sourceModelPath,
            createStamp: artifactFiles => EpContextArtifact.CreateStamp(
                sourceModelPath,
                new FileInfo(sourceModelPath),
                sourceSha256: null,
                gpuArchitecture,
                driverVersion,
                artifactFiles,
                compileOptionsIdentity: compileOptionsIdentity));
    }

    internal static bool ShouldRetryWithoutCapture(Exception exception) =>
        exception is IOException or UnauthorizedAccessException ||
        (exception is OnnxRuntimeException && IsCaptureWriteFailure(exception.Message));

    internal static bool IsCaptureWriteFailure(string message) =>
        message.Contains("ep.context", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("EP-context", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("EPContext", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("failed to write", StringComparison.OrdinalIgnoreCase);

    private static void MarkFailed(string sourceModelPath, string reason)
    {
        FailedSources[sourceModelPath] = reason;
        Trace.TraceWarning($"EP-context capture skipped for '{sourceModelPath}': {reason}");
    }

    private static bool HasFreeSpace(string path, long requiredBytes)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return root is not null && new DriveInfo(root).AvailableFreeSpace >= requiredBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An inaccessible drive is treated as insufficient space: capture is optional.
            return false;
        }
    }
}
