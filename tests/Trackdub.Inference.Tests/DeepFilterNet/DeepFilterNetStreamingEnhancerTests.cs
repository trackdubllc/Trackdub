using Trackdub.Domain;
using Trackdub.Inference.Onnx.Audio;
using Trackdub.Inference.Onnx.DeepFilterNet;

namespace Trackdub.Inference.Tests.DeepFilterNet;

/// <summary>
/// Tests for <see cref="DeepFilterNetStreamingEnhancer"/>, the state-preserving
/// DeepFilterNet3 engine. Windowing must be a pure throughput choice: different window
/// sizes over the same audio must produce (near-)identical output, because GRU states,
/// feature-normalization statistics and convolution lookbacks are carried across windows
/// instead of restarting per chunk (native libDF parity).
/// </summary>
public sealed class DeepFilterNetStreamingEnhancerTests
{
    [Fact]
    public void ClassifyGate_MatchesNativeApplyStagesThresholds()
    {
        // Native libDF apply_stages with CLI thresholds (-15/35/35); comparisons are strict,
        // so equality takes the full-processing branch (matches the validated prototype).
        Assert.Equal(DeepFilterNetFrameGate.NoiseOnly, DeepFilterNetStreamingEnhancer.ClassifyGate(-20f));
        Assert.Equal(DeepFilterNetFrameGate.NoiseOnly, DeepFilterNetStreamingEnhancer.ClassifyGate(-15.001f));
        Assert.Equal(DeepFilterNetFrameGate.Speech, DeepFilterNetStreamingEnhancer.ClassifyGate(-15f));
        Assert.Equal(DeepFilterNetFrameGate.Speech, DeepFilterNetStreamingEnhancer.ClassifyGate(0f));
        Assert.Equal(DeepFilterNetFrameGate.Speech, DeepFilterNetStreamingEnhancer.ClassifyGate(35f));
        Assert.Equal(DeepFilterNetFrameGate.Clean, DeepFilterNetStreamingEnhancer.ClassifyGate(35.001f));
    }

    [Fact]
    public async Task EnhanceAsync_EmptyInput_ReturnsEmptyWithoutTouchingSessions()
    {
        float[] output = await EnhanceToArrayAsync(
            new InMemoryAudioSamples([]),
            sessions: null!,
            attenuationLimit: 0f,
            CancellationToken.None);

        Assert.Empty(output);
    }

    [DfModelFact]
    public async Task EnhanceAsync_AllQuietInput_ReturnsZerosOfNativeLength()
    {
        DeepFilterNetModelSessions sessions = await CreateSessionsAsync();
        try
        {
            const int totalSamples = 48000 * 2;
            float[] output = await EnhanceToArrayAsync(
                new InMemoryAudioSamples(new float[totalSamples]),
                sessions,
                attenuationLimit: 0f,
                CancellationToken.None);

            // Native -D semantics: one output hop per input hop, minus the 3-hop system delay.
            Assert.Equal(totalSamples - DeepFilterNetStreamingEnhancer.LatencyTrimSamples, output.Length);
            Assert.All(output, static sample => Assert.Equal(0f, sample));
        }
        finally
        {
            sessions.Dispose();
        }
    }

    [DfModelFact]
    public async Task EnhanceAsync_WindowSize_DoesNotChangeOutput()
    {
        DeepFilterNetModelSessions sessions = await CreateSessionsAsync();
        try
        {
            float[] voiced = BuildVoiced(48000 * 12);

            float[] wide = await EnhanceToArrayAsync(
                new InMemoryAudioSamples(voiced),
                sessions,
                attenuationLimit: ToLinearLimitDb(35),
                CancellationToken.None,
                windowFrames: 600);
            float[] narrow = await EnhanceToArrayAsync(
                new InMemoryAudioSamples(voiced),
                sessions,
                attenuationLimit: ToLinearLimitDb(35),
                CancellationToken.None,
                windowFrames: 37);

            Assert.Equal(wide.Length, narrow.Length);
            float peak = 0f;
            for (int i = 0; i < wide.Length; i++)
            {
                peak = MathF.Max(peak, MathF.Abs(wide[i] - narrow[i]));
            }

            // One PCM16 quantization step of headroom; the old chunked engine diverged by
            // orders of magnitude more at every 5.5 s boundary.
            Assert.True(peak <= (2f / 32768f), $"Window-size peak divergence {peak:E3} exceeds one PCM step.");
        }
        finally
        {
            sessions.Dispose();
        }
    }

