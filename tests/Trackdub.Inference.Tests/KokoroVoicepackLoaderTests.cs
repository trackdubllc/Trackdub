using System.Buffers.Binary;
using Trackdub.Inference.Onnx.Kokoro;

namespace Trackdub.Inference.Tests;

public sealed class KokoroVoicepackLoaderTests : IDisposable
{
    private const int StyleVectorSize = 256;
    private const int BytesPerFloat = sizeof(float);
    private const int BytesPerRow = StyleVectorSize * BytesPerFloat; // 1024

    private readonly List<string> tempFiles = [];

    public void Dispose()
    {
        foreach (string file in tempFiles)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    // ── LoadStyleVector ───────────────────────────────────────────────────────

    [Fact]
    public void LoadStyleVector_Row0_ReturnsFirstRow()
    {
        float[] row0 = Enumerable.Range(0, StyleVectorSize).Select(i => (float)i).ToArray();
        float[] row1 = Enumerable.Range(StyleVectorSize, StyleVectorSize).Select(i => (float)i).ToArray();

        string binPath = WriteBinFile(row0, row1);

        float[] result = KokoroVoicepackLoader.LoadStyleVector(binPath, tokenCount: 0);

        Assert.Equal(StyleVectorSize, result.Length);
        for (int i = 0; i < StyleVectorSize; i++)
        {
            Assert.Equal(row0[i], result[i]);
        }
    }

    [Fact]
    public void LoadStyleVector_Row1_ReturnsSecondRow()
    {
        float[] row0 = new float[StyleVectorSize]; // all zeros
        float[] row1 = Enumerable.Range(0, StyleVectorSize).Select(i => (float)(i + 100)).ToArray();

        string binPath = WriteBinFile(row0, row1);

        float[] result = KokoroVoicepackLoader.LoadStyleVector(binPath, tokenCount: 1);

        Assert.Equal(StyleVectorSize, result.Length);
        for (int i = 0; i < StyleVectorSize; i++)
        {
            Assert.Equal(row1[i], result[i]);
        }
    }

    [Fact]
    public void LoadStyleVector_ResultLength_IsAlways256()
    {
        float[] row = new float[StyleVectorSize];
        string binPath = WriteBinFile(row, row);

        float[] result = KokoroVoicepackLoader.LoadStyleVector(binPath, tokenCount: 0);

        Assert.Equal(256, result.Length);
    }

    [Fact]
    public void LoadStyleVector_SilentRow_ReturnsAllZeros()
    {
        float[] silentRow = new float[StyleVectorSize]; // all 0.0f
        string binPath = WriteBinFile(silentRow);

        float[] result = KokoroVoicepackLoader.LoadStyleVector(binPath, tokenCount: 0);

        Assert.All(result, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void LoadStyleVector_KnownValues_RoundTripCorrectly()
    {
        float[] row = Enumerable.Range(0, StyleVectorSize)
            .Select(i => i * 0.001f)
            .ToArray();
        string binPath = WriteBinFile(row);

        float[] result = KokoroVoicepackLoader.LoadStyleVector(binPath, tokenCount: 0);

        for (int i = 0; i < StyleVectorSize; i++)
        {
            Assert.Equal(row[i], result[i], precision: 5);
        }
    }

    [Fact]
    public void LoadStyleVector_LastRequestedRow_DoesNotReadBeyondIt()
    {
        float[] row0 = Enumerable.Repeat(1.0f, StyleVectorSize).ToArray();
        float[] row1 = Enumerable.Repeat(2.0f, StyleVectorSize).ToArray();
        float[] row2 = Enumerable.Repeat(3.0f, StyleVectorSize).ToArray();
        string binPath = WriteBinFile(row0, row1, row2);

        float[] result = KokoroVoicepackLoader.LoadStyleVector(binPath, tokenCount: 2);

        Assert.Equal(StyleVectorSize, result.Length);
        Assert.All(result, value => Assert.Equal(3.0f, value));
    }

    [Fact]
    public void LoadStyleVector_ReadsBytesInLittleEndianOrder()
    {
        const float expected = 3.14159f;
        byte[] bytes = new byte[BytesPerRow];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, expected);
        string binPath = WriteTempBinWithBytes(bytes);

        float[] result = KokoroVoicepackLoader.LoadStyleVector(binPath, tokenCount: 0);

        Assert.Equal(expected, result[0], precision: 4);
    }

    // ── Error cases ───────────────────────────────────────────────────────────

    [Fact]
    public void LoadStyleVector_FileTooSmallForRow0_Throws()
    {
        // File with only half a row
        string binPath = WriteTempBinWithBytes(new byte[BytesPerRow / 2]);

        Assert.Throws<InvalidOperationException>(
            () => KokoroVoicepackLoader.LoadStyleVector(binPath, tokenCount: 0));
    }

    [Fact]
    public void LoadStyleVector_FileTooSmallForRequestedRow_Throws()
    {
        // File has exactly 1 row (tokenCount=0 would work, tokenCount=5 would not)
        string binPath = WriteBinFile(new float[StyleVectorSize]);

        Assert.Throws<InvalidOperationException>(
            () => KokoroVoicepackLoader.LoadStyleVector(binPath, tokenCount: 5));
    }

    [Fact]
    public void LoadStyleVector_EmptyFile_Throws()
    {
        string binPath = WriteTempBinWithBytes([]);

        Assert.Throws<InvalidOperationException>(
            () => KokoroVoicepackLoader.LoadStyleVector(binPath, tokenCount: 0));
    }

    [Fact]
    public void LoadStyleVector_ErrorMessage_ContainsFilenameAndTokenCount()
    {
        string binPath = WriteTempBinWithBytes([]);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => KokoroVoicepackLoader.LoadStyleVector(binPath, tokenCount: 7));

        Assert.Contains(Path.GetFileName(binPath), ex.Message, StringComparison.Ordinal);
        Assert.Contains("7", ex.Message, StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string WriteBinFile(params float[][] rows)
    {
        byte[] bytes = new byte[rows.Length * BytesPerRow];
        for (int rowIdx = 0; rowIdx < rows.Length; rowIdx++)
        {
            for (int i = 0; i < StyleVectorSize; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(
                    bytes.AsSpan((rowIdx * BytesPerRow) + (i * BytesPerFloat)),
                    rows[rowIdx][i]);
            }
        }
        return WriteTempBinWithBytes(bytes);
    }

    private string WriteTempBinWithBytes(byte[] bytes)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"Trackdub.VoicepackLoader.{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, bytes);
        tempFiles.Add(path);
        return path;
    }
}
