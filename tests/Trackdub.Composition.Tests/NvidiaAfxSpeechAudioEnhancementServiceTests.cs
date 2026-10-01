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
    public async Task EnhanceAsync_FallsBack_WhenIntegrationIsStubbed()
    {
        Assert.True(NvidiaAfxIntegration.IsStubbed());

        var fallback = new FakeSpeechAudioEnhancementService();
        var readiness = new FakeReadinessService(new NvidiaAfxRuntimeReadiness(true, "Ready", "C:\\afx", null));
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
    public async Task EnhanceAsync_FallsBack_WhenAecProfileMissingFarEnd_PastStubGate()
    {
        var fallback = new FakeSpeechAudioEnhancementService();
        var readiness = new FakeReadinessService(new NvidiaAfxRuntimeReadiness(true, "Ready", "C:\\afx", null));
        var sut = new NvidiaAfxSpeechAudioEnhancementService(readiness, fallback)
        {
            // Exercise the AEC far-end gate without flipping NvidiaAfxIntegration.IsStubbed().
            IsStubbedOverride = static () => false
        };

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
    public async Task EnhanceAsync_DoesNotProbeReadiness_WhenIntegrationIsStubbed()
    {
        Assert.True(NvidiaAfxIntegration.IsStubbed());

        var fallback = new FakeSpeechAudioEnhancementService();
        var readiness = new ThrowingReadinessService();
        var sut = new NvidiaAfxSpeechAudioEnhancementService(readiness, fallback);

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