    [DfModelFact]
    public async Task EnhanceAsync_AttenuationLimit_ChangesOutputOnNoisyInput()
    {
        DeepFilterNetModelSessions sessions = await CreateSessionsAsync();
        try
        {
            float[] noisy = BuildNoisyVoiced(48000 * 6, seed: 7);

            float[] limited = await EnhanceToArrayAsync(
                new InMemoryAudioSamples(noisy),
                sessions,
                attenuationLimit: ToLinearLimitDb(35),
                CancellationToken.None,
                windowFrames: 200);
            float[] unlimited = await EnhanceToArrayAsync(
                new InMemoryAudioSamples(noisy),
                sessions,
                attenuationLimit: 0f,
                CancellationToken.None,
                windowFrames: 200);

            Assert.Equal(limited.Length, unlimited.Length);
            float peak = 0f;
            for (int i = 0; i < limited.Length; i++)
            {
                peak = MathF.Max(peak, MathF.Abs(limited[i] - unlimited[i]));
            }

            Assert.True(peak > 1e-4f, $"Attenuation limit had no effect (peak {peak:E3}).");
        }
        finally
        {
            sessions.Dispose();
        }
    }

    [DfModelFact]
    public async Task EnhanceAsync_CanceledToken_Throws()
    {
        DeepFilterNetModelSessions sessions = await CreateSessionsAsync();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                EnhanceToArrayAsync(
                    new InMemoryAudioSamples(BuildVoiced(48000)),
                    sessions,
                    attenuationLimit: 0f,
                    cts.Token));
        }
        finally
        {
            sessions.Dispose();
        }
    }

    [DfModelFact]
    public async Task EnhanceAsync_ReadsEachSampleOnceInBoundedWindows()
    {
        using DeepFilterNetModelSessions sessions = await CreateSessionsAsync();
        const int windowFrames = 37;
        using var audio = new GuardedAudioSamples((48000 * 2) + 17, windowFrames * 480);

        float[] output = await EnhanceToArrayAsync(
            audio, sessions, ToLinearLimitDb(35), CancellationToken.None, windowFrames);

        Assert.Equal(audio.SampleFrameCount, audio.FramesRead);
        Assert.Equal((((audio.SampleFrameCount + 479) / 480) * 480) - 1440, output.LongLength);
    }

    // Full-output materialization is confined to small test fixtures, never production.
    private static async Task<float[]> EnhanceToArrayAsync(
        IAudioSamples audio, DeepFilterNetModelSessions sessions, float attenuationLimit,
        CancellationToken cancellationToken, int windowFrames = 600)
    {
        var output = new float[checked((int)DeepFilterNetStreamingEnhancer.GetOutputSampleCount(audio.SampleFrameCount))];
        int offset = 0;
        long count = await DeepFilterNetStreamingEnhancer.EnhanceAsync(
            audio, sessions, attenuationLimit, (samples, _) =>
            {
                samples.CopyTo(output.AsMemory(offset));
                offset += samples.Length;
                return ValueTask.CompletedTask;
            }, cancellationToken, windowFrames);
        Assert.Equal(output.LongLength, count);
        return output;
    }

    [DfModelFact]
    public async Task EnhanceAsync_StreamsBeforeEofWithBackpressureAndNoPrefixReplay()
    {
        using DeepFilterNetModelSessions sessions = await CreateSessionsAsync();
        const int windowFrames = 37;
        using var audio = new GuardedAudioSamples((48000 * 2) + 17, windowFrames * 480);
        int writes = 0;
        long framesWritten = 0;
        long count = await DeepFilterNetStreamingEnhancer.EnhanceAsync(
            audio, sessions, ToLinearLimitDb(35), async (samples, token) =>
            {
                Assert.InRange(samples.Length, 1, windowFrames * 480);
                if (writes++ == 0)
                {
                    Assert.Equal(windowFrames * 480, audio.FramesRead);
                    Assert.True(audio.FramesRead < audio.SampleFrameCount);
                }

                long readAtWrite = audio.FramesRead;
                float firstSample = samples.Span[0];
                await Task.Delay(1, token);
                Assert.Equal(readAtWrite, audio.FramesRead);
                Assert.Equal(firstSample, samples.Span[0]);
                framesWritten += samples.Length;
            }, CancellationToken.None, windowFrames);

        Assert.True(writes > 1);
        Assert.Equal(audio.SampleFrameCount, audio.FramesRead);
        Assert.Equal(DeepFilterNetStreamingEnhancer.GetOutputSampleCount(audio.SampleFrameCount), count);
        Assert.Equal(count, framesWritten);
    }

    [DfModelFact]
    public async Task EnhanceAsync_LongQuietSourceUsesLongOffsetsAndStopsOnSinkCancellation()
    {
        using DeepFilterNetModelSessions sessions = await CreateSessionsAsync();
        using var cts = new CancellationTokenSource();
        const long length = (long)int.MaxValue + 48000;
        using var audio = new GuardedAudioSamples(length, 480 * 37, allQuiet: true);
        int writes = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            DeepFilterNetStreamingEnhancer.EnhanceAsync(audio, sessions, 0f, (samples, token) =>
            {
                Assert.Equal(cts.Token, token);
                Assert.Equal(480 * 37, audio.FramesRead);
                Assert.All(samples.ToArray(), static sample => Assert.Equal(0f, sample));
                writes++;
                cts.Cancel();
                return ValueTask.CompletedTask;
            }, cts.Token, windowFrames: 37));
        Assert.Equal(1, writes);
        Assert.Equal(480 * 37, audio.FramesRead);
    }

    [DfModelFact]
    public async Task EnhanceAsync_SinkFailureStopsReadingImmediately()
    {
        using DeepFilterNetModelSessions sessions = await CreateSessionsAsync();
        using var audio = new GuardedAudioSamples(48000 * 2, 480 * 37);
        var expected = new IOException("Sink failed");
        Exception actual = await Assert.ThrowsAsync<IOException>(() =>
            DeepFilterNetStreamingEnhancer.EnhanceAsync(audio, sessions, 0f,
                (_, _) => ValueTask.FromException(expected), CancellationToken.None, windowFrames: 37));
        Assert.Same(expected, actual);
        Assert.Equal(480 * 37, audio.FramesRead);
    }

    [DfModelFact]
    public async Task EnhanceAsync_QuietGapsAndPartialHopKeepStateAcrossSingleHopWindows()
    {
        using DeepFilterNetModelSessions sessions = await CreateSessionsAsync();
        const int length = (480 * 53) + 17;
        using var wideAudio = new GuardedAudioSamples(length, 480 * 600);
        using var narrowAudio = new GuardedAudioSamples(length, 480);
        float[] wide = await EnhanceToArrayAsync(wideAudio, sessions, ToLinearLimitDb(35), CancellationToken.None, 600);
        float[] narrow = await EnhanceToArrayAsync(narrowAudio, sessions, ToLinearLimitDb(35), CancellationToken.None, 1);
        Assert.Equal(wide.Length, narrow.Length);
        float peak = wide.Zip(narrow, static (a, b) => MathF.Abs(a - b)).Max();
        Assert.True(peak <= 2f / 32768f, $"Quiet-gap/window divergence: {peak:E3}");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(1440, 0)]
    [InlineData(1441, 480)]
    [InlineData(1920, 480)]
    public void GetOutputSampleCount_PadsLastHopAndTrimsPhysicalLatency(long input, long expected) =>
        Assert.Equal(expected, DeepFilterNetStreamingEnhancer.GetOutputSampleCount(input));

    private sealed class GuardedAudioSamples(long sampleCount, int maxRead, bool allQuiet = false) : IAudioSamples
    {
        public int SampleRate => 48000;
        public long SampleFrameCount => sampleCount;
        public long FramesRead { get; private set; }

        public void ReadMonoSamples(long startFrame, Span<float> destination)
        {
            Assert.Equal(FramesRead, startFrame);
            Assert.InRange(destination.Length, 1, maxRead);
            Assert.True(startFrame + destination.Length <= sampleCount);
            // Include quiet gaps to exercise the frozen analysis/recurrent/synthesis clock.
            for (int i = 0; i < destination.Length; i++)
            {
                long frame = startFrame + i;
                destination[i] = allQuiet || (frame / 480) % 11 < 4
                    ? 0f
                    : 0.2f * MathF.Sin(2f * MathF.PI * 110f * (float)frame / 48000f);
            }

            FramesRead += destination.Length;
        }

        public void Dispose() { }
    }

    private static float ToLinearLimitDb(double attenuationLimitDb) =>
        attenuationLimitDb > 0 ? (float)Math.Pow(10, -attenuationLimitDb / 20) : 0f;

    private static Task<DeepFilterNetModelSessions> CreateSessionsAsync()
    {
        string root = DfModelFactAttribute.RequireModelRoot();
        var paths = new DeepFilterNetModelPaths(
            root,
            Path.Join(root, "enc.onnx"),
            Path.Join(root, "erb_dec.onnx"),
            Path.Join(root, "df_dec.onnx"),
            CommercialAllowed: true,
            CommercialUseVerified: true);
        return DeepFilterNetModelSessions.CreateAsync(paths, ExecutionProviderKind.Cpu, CancellationToken.None);
    }

    private static float[] BuildVoiced(int length)
    {
        var signal = new float[length];
        for (int i = 0; i < length; i++)
        {
            float t = (float)i / 48000f;
            float tremolo = 0.75f + (0.25f * MathF.Sin(2f * MathF.PI * 5f * t));
            signal[i] = tremolo * (
                (0.20f * MathF.Sin(2f * MathF.PI * 110f * t)) +
                (0.10f * MathF.Sin(2f * MathF.PI * 220f * t)) +
                (0.05f * MathF.Sin(2f * MathF.PI * 330f * t)));
        }

        return signal;
    }

    private static float[] BuildNoisyVoiced(int length, int seed)
    {
        float[] signal = BuildVoiced(length);
        var random = new Random(seed);
        for (int i = 0; i < length; i++)
        {
            signal[i] += 0.05f * (float)(random.NextDouble() * 2.0 - 1.0);
        }

        return signal;
    }

    /// <summary>
    /// Runs model-backed tests only when the DeepFilterNet3 cache is present; otherwise the
    /// test is skipped (same pattern as the transcript-engine fixture tests).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    private sealed class DfModelFactAttribute : FactAttribute
    {
        public DfModelFactAttribute()
        {
            try
            {
                RequireModelRoot();
            }
            catch (DirectoryNotFoundException ex)
            {
                Skip = ex.Message;
            }
            catch (FileNotFoundException ex)
            {
                Skip = ex.Message;
            }
        }

        public static string RequireModelRoot()
        {
            string? overrideDir = Environment.GetEnvironmentVariable("TRACKDUB_DF_MODEL_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                return RequireAllModels(overrideDir);
            }

            string cacheRoot = Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Trackdub", "model-cache", "Rikorose", "DeepFilterNet3");
            if (!Directory.Exists(cacheRoot))
            {
                throw new DirectoryNotFoundException(
                    "Set TRACKDUB_DF_MODEL_DIR to a directory containing enc.onnx, erb_dec.onnx " +
                    "and df_dec.onnx to run this integration test.");
            }

            return RequireAllModels(cacheRoot);
        }

        private static string RequireAllModels(string root)
        {
            foreach (string name in new[] { "enc.onnx", "erb_dec.onnx", "df_dec.onnx" })
            {
                if (!File.Exists(Path.Join(root, name)))
                {
                    throw new FileNotFoundException(
                        $"Model file '{name}' was not found under '{root}'. " +
                        "Set TRACKDUB_DF_MODEL_DIR to a complete DeepFilterNet3 directory.");
                }
            }

            return root;
        }
    }

    private sealed class InMemoryAudioSamples(float[] data) : IAudioSamples
    {
        public int SampleRate => 48000;

        public long SampleFrameCount => data.Length;

        public void ReadMonoSamples(long startFrame, Span<float> destination) =>
            data.AsSpan((int)startFrame, destination.Length).CopyTo(destination);

        public void Dispose()
        {
        }
    }
}
