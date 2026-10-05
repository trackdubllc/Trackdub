using System.Runtime.CompilerServices;
using Trackdub.Composition.NvidiaAfx;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Composition.Tests;

/// <summary>
/// Skips unless <c>TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT</c> points at an installed NVIDIA AFX runtime
/// (a folder containing <c>NVAudioEffects.dll</c>) on a Windows NVIDIA RTX machine.
/// </summary>
public sealed class NvidiaAfxLiveRuntimeFactAttribute : FactAttribute
{
    public const string RuntimeRootVariable = "TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT";

    public NvidiaAfxLiveRuntimeFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        string? root = Environment.GetEnvironmentVariable(RuntimeRootVariable);
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(root)
            || !NvidiaAfxRuntimeLayout.HasNativeLibrary(root))
        {
            Skip = $"Set {RuntimeRootVariable} to an installed NVIDIA AFX runtime (folder containing " +
                   "NVAudioEffects.dll) on a Windows NVIDIA RTX machine to run the live create/run proofs.";
        }
    }

    public static string RuntimeRoot =>
        Environment.GetEnvironmentVariable(RuntimeRootVariable)
        ?? throw new InvalidOperationException($"{RuntimeRootVariable} is not set.");
}

/// <summary>
/// Hardware-gated proof that the Maxine native create/load/run path works against a real runtime.
/// These are the evidence behind flipping <see cref="NvidiaAfxIntegration.IsStubbed"/>; they never
/// run in default CI.
/// </summary>
public sealed class NvidiaAfxLiveRuntimeTests(Xunit.ITestOutputHelper output)
{
    [NvidiaAfxLiveRuntimeFact]
    public void Probe_succeeds_for_every_selectable_profile_and_rate()
    {
        string root = NvidiaAfxLiveRuntimeFactAttribute.RuntimeRoot;
        string architecture = new NvidiaAfxArchitectureDetector().DetectArchitectureBucket();
        output.WriteLine($"runtime root: {root}");
        output.WriteLine($"architecture bucket: {architecture}");

        var failures = new List<string>();
        foreach (NvidiaAfxProfileDefinition definition in NvidiaAfxProfileCatalog.Definitions
                     .Where(definition => !definition.RequiresFarEndReference))
        {
            foreach (int sampleRate in definition.SupportedSampleRates)
            {
                NvidiaAfxEffectProbeResult result = NvidiaAfxSessionEffectProbe.Instance
                    .Probe(root, definition, sampleRate, architecture);
                output.WriteLine(
                    $"{definition.Profile} @ {sampleRate} Hz: " +
                    (result.Succeeded ? $"OK (output {result.OutputSampleRate} Hz)" : $"FAIL {result.FailureReason}"));
                if (!result.Succeeded)
                {
                    failures.Add($"{definition.Profile} @ {sampleRate} Hz: {result.FailureReason}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [NvidiaAfxLiveRuntimeFact]
    public void Same_rate_effects_process_real_audio_at_every_supported_rate()
    {
        string root = NvidiaAfxLiveRuntimeFactAttribute.RuntimeRoot;
        string architecture = new NvidiaAfxArchitectureDetector().DetectArchitectureBucket();

        var failures = new List<string>();
        foreach (NvidiaAfxProfileDefinition definition in NvidiaAfxProfileCatalog.Definitions
                     .Where(definition => !definition.RequiresFarEndReference
                                          && definition.ResolveOutputSampleRate(definition.SupportedSampleRates[0])
                                             == definition.SupportedSampleRates[0]))
        {
            foreach (int sampleRate in definition.SupportedSampleRates)
            {
                try
                {
                    float[] input = BuildNoisySpeechLikeSignal(sampleRate, seconds: 2);
                    using NvidiaAfxSession session = NvidiaAfxSession.Create(
                        definition,
                        root,
                        sampleRate,
                        intensityRatio: 1.0f,
                        architecture);
                    float[] processed = session.Process(input);

                    double meanAbsDifference = input.Zip(processed, (a, b) => Math.Abs(a - b)).Average();
                    output.WriteLine(
                        $"{definition.Profile} @ {sampleRate} Hz: frame in/out " +
                        $"{session.NumInputSamplesPerFrame}/{session.NumOutputSamplesPerFrame}, " +
                        $"samples in/out {input.Length}/{processed.Length}, mean |in-out| {meanAbsDifference:F5}");

                    if (processed.Length != input.Length)
                    {
                        failures.Add($"{definition.Profile} @ {sampleRate} Hz: length {processed.Length} != {input.Length}");
                    }
                    else if (processed.Any(sample => !float.IsFinite(sample)))
                    {
                        failures.Add($"{definition.Profile} @ {sampleRate} Hz: non-finite output");
                    }
                    else if (meanAbsDifference <= 1e-4)
                    {
                        failures.Add($"{definition.Profile} @ {sampleRate} Hz: output identical to input");
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or DllNotFoundException)
                {
                    failures.Add($"{definition.Profile} @ {sampleRate} Hz: {ex.Message}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [NvidiaAfxLiveRuntimeFact]
    public void Telephony_upscale_changes_the_sample_rate_when_models_are_present()
    {
        string root = NvidiaAfxLiveRuntimeFactAttribute.RuntimeRoot;
        string architecture = new NvidiaAfxArchitectureDetector().DetectArchitectureBucket();
        NvidiaAfxProfileDefinition definition = NvidiaAfxProfileCatalog.GetDefinition(NvidiaAfxProfile.TelephonyUpscale);
        const int inputRate = 8000;

        float[] input = BuildNoisySpeechLikeSignal(inputRate, seconds: 2);
        using NvidiaAfxSession session = NvidiaAfxSession.Create(
            definition,
            root,
            inputRate,
            intensityRatio: 1.0f,
            architecture);
        float[] processed = session.Process(input);

        output.WriteLine(
            $"telephony frame in/out {session.NumInputSamplesPerFrame}/{session.NumOutputSamplesPerFrame}, " +
            $"output rate {session.OutputSampleRate} Hz, samples in/out {input.Length}/{processed.Length}");

        Assert.Equal(16000, session.OutputSampleRate);
        Assert.Equal(input.Length * 2, processed.Length);
        Assert.All(processed, sample => Assert.True(float.IsFinite(sample)));
    }

    private static float[] BuildNoisySpeechLikeSignal(int sampleRate, int seconds)
    {
        var random = new Random(1234);
        float[] signal = new float[sampleRate * seconds];
        for (int index = 0; index < signal.Length; index++)
        {
            double time = (double)index / sampleRate;
            double tone = 0.25 * Math.Sin(2 * Math.PI * 220 * time) + 0.15 * Math.Sin(2 * Math.PI * 660 * time);
            double noise = 0.08 * (random.NextDouble() * 2 - 1);
            signal[index] = (float)(tone + noise);
        }

        return signal;
    }
}
