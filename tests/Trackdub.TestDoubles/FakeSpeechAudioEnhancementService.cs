using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;

namespace Trackdub.TestDoubles;

public sealed class FakeSpeechAudioEnhancementService : ISpeechAudioEnhancementService
{
    public int CallCount { get; private set; }

    public SpeechAudioEnhancementRequest? LastRequest { get; private set; }

    public bool ThrowOnEnhance { get; set; }

    public bool ThrowRequiredModelNotAvailable { get; set; }

    public double DurationSeconds { get; set; } = 12.0d;

    public int SampleRate { get; set; } = 48000;

    public int ChannelCount { get; set; } = 1;

    public long SampleFrames { get; set; } = 576000;

    public async Task<SpeechAudioEnhancementResult> EnhanceAsync(
        SpeechAudioEnhancementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        CallCount++;
        LastRequest = request;
        if (ThrowRequiredModelNotAvailable)
        {
            throw new RequiredModelNotAvailableException("fake/enhancement", request.SourceAudioPath, canAutoDownload: true);
        }

        if (ThrowOnEnhance)
        {
            throw new InvalidOperationException("Fake speech enhancement failed.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
        if (File.Exists(request.SourceAudioPath))
        {
            await using FileStream source = new(
                request.SourceAudioPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream destination = new(
                request.DestinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllBytesAsync(request.DestinationPath, FakeWavHelper.MinimalPcm16(), cancellationToken).ConfigureAwait(false);
        }

        return new SpeechAudioEnhancementResult(
            request.DestinationPath,
            DurationSeconds,
            SampleRate,
            ChannelCount,
            SampleFrames);
    }
}
