using Trackdub.Contracts;
using Trackdub.Composition.NvidiaAfx;
using Trackdub.Infrastructure.Components;
using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Composition.Tests;

public sealed class NvidiaAfxRuntimeReadinessServiceTests
{
    private const NvidiaAfxProfile Profile = NvidiaAfxProfile.NoiseAndReverb;

    [Fact]
    public void GetReadiness_ReturnsMissingModels_WhenRuntimeInstalledWithoutProfileModels()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = RuntimeFixture.Create(withModels: false);
        var service = fixture.CreateService(new CountingProbe());

        NvidiaAfxRuntimeReadiness readiness = service.GetReadiness(Profile);

        Assert.False(readiness.IsReady);
        Assert.Equal("Missing model files", readiness.StatusLabel);
        Assert.Equal(fixture.RuntimePath, readiness.RuntimeRoot);
    }

    [Fact]
    public void GetReadiness_RefusesAnEarlyAccessEffect_UnlessItIsExplicitlyAllowed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = RuntimeFixture.Create();
        var probe = new CountingProbe();
        var service = fixture.CreateService(probe);

        NvidiaAfxRuntimeReadiness readiness = service.GetReadiness(NvidiaAfxProfile.SpeakerFocus);

        Assert.False(readiness.IsReady);
        Assert.Equal("Early Access disabled", readiness.StatusLabel);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public void GetReadiness_RequiresTheLicenseToBeAccepted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = RuntimeFixture.Create();
        var probe = new CountingProbe();
        var service = fixture.CreateService(probe, licenseAccepted: false);

        NvidiaAfxRuntimeReadiness readiness = service.GetReadiness(Profile);

        Assert.False(readiness.IsReady);
        Assert.Equal("License not accepted", readiness.StatusLabel);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public void GetReadiness_ReportsUnreadableSettings_InsteadOfThrowing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = RuntimeFixture.Create();
        var service = fixture.CreateService(
            new CountingProbe(),
            settingsProvider: () => throw new IOException("settings.json is locked"));

        NvidiaAfxRuntimeReadiness readiness = service.GetReadiness(Profile);

        Assert.False(readiness.IsReady);
        Assert.Equal("Settings unavailable", readiness.StatusLabel);
        Assert.Contains("locked", readiness.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReadiness_ProbesEverySupportedRate_ThenCachesTheSuccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = RuntimeFixture.Create();
        var probe = new CountingProbe();
        var service = fixture.CreateService(probe);

        NvidiaAfxRuntimeReadiness first = service.GetReadiness(Profile);
        NvidiaAfxRuntimeReadiness second = service.GetReadiness(Profile);

        Assert.True(first.IsReady);
        Assert.True(second.IsReady);
        Assert.Equal("ada", first.ArchitectureBucket);
        Assert.Equal([16000, 48000], probe.ProbedRates.Order());
        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public void GetReadiness_IsNotReady_WhenAnySupportedRateFailsItsProbe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = RuntimeFixture.Create();
        var probe = new CountingProbe(succeedsFor: rate => rate == 48000);
        var service = fixture.CreateService(probe);

        NvidiaAfxRuntimeReadiness readiness = service.GetReadiness(Profile);

        Assert.False(readiness.IsReady);
        Assert.Equal("Native probe failed", readiness.StatusLabel);
        Assert.Contains("16000 Hz", readiness.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void GetReadiness_DoesNotCacheFailedProbes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = RuntimeFixture.Create();
        var probe = new CountingProbe(succeedsFor: _ => false);
        var service = fixture.CreateService(probe);

        service.GetReadiness(Profile);
        service.GetReadiness(Profile);

        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public void GetReadiness_NoticesARemovedModel_EvenAfterASuccessfulProbe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = RuntimeFixture.Create();
        var probe = new CountingProbe();
        var service = fixture.CreateService(probe);
        Assert.True(service.GetReadiness(Profile).IsReady);

        fixture.RemoveModel("dereverb_denoiser_16k");
        NvidiaAfxRuntimeReadiness readiness = service.GetReadiness(Profile);

        Assert.False(readiness.IsReady);
        Assert.Equal("Missing model files", readiness.StatusLabel);
    }

    [Fact]
    public void GetReadiness_ProbesAgain_WhenTheDetectedArchitectureChanges()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = RuntimeFixture.Create();
        var probe = new CountingProbe();
        var detector = new MutableArchitectureDetector("ada");
        var service = fixture.CreateService(probe, detector);
        Assert.Equal("ada", service.GetReadiness(Profile).ArchitectureBucket);
        int probesForAda = probe.CallCount;

        detector.Buckets = ["ampere"];
        NvidiaAfxRuntimeReadiness readiness = service.GetReadiness(Profile);

        Assert.True(readiness.IsReady);
        Assert.Equal("ampere", readiness.ArchitectureBucket);
        Assert.Equal(probesForAda * 2, probe.CallCount);
    }

    [Fact]
    public void GetReadiness_TriesTheNextArchitectureCandidate_WhenTheFirstDoesNotRun()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = RuntimeFixture.Create();
        var probe = new CountingProbe(succeedsForArchitecture: architecture => architecture == "ampere");
        var service = fixture.CreateService(probe, new MutableArchitectureDetector("ada", "ampere"));

        NvidiaAfxRuntimeReadiness readiness = service.GetReadiness(Profile);

        Assert.True(readiness.IsReady);
        Assert.Equal("ampere", readiness.ArchitectureBucket);
        Assert.Equal(["ada", "ampere", "ampere"], probe.ProbedArchitectures);
    }

    [Fact]
    public void GetReadiness_ReportsTheFirstCandidatesFailure_WhenNoCandidateRuns()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = RuntimeFixture.Create();
        var service = fixture.CreateService(
            new CountingProbe(succeedsFor: _ => false),
            new MutableArchitectureDetector("ada", "ampere"));

        NvidiaAfxRuntimeReadiness readiness = service.GetReadiness(Profile);

        Assert.False(readiness.IsReady);
        Assert.Contains("'ada'", readiness.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReadiness_SharesOneProbePerRate_AcrossConcurrentCallers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = RuntimeFixture.Create();
        using var gate = new ManualResetEventSlim(false);
        var probe = new CountingProbe(gate: gate);
        var service = fixture.CreateService(probe);

        Task<NvidiaAfxRuntimeReadiness>[] callers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => service.GetReadiness(Profile)))
            .ToArray();
        Assert.True(probe.Started.Wait(TimeSpan.FromSeconds(10)), "No probe started.");
        gate.Set();
        NvidiaAfxRuntimeReadiness[] results = await Task.WhenAll(callers);

        Assert.All(results, readiness => Assert.True(readiness.IsReady));
        Assert.Equal(2, probe.CallCount);
    }

    private sealed class CountingProbe(
        Func<int, bool>? succeedsFor = null,
        Func<string?, bool>? succeedsForArchitecture = null,
        ManualResetEventSlim? gate = null) : INvidiaAfxEffectProbe
    {
        private readonly object _sync = new();
        private readonly List<int> _rates = [];
        private readonly List<string?> _architectures = [];

        public ManualResetEventSlim Started { get; } = new(false);

        public int CallCount
        {
            get
            {
                lock (_sync)
                {
                    return _rates.Count;
                }
            }
        }

        public IReadOnlyList<int> ProbedRates
        {
            get
            {
                lock (_sync)
                {
                    return [.. _rates];
                }
            }
        }

        public IReadOnlyList<string?> ProbedArchitectures
        {
            get
            {
                lock (_sync)
                {
                    return [.. _architectures];
                }
            }
        }

        public NvidiaAfxEffectProbeResult Probe(
            string runtimeRoot,
            NvidiaAfxProfileDefinition profile,
            int inputSampleRate,
            string? architectureBucket = null)
        {
            lock (_sync)
            {
                _rates.Add(inputSampleRate);
                _architectures.Add(architectureBucket);
            }

            Started.Set();
            gate?.Wait(TimeSpan.FromSeconds(10));

            bool succeeds = (succeedsFor?.Invoke(inputSampleRate) ?? true)
                            && (succeedsForArchitecture?.Invoke(architectureBucket) ?? true);
            return succeeds
                ? new NvidiaAfxEffectProbeResult(true, null, inputSampleRate)
                : new NvidiaAfxEffectProbeResult(false, "synthetic probe failure", null);
        }
    }

    private sealed class MutableArchitectureDetector(params string[] buckets) : INvidiaAfxArchitectureDetector
    {
        public string[] Buckets { get; set; } = buckets;

        public string DetectArchitectureBucket() => Buckets.FirstOrDefault() ?? "unsupported";

        public IReadOnlyList<string> DetectArchitectureBuckets() => Buckets;
    }

    private sealed class TestLogger : IApplicationLogger
    {
        public void LogDebug(string message) { }
        public void LogInformation(string message) { }
        public void LogWarning(string message, Exception? exception = null) { }
        public void LogError(string message, Exception? exception = null) { }
    }

    /// <summary>
    /// A temp runtime folder with a stub native library, flat models for the Noise + Reverb profile
    /// and a manifest that lists the ada and ampere architectures.
    /// </summary>
    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _manifestPath;

        private RuntimeFixture(string tempRoot, string runtimePath, string manifestPath)
        {
            _tempRoot = tempRoot;
            RuntimePath = runtimePath;
            _manifestPath = manifestPath;
        }

        public string RuntimePath { get; }

        public static RuntimeFixture Create(bool withModels = true)
        {
            string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-readiness-{Guid.NewGuid():N}");
            string runtimePath = Path.Join(tempRoot, "runtime");
            Directory.CreateDirectory(Path.Join(runtimePath, "models"));
            File.WriteAllBytes(Path.Join(runtimePath, "NvAudioEffects.dll"), [0x00]);
            if (withModels)
            {
                File.WriteAllText(Path.Join(runtimePath, "models", "dereverb_denoiser_16k.nvam"), "stub");
                File.WriteAllText(Path.Join(runtimePath, "models", "dereverb_denoiser_48k.nvam"), "stub");
            }

            string manifestPath = Path.Join(tempRoot, "manifest.json");
            File.WriteAllText(manifestPath, ManifestJson("ada", "ampere"));
            return new RuntimeFixture(tempRoot, runtimePath, manifestPath);
        }

        public void RemoveModel(string stem) =>
            File.Delete(Path.Join(RuntimePath, "models", stem + ".nvam"));

        // Settings outrank TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT, so these stay deterministic when a
        // developer has the variable set for the live GPU tests.
        public NvidiaAfxRuntimeReadinessService CreateService(
            INvidiaAfxEffectProbe probe,
            INvidiaAfxArchitectureDetector? detector = null,
            bool licenseAccepted = true,
            Func<StudioSettings>? settingsProvider = null) =>
            new(
                new ComponentStore(_tempRoot, new TestLogger()),
                detector ?? new MutableArchitectureDetector("ada"),
                _manifestPath,
                settingsProvider: settingsProvider ?? (() => StudioSettings.Default with
                {
                    NvidiaAfxRuntimeDirectory = RuntimePath,
                    NvidiaAfxLicenseAccepted = licenseAccepted,
                }),
                effectProbe: probe);

        public void Dispose()
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }

        private static string ManifestJson(params string[] architectures) =>
            "{ \"manifestVersion\": \"1.0.0\", \"packages\": [" +
            string.Join(",", architectures.Select(architecture =>
                $"{{ \"architecture\": \"{architecture}\", \"downloadUrl\": \"https://example.invalid\", " +
                "\"sha256\": \"0\", \"sizeBytes\": 1, \"runtimeVersion\": \"1\", " +
                "\"licenseUrl\": \"https://example.invalid/license\", " +
                "\"modelRelativePaths\": [ \"models/dereverb_denoiser_48k.nvam\" ] }")) +
            "] }";
    }
}
