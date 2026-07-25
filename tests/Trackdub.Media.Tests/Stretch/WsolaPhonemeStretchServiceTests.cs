using Trackdub.Contracts.Pipeline;
using Trackdub.Media.Stretch;
using Trackdub.Media.Waveforms;

namespace Trackdub.Media.Tests;

public sealed class WsolaPhonemeStretchServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "Trackdub.WsolaTests",
        Guid.NewGuid().ToString("N"));

    public WsolaPhonemeStretchServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    // -----------------------------------------------------------------
    // Skip / null-return paths
    // -----------------------------------------------------------------

    [Fact]
    public async Task StretchAsync_AllOutOfBounds_ReturnsNull()
    {
        string inputPath = await WriteSineWavAsync(TimeSpan.FromSeconds(0.5));
        string outputPath = Path.Combine(_tempDir, "out_oob.wav");
        var plan = new PhonemeStretchPlan[]
        {
            new("AH", TimeSpan.Zero, TimeSpan.FromSeconds(0.5), 1.5, WithinBounds: false),
            new("IH", TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.0), 0.8, WithinBounds: false),
        };

        TimeSpan? result = await new WsolaPhonemeStretchService()
            .StretchAsync(inputPath, outputPath, plan, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task StretchAsync_EmptyPlan_ReturnsNull()
    {
        string inputPath = await WriteSineWavAsync(TimeSpan.FromSeconds(0.5));
        string outputPath = Path.Combine(_tempDir, "out_empty.wav");

        TimeSpan? result = await new WsolaPhonemeStretchService()
            .StretchAsync(inputPath, outputPath, [], TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    // -----------------------------------------------------------------
    // Duration accuracy
    // -----------------------------------------------------------------

    [Fact]
    public async Task StretchAsync_SinglePhonemeRatio1_OutputDurationMatchesInput()
    {
        const double inputDurationSeconds = 1.0;
        string inputPath = await WriteSineWavAsync(TimeSpan.FromSeconds(inputDurationSeconds));
        string outputPath = Path.Combine(_tempDir, "out_r1.wav");
        var plan = new PhonemeStretchPlan[]
        {
            new("AH", TimeSpan.Zero, TimeSpan.FromSeconds(inputDurationSeconds),
                StretchRatio: 1.0, WithinBounds: true),
        };

        TimeSpan? result = await new WsolaPhonemeStretchService()
            .StretchAsync(inputPath, outputPath, plan, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.InRange(result!.Value.TotalSeconds, 0.95, 1.05);
    }

    [Fact]
    public async Task StretchAsync_SinglePhonemeRatio2_OutputDurationDoubled()
    {
        const double inputDurationSeconds = 1.0;
        string inputPath = await WriteSineWavAsync(TimeSpan.FromSeconds(inputDurationSeconds));
        string outputPath = Path.Combine(_tempDir, "out_r2.wav");
        var plan = new PhonemeStretchPlan[]
        {
            new("AH", TimeSpan.Zero, TimeSpan.FromSeconds(inputDurationSeconds),
                StretchRatio: 2.0, WithinBounds: true),
        };

        TimeSpan? result = await new WsolaPhonemeStretchService()
            .StretchAsync(inputPath, outputPath, plan, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.InRange(result!.Value.TotalSeconds,
            inputDurationSeconds * 2.0 - 0.1,
            inputDurationSeconds * 2.0 + 0.1);
    }

    // -----------------------------------------------------------------
    // File system side effects
    // -----------------------------------------------------------------

    [Fact]
    public async Task StretchAsync_OutputFileCreated_WhenSuccessful()
    {
        string inputPath = await WriteSineWavAsync(TimeSpan.FromSeconds(0.5));
        string outputPath = Path.Combine(_tempDir, "out_created.wav");
        var plan = new PhonemeStretchPlan[]
        {
            new("AH", TimeSpan.Zero, TimeSpan.FromSeconds(0.5),
                StretchRatio: 1.0, WithinBounds: true),
        };

        await new WsolaPhonemeStretchService()
            .StretchAsync(inputPath, outputPath, plan, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public async Task StretchAsync_InputPreserved_OnSkip()
    {
        string inputPath = await WriteSineWavAsync(TimeSpan.FromSeconds(0.5));
        byte[] originalBytes = await File.ReadAllBytesAsync(
            inputPath, TestContext.Current.CancellationToken);
        string outputPath = Path.Combine(_tempDir, "out_skip.wav");
        var plan = new PhonemeStretchPlan[]
        {
            new("AH", TimeSpan.Zero, TimeSpan.FromSeconds(0.5),
                StretchRatio: 1.5, WithinBounds: false),
        };

        TimeSpan? result = await new WsolaPhonemeStretchService()
            .StretchAsync(inputPath, outputPath, plan, TestContext.Current.CancellationToken);

        Assert.Null(result);
        byte[] currentBytes = await File.ReadAllBytesAsync(
            inputPath, TestContext.Current.CancellationToken);
        Assert.Equal(originalBytes, currentBytes);
    }

    // -----------------------------------------------------------------
    // Cleanup
    // -----------------------------------------------------------------

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<string> WriteSineWavAsync(
        TimeSpan duration,
        int sampleRate = 16_000,
        double frequency = 440.0)
    {
        string path = Path.Combine(_tempDir, $"sine_{Guid.NewGuid():N}.wav");
        int frameCount = (int)(duration.TotalSeconds * sampleRate);
        var samples = new float[frameCount];
        double angleStep = 2.0 * Math.PI * frequency / sampleRate;
        for (int i = 0; i < frameCount; i++)
            samples[i] = 0.5f * (float)Math.Sin(angleStep * i);

        await WavePcm16.WriteMonoAsync(
            path, samples, sampleRate,
            TestContext.Current.CancellationToken);
        return path;
    }
}
