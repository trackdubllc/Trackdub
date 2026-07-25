using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakeOverlapRescueEngine : IOverlapRescueEngine, IStageRuntimeExecutionReporter
{
    public int CallCount { get; private set; }

    public OverlapRescueRequest? LastRequest { get; private set; }

    public bool ThrowOnRescue { get; set; }

    public bool PermutationWarning { get; set; }

    public double DurationSeconds { get; set; } = 1.0d;

    public int SampleRate { get; set; } = 16000;

    public int ChannelCount { get; set; } = 1;

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public Task<OverlapRescueResult> RescueAsync(
        OverlapRescueRequest request,
        IProgress<OverlapRescueProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        CallCount++;
        LastRequest = request;
        LastExecutionSummary = new StageRuntimeExecutionSummary(
            "auto",
            "cpu",
            "fake/sepformer",
            "sepformer",
            "default",
            "Fake overlap rescue");

        if (ThrowOnRescue)
        {
            throw new InvalidOperationException("Fake overlap rescue failed.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(request.SourceCandidate0OutputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(request.SourceCandidate1OutputPath)!);
        return WriteOutputsAsync(request, progress, cancellationToken);
    }

    private async Task<OverlapRescueResult> WriteOutputsAsync(
        OverlapRescueRequest request,
        IProgress<OverlapRescueProgress>? progress,
        CancellationToken cancellationToken)
    {
        await CopyOrWriteFallbackAsync(request.RegionAudioPath, request.SourceCandidate0OutputPath, cancellationToken)
            .ConfigureAwait(false);
        await CopyOrWriteFallbackAsync(request.RegionAudioPath, request.SourceCandidate1OutputPath, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new OverlapRescueProgress(1, 1, 0, 0d, 0d));

        return new OverlapRescueResult(
            DurationSeconds,
            SampleRate,
            ChannelCount,
            PermutationWarning,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["engine_family"] = "sepformer",
                ["runner"] = "fake-overlap-rescue"
            });
    }

    private static async Task CopyOrWriteFallbackAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(sourcePath))
        {
            await using FileStream source = new(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream destination = new(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        await File.WriteAllBytesAsync(destinationPath, FakeWavHelper.MinimalPcm16(), cancellationToken).ConfigureAwait(false);
    }
}
