using System.Buffers.Binary;

namespace Trackdub.Inference.Onnx.Audio;

internal static class WaveAudioReader
{
    public static Task<IAudioChannelSamples> ReadPcm16Async(
        string path,
        CancellationToken cancellationToken) =>
        ReadPcm16CoreAsync(path, cancellationToken);

    public static async Task<IAudioSamples> ReadMonoPcm16Async(
        string path,
        CancellationToken cancellationToken) =>
        await ReadPcm16CoreAsync(path, cancellationToken).ConfigureAwait(false);

    private static async Task<IAudioChannelSamples> ReadPcm16CoreAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        FileStream? stream = null;
        System.IO.MemoryMappedFiles.MemoryMappedFile? mmf = null;
        System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? accessor = null;

        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] buffer4 = new byte[4];
            byte[] buffer2 = new byte[2];

            EnsureFourCc(await ReadFourCcAsync(stream, buffer4, cancellationToken).ConfigureAwait(false), "RIFF");
            _ = await ReadInt32Async(stream, buffer4, cancellationToken).ConfigureAwait(false);
            EnsureFourCc(await ReadFourCcAsync(stream, buffer4, cancellationToken).ConfigureAwait(false), "WAVE");

            ushort audioFormat = 0;
            ushort channelCount = 0;
            int sampleRate = 0;
            ushort blockAlign = 0;
            ushort bitsPerSample = 0;
            long dataStart = 0;
            int dataLength = 0;

            while (stream.Position < stream.Length)
            {
                string chunkId = await ReadFourCcAsync(stream, buffer4, cancellationToken).ConfigureAwait(false);
                int chunkSize = await ReadInt32Async(stream, buffer4, cancellationToken).ConfigureAwait(false);
                if (chunkSize < 0)
                {
                    throw new InvalidOperationException($"WAV chunk '{chunkId}' at offset {stream.Position} has negative size {chunkSize}.");
                }

                long nextChunk = stream.Position + chunkSize;

                switch (chunkId)
                {
                    case "fmt ":
                        if (chunkSize < 16)
                        {
                            throw new InvalidOperationException($"WAV 'fmt ' chunk is too small ({chunkSize} bytes); minimum required is 16.");
                        }

                        audioFormat = await ReadUInt16Async(stream, buffer2, cancellationToken).ConfigureAwait(false);
                        channelCount = await ReadUInt16Async(stream, buffer2, cancellationToken).ConfigureAwait(false);
                        sampleRate = await ReadInt32Async(stream, buffer4, cancellationToken).ConfigureAwait(false);
                        _ = await ReadInt32Async(stream, buffer4, cancellationToken).ConfigureAwait(false);
                        blockAlign = await ReadUInt16Async(stream, buffer2, cancellationToken).ConfigureAwait(false);
                        bitsPerSample = await ReadUInt16Async(stream, buffer2, cancellationToken).ConfigureAwait(false);
                        break;
                    case "data":
                        dataStart = stream.Position;
                        dataLength = chunkSize;
                        break;
                }

                long paddedChunkEnd = nextChunk + (chunkSize % 2);
                if (paddedChunkEnd < stream.Position || paddedChunkEnd > stream.Length)
                {
                    throw new InvalidOperationException($"WAV chunk '{chunkId}' at offset {nextChunk} overflows file length {stream.Length}.");
                }

                stream.Position = paddedChunkEnd;
            }

            ValidateHeader(audioFormat, channelCount, sampleRate, blockAlign, bitsPerSample, dataStart, dataLength);

            long sampleFrameCount = dataLength / blockAlign;

            mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
                stream,
                null,
                0,
                System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: true);

            accessor = mmf.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);

            var reader = new MemoryMappedWaveAudioReader(mmf, accessor, stream, channelCount, blockAlign, dataStart, sampleRate, sampleFrameCount);
            mmf = null;
            accessor = null;
            stream = null;
            return reader;
        }
        finally
        {
            accessor?.Dispose();
            mmf?.Dispose();
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static void ValidateHeader(
        ushort audioFormat,
        ushort channelCount,
        int sampleRate,
        ushort blockAlign,
        ushort bitsPerSample,
        long dataStart,
        int dataLength)
    {
        if (audioFormat != 1)
        {
            throw new InvalidOperationException($"Unsupported WAV encoding '{audioFormat}'. Only PCM is supported.");
        }

        if (bitsPerSample != 16)
        {
            throw new InvalidOperationException($"Unsupported WAV bit depth '{bitsPerSample}'. Only 16-bit PCM is supported.");
        }

        if (channelCount == 0 || sampleRate <= 0 || blockAlign == 0)
        {
            throw new InvalidOperationException("WAV header contains invalid channel count, sample rate, or block alignment.");
        }

        ushort expectedBlockAlign = checked((ushort)(channelCount * (bitsPerSample / 8)));
        if (blockAlign != expectedBlockAlign)
        {
            throw new InvalidOperationException(
                $"WAV block alignment {blockAlign} is inconsistent with channel count {channelCount} and bit depth {bitsPerSample} (expected {expectedBlockAlign}).");
        }

        if (dataStart == 0 || dataLength == 0)
        {
            throw new InvalidOperationException("WAV file does not contain a data chunk.");
        }
    }

    private static void EnsureFourCc(string actualText, string expected)
    {
        if (!string.Equals(actualText, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected WAV marker '{expected}' but found '{actualText}'.");
        }
    }

    private static async Task<string> ReadFourCcAsync(
        FileStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, 4);
    }

    private static async Task<ushort> ReadUInt16Async(
        FileStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buffer.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }

    private static async Task<int> ReadInt32Async(
        FileStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await ReadExactAsync(stream, buffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static async Task ReadExactAsync(
        FileStream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int totalBytesRead = 0;
        while (totalBytesRead < buffer.Length)
        {
            int bytesRead = await stream.ReadAsync(buffer[totalBytesRead..], cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new InvalidOperationException("Unexpected end of WAV header.");
            }

            totalBytesRead += bytesRead;
        }
    }
}
