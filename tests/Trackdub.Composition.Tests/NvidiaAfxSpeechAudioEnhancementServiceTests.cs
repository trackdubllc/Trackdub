using Trackdub.Contracts;
using Trackdub.Composition.NvidiaAfx;

namespace Trackdub.Composition.Tests;

public sealed class NvidiaAfxSpeechAudioEnhancementServiceTests
{
    [Fact]
    public async Task EnhanceAsync_FallsBack_WhenAfxDisabled()
    {
        var fallback = new FakeSpeechAudioEnhancementService();
        var readiness = new FakeReadinessService(new NvidiaAfxRuntimeReadiness(true, "Ready", "C:\\afx", null));
        var sut = new NvidiaAfxSpeechAudioEnhancementService(readiness, fallback);

        SpeechAudioEnhancementResult result = await sut.EnhanceAsync(
            new SpeechAudioEnhancementRequest(
                "source.wav",
                "dest.wav",
                new SpeechAudioEnhancementOptions(false, NvidiaAfxProfile.NoiseAndReverb, 1.0f)),
            CancellationToken.None);

        Assert.Equal(SpeechAudioEnhancementBackend.Ffmpeg, result.Backend);
        Assert.True(fallback.WasCalled);
    }

    [Fact]
    public async Task EnhanceAsync_FallsBack_WhenReadinessNotReady()
    {
        var fallback = new FakeSpeechAudioEnhancementService();
        var readiness = new FakeReadinessService(new NvidiaAfxRuntimeReadiness(false, "Not installed", null, "Missing runtime"));
        var sut = new NvidiaAfxSpeechAudioEnhancementService(readiness, fallback);

        SpeechAudioEnhancementResult result = await sut.EnhanceAsync(
            new SpeechAudioEnhancementRequest(
                "source.wav",
                "dest.wav",
                new SpeechAudioEnhancementOptions(true, NvidiaAfxProfile.NoiseAndReverb, 1.0f)),
            CancellationToken.None);

        Assert.Equal(SpeechAudioEnhancementBackend.Ffmpeg, result.Backend);
        Assert.True(fallback.WasCalled);
    }

    [Fact]
    public async Task EnhanceAsync_FallsBack_WhenKillSwitchIsOn()
    {
        var fallback = new FakeSpeechAudioEnhancementService();
        var readiness = new FakeReadinessService(new NvidiaAfxRuntimeReadiness(true, "Ready", "C:\\afx", null));
        var sut = new NvidiaAfxSpeechAudioEnhancementService(readiness, fallback, isStubbed: static () => true);

        SpeechAudioEnhancementResult result = await sut.EnhanceAsync(
            new SpeechAudioEnhancementRequest(
                "source.wav",
                "dest.wav",
                new SpeechAudioEnhancementOptions(true, NvidiaAfxProfile.NoiseAndReverb, 1.0f)),
            CancellationToken.None);

        Assert.Equal(SpeechAudioEnhancementBackend.Ffmpeg, result.Backend);
        Assert.True(fallback.WasCalled);
    }

    [Fact]
    public async Task EnhanceAsync_FallsBack_WhenAecProfileMissingFarEnd_PastStubGate()
    {
        var fallback = new FakeSpeechAudioEnhancementService();
        var readiness = new FakeReadinessService(new NvidiaAfxRuntimeReadiness(true, "Ready", "C:\\afx", null));
        var sut = new NvidiaAfxSpeechAudioEnhancementService(readiness, fallback);

        SpeechAudioEnhancementResult result = await sut.EnhanceAsync(
            new SpeechAudioEnhancementRequest(
                "source.wav",
                "dest.wav",
                new SpeechAudioEnhancementOptions(
                    true,
                    NvidiaAfxProfile.AcousticEchoCancellation,
                    1.0f,
                    FarEndReferenceAudioPath: null)),
            CancellationToken.None);

        Assert.Equal(SpeechAudioEnhancementBackend.Ffmpeg, result.Backend);
        Assert.True(fallback.WasCalled);
        Assert.Equal(1, readiness.CallCount);
    }

    [Fact]
    public async Task EnhanceAsync_FallsBack_WhenTheReadinessCheckThrows()
    {
        var fallback = new FakeSpeechAudioEnhancementService();
        var readiness = new ThrowingReadinessService();
        var sut = new NvidiaAfxSpeechAudioEnhancementService(readiness, fallback);

        SpeechAudioEnhancementResult result = await sut.EnhanceAsync(
            new SpeechAudioEnhancementRequest(
                "source.wav",
                "dest.wav",
                new SpeechAudioEnhancementOptions(true, NvidiaAfxProfile.NoiseAndReverb, 1.0f)),
            CancellationToken.None);

        Assert.Equal(1, readiness.CallCount);
        Assert.True(fallback.WasCalled);
        Assert.Equal(SpeechAudioEnhancementBackend.Ffmpeg, result.Backend);
    }

