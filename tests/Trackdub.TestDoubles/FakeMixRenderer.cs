using System.Buffers.Binary;
using System.Text;
using Trackdub.Contracts;
using Trackdub.Domain.Mixing;

namespace Trackdub.TestDoubles;

public sealed class FakeMixRenderer : IPreviewRangeRenderer
{
    private readonly List<PreviewRangeRenderRequest> calls = [];
    private readonly Dictionary<string, int> sourceChannelCounts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PreviewRangeRenderRequest> Calls => calls;

    public void SeedSourceChannelCount(string sourceAudioRelativePath, int channelCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAudioRelativePath);
        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount), "Channel count must be positive.");
        }

        sourceChannelCounts[sourceAudioRelativePath] = channelCount;
    }

    public Task<PreviewRangeRenderResult> RenderAsync(
        PreviewRangeRenderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.MixPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(request.StartSeconds) || request.StartSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(request.StartSeconds), "Preview range start must be finite and non-negative.");
        }

        if (!double.IsFinite(request.EndSeconds) || request.EndSeconds <= request.StartSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(request.EndSeconds), "Preview range end must be greater than the start.");
        }

        calls.Add(request);

        double durationSeconds = request.EndSeconds - request.StartSeconds;
        int sampleRate = 48000;
        int sourceChannelCount = sourceChannelCounts.TryGetValue(request.MixPlan.SourceAudioRelativePath, out int seededChannelCount)
            ? seededChannelCount
            : request.MixPlan.OutputChannelCount;
        int channelCount = request.MixPlan.OutputChannelCount >= 2 || sourceChannelCount >= 2 ? 2 : 1;
        cancellationToken.ThrowIfCancellationRequested();
        WriteSilentWave(request.OutputPath, (int)Math.Round(durationSeconds * sampleRate), sampleRate, channelCount);
        return Task.FromResult(new PreviewRangeRenderResult(
            request.OutputPath,
            durationSeconds,
            sampleRate,
            channelCount));
    }

    public MixPlan? LastMixPlan => calls.LastOrDefault()?.MixPlan;

    private static void WriteSilentWave(string path, int sampleCount, int sampleRate, int channelCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        int dataLength = sampleCount * channelCount * sizeof(short);
        var header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 36 + dataLength);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22, 2), (short)channelCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), sampleRate * channelCount * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32, 2), (short)(channelCount * sizeof(short)));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40, 4), dataLength);

        File.WriteAllBytes(path, header.Concat(new byte[dataLength]).ToArray());
    }
}
