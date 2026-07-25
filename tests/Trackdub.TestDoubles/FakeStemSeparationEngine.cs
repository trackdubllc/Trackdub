using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakeStemSeparationEngine : IStemSeparationEngine, IStageRuntimeExecutionReporter
{
    public int CallCount { get; private set; }

    public StemSeparationRequest? LastRequest { get; private set; }

    public bool ThrowOnSeparate { get; set; }

    public double DurationSeconds { get; set; } = 12.0d;

    public int SampleRate { get; set; } = 48000;

    public int ChannelCount { get; set; } = 1;

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public string EngineFamily { get; set; } = "spleeter";

    public string Model { get; set; } = "spleeter";

    public string? RunnerVersion { get; set; }

    public IReadOnlyList<string> RawStemNames { get; set; } = [];

    public bool WriteMusic { get; set; } = true;

    public bool WriteSoundEffects { get; set; } = true;

    public async Task<StemSeparationResult> SeparateAsync(
        StemSeparationRequest request,
        IProgress<StemSeparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        CallCount++;
        LastRequest = request;
        LastExecutionSummary = new StageRuntimeExecutionSummary(
            "auto",
            "cpu",
            $"fake/{EngineFamily}",
            Model,
            "default",
            "Fake stem separation");

        if (ThrowOnSeparate)
        {
            throw new InvalidOperationException("Fake stem separation failed.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(request.VocalsOutputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(request.AmbianceOutputPath)!);
        progress?.Report(new StemSeparationProgress(0, 2, 0d, DurationSeconds / 2d));
        await CopyOrWriteFallbackAsync(request.SourceAudioPath, request.VocalsOutputPath, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new StemSeparationProgress(1, 2, DurationSeconds / 2d, DurationSeconds));
        await CopyOrWriteFallbackAsync(request.SourceAudioPath, request.AmbianceOutputPath, cancellationToken)
            .ConfigureAwait(false);
        if (WriteMusic && !string.IsNullOrWhiteSpace(request.MusicOutputPath))
        {
            await CopyOrWriteFallbackAsync(request.SourceAudioPath, request.MusicOutputPath, cancellationToken)
                .ConfigureAwait(false);
        }

        if (WriteSoundEffects && !string.IsNullOrWhiteSpace(request.SoundEffectsOutputPath))
        {
            await CopyOrWriteFallbackAsync(request.SourceAudioPath, request.SoundEffectsOutputPath, cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.RawStemOutputPaths is not null)
        {
            foreach (string rawStemName in RawStemNames)
            {
                if (request.RawStemOutputPaths.TryGetValue(rawStemName, out string? rawStemOutputPath))
                {
                    await CopyOrWriteFallbackAsync(request.SourceAudioPath, rawStemOutputPath, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        progress?.Report(new StemSeparationProgress(2, 2, DurationSeconds / 2d, DurationSeconds));

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["engine_family"] = EngineFamily,
            ["model"] = Model,
            ["raw_stems"] = string.Join(',', RawStemNames),
            ["runner"] = "fake-spleeter"
        };
        if (!string.IsNullOrWhiteSpace(RunnerVersion))
        {
            metadata["runner_version"] = RunnerVersion;
        }

        return new StemSeparationResult(
            DurationSeconds,
            SampleRate,
            ChannelCount,
            metadata);
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