    [Fact]
    public async Task EnhanceAsync_LogsTheReadinessFailure_BeforeFallingBack()
    {
        var logger = new CapturingLogger();
        var sut = new NvidiaAfxSpeechAudioEnhancementService(
            new ThrowingReadinessService(),
            new FakeSpeechAudioEnhancementService(),
            logger: logger);

        await sut.EnhanceAsync(
            new SpeechAudioEnhancementRequest(
                "source.wav",
                "dest.wav",
                new SpeechAudioEnhancementOptions(true, NvidiaAfxProfile.NoiseAndReverb, 1.0f)),
            CancellationToken.None);

        Assert.Contains(NvidiaAfxProfile.NoiseAndReverb.ToString(), logger.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(logger.Exception);
    }

    [Fact]
    public async Task EnhanceAsync_LogsAFailedNativeAttempt_AndFallsBack()
    {
        var logger = new CapturingLogger();
        var fallback = new FakeSpeechAudioEnhancementService();
        string missingSource = Path.Join(Path.GetTempPath(), $"trackdub-afx-missing-{Guid.NewGuid():N}.wav");
        string destination = Path.Join(Path.GetTempPath(), $"trackdub-afx-dest-{Guid.NewGuid():N}.wav");
        var sut = new NvidiaAfxSpeechAudioEnhancementService(
            new FakeReadinessService(new NvidiaAfxRuntimeReadiness(true, "Ready", "C:\afx", null)),
            fallback,
            logger: logger);

        SpeechAudioEnhancementResult result = await sut.EnhanceAsync(
            new SpeechAudioEnhancementRequest(
                missingSource,
                destination,
                new SpeechAudioEnhancementOptions(true, NvidiaAfxProfile.NoiseAndReverb, 1.0f)),
            CancellationToken.None);

        Assert.True(fallback.WasCalled);
        Assert.Equal(SpeechAudioEnhancementBackend.Ffmpeg, result.Backend);
        Assert.NotNull(logger.Exception);
        Assert.False(File.Exists(destination + ".partial"));
    }

    [Fact]
    public async Task EnhanceAsync_DoesNotProbeReadiness_WhenAfxDisabled()
    {
        var fallback = new FakeSpeechAudioEnhancementService();
        var readiness = new ThrowingReadinessService();
        var sut = new NvidiaAfxSpeechAudioEnhancementService(readiness, fallback);

        SpeechAudioEnhancementResult result = await sut.EnhanceAsync(
            new SpeechAudioEnhancementRequest(
                "source.wav",
                "dest.wav",
                new SpeechAudioEnhancementOptions(false, NvidiaAfxProfile.NoiseAndReverb, 1.0f)),
            CancellationToken.None);

        Assert.Equal(0, readiness.CallCount);
        Assert.True(fallback.WasCalled);
        Assert.Equal(SpeechAudioEnhancementBackend.Ffmpeg, result.Backend);
    }

    [Fact]
    public async Task EnhanceAsync_DoesNotProbeReadiness_WhenKillSwitchIsOn()
    {
        var fallback = new FakeSpeechAudioEnhancementService();
        var readiness = new ThrowingReadinessService();
        var sut = new NvidiaAfxSpeechAudioEnhancementService(readiness, fallback, isStubbed: static () => true);

        SpeechAudioEnhancementResult result = await sut.EnhanceAsync(
            new SpeechAudioEnhancementRequest(
                "source.wav",
                "dest.wav",
                new SpeechAudioEnhancementOptions(true, NvidiaAfxProfile.NoiseAndReverb, 1.0f)),
            CancellationToken.None);

        Assert.Equal(0, readiness.CallCount);
        Assert.True(fallback.WasCalled);
        Assert.Equal(SpeechAudioEnhancementBackend.Ffmpeg, result.Backend);
    }

    private sealed class CapturingLogger : IApplicationLogger
    {
        public string? Message { get; private set; }

        public Exception? Exception { get; private set; }

        public void LogDebug(string message) { }

        public void LogInformation(string message) { }

        public void LogWarning(string message, Exception? exception = null)
        {
            Message = message;
            Exception = exception;
        }

        public void LogError(string message, Exception? exception = null) { }
    }

    private sealed class FakeReadinessService(NvidiaAfxRuntimeReadiness readiness) : INvidiaAfxRuntimeReadinessService
    {
        public int CallCount { get; private set; }

        public NvidiaAfxRuntimeReadiness GetReadiness(NvidiaAfxProfile profile)
        {
            CallCount++;
            return readiness;
        }
    }

    private sealed class ThrowingReadinessService : INvidiaAfxRuntimeReadinessService
    {
        public int CallCount { get; private set; }

        public NvidiaAfxRuntimeReadiness GetReadiness(NvidiaAfxProfile profile)
        {
            CallCount++;
            throw new InvalidOperationException("Readiness probe should not run for disabled/stubbed AFX.");
        }
    }

    private sealed class FakeSpeechAudioEnhancementService : ISpeechAudioEnhancementService
    {
        public bool WasCalled { get; private set; }

        public Task<SpeechAudioEnhancementResult> EnhanceAsync(SpeechAudioEnhancementRequest request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(new SpeechAudioEnhancementResult(
                request.DestinationPath,
                1.0d,
                16000,
                1,
                16000,
                SpeechAudioEnhancementBackend.Ffmpeg,
                null));
        }
    }
}
