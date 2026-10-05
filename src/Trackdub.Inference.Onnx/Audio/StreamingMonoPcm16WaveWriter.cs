using System.Buffers.Binary;

namespace Trackdub.Inference.Onnx.Audio;

/// <summary>
/// Sequential mono PCM16 WAV output with an 8 KiB encoding buffer. The caller owns the
/// stream and must await each write before issuing the next, then call CompleteAsync.
/// </summary>
internal sealed class StreamingMonoPcm16WaveWriter
{
    private readonly Stream stream;
    private readonly long expectedSampleFrames;
    private readonly byte[] buffer = new byte[8192];

    public long SampleFramesWritten { get; private set; }

    private StreamingMonoPcm16WaveWriter(Stream stream, long sampleFrames)
    {
        this.stream = stream;
        expectedSampleFrames = sampleFrames;
    }

    public static async Task<StreamingMonoPcm16WaveWriter> CreateAsync(
        Stream stream, long sampleFrames, int sampleRate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        // Match WaveAudioReader's signed 32-bit data chunk sizes. Larger media needs RF64,
        // not clip-sized allocations or silently wrapped RIFF lengths.
        if (sampleFrames < 0 || sampleFrames > int.MaxValue / sizeof(short))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleFrames), "PCM16 output exceeds the supported WAV data chunk size.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] header = WaveAudioWriter.EncodeMonoPcm16([], sampleRate);
        uint dataBytes = checked((uint)(sampleFrames * sizeof(short)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 36u + dataBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), dataBytes);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        return new StreamingMonoPcm16WaveWriter(stream, sampleFrames);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (samples.Length > expectedSampleFrames - SampleFramesWritten)
        {
            throw new InvalidOperationException("PCM output exceeds the declared WAV sample count.");
        }

        int offset = 0;
        while (offset < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(buffer.Length / sizeof(short), samples.Length - offset);
            Encode(samples.Span.Slice(offset, count), buffer);
            await stream.WriteAsync(buffer.AsMemory(0, count * sizeof(short)), cancellationToken)
                .ConfigureAwait(false);
            offset += count;
            SampleFramesWritten += count;
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SampleFramesWritten != expectedSampleFrames)
        {
            throw new InvalidOperationException(
                $"PCM output wrote {SampleFramesWritten} samples; WAV header declares {expectedSampleFrames}.");
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Encode(ReadOnlySpan<float> samples, Span<byte> destination)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            // Preserve the existing writer's truncation and 32767 quantization scale.
            float clamped = Math.Clamp(samples[i], -1f, 1f);
            short pcm = (short)Math.Clamp(clamped * 32767f, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(destination[(i * sizeof(short))..], pcm);
        }
    }
}
