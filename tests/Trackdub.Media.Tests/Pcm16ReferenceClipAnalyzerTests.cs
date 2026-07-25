using System.Buffers.Binary;
using Trackdub.Contracts;
using Trackdub.Media.Waveforms;

namespace Trackdub.Media.Tests;

public sealed class Pcm16ReferenceClipAnalyzerTests : IDisposable
{
    private readonly string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.ReferenceClipAnalyzer", Guid.NewGuid().ToString("N"));

    public Pcm16ReferenceClipAnalyzerTests()
    {
        Directory.CreateDirectory(tempDirectory);
    }

    [Fact]
    public async Task AnalyzeAsync_counts_active_speech_instead_of_total_duration()
    {
        string path = Path.Combine(tempDirectory, "mostly-silence.wav");
        WriteWave(path, sampleRate: 16000, totalSeconds: 4.0, activeSeconds: 0.5);
        var analyzer = new Pcm16ReferenceClipAnalyzer();

        var analysis = await analyzer.AnalyzeAsync(path, TestContext.Current.CancellationToken);

        Assert.True(analysis.TotalDurationSeconds >= 3.9d);
        Assert.True(analysis.ActiveSpeechSeconds < 1.0d);
        Assert.True(analysis.ActiveSpeechSeconds < ReferenceClipPolicy.MinimumActiveSpeechSeconds);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static void WriteWave(string path, int sampleRate, double totalSeconds, double activeSeconds)
    {
        int sampleCount = (int)(sampleRate * totalSeconds);
        int activeSampleCount = (int)(sampleRate * activeSeconds);
        byte[] data = new byte[sampleCount * sizeof(short)];
        for (int i = 0; i < activeSampleCount; i++)
        {
            double phase = 2d * Math.PI * 220d * i / sampleRate;
            short sample = (short)(Math.Sin(phase) * short.MaxValue * 0.5d);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * sizeof(short), sizeof(short)), sample);
        }

        byte[] header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 36 + data.Length);
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), sampleRate * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), data.Length);

        using FileStream stream = File.Create(path);
        stream.Write(header);
        stream.Write(data);
    }
}
