using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Trackdub.Analyzers.Tests;

public sealed class WavePcm16AnalyzerTests
{
    [Fact]
    public async Task MixMethod_WithoutNormalizePeak_ReportsWarningAsync()
    {
        const string Source = SourceWavePcm16 + """

            internal sealed class AudioMixer
            {
                public void MixStereoAudio()
                {
                    var samples = new float[0];
                    var _ = Trackdub.Media.Waveforms.WavePcm16.WriteSamplesAsync(
                        "a.wav", samples, sampleRate: 48000, channelCount: 2);
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await GetDiagnosticsAsync(Source);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "TRACKDUB001");
        // Roslyn 4.x does not expose the raw messageArgs array on Diagnostic publicly; the formatted
        // text is obtainable via GetMessage(), which substitutes args into Descriptor.MessageFormat.
        Assert.Contains("MixStereoAudio", diagnostic.GetMessage());
    }

    [Fact]
    public async Task MixMethod_WithNormalizePeakTrue_NoDiagnosticAsync()
    {
        const string Source = SourceWavePcm16 + """

            internal sealed class AudioMixer
            {
                public void MixStereoAudio()
                {
                    var samples = new float[0];
                    var _ = Trackdub.Media.Waveforms.WavePcm16.WriteSamplesAsync(
                        "a.wav", samples, sampleRate: 48000, channelCount: 2, normalizePeak: true);
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await GetDiagnosticsAsync(Source);
        Assert.DoesNotContain(diagnostics, d => d.Id == "TRACKDUB001");
    }

    [Fact]
    public async Task RenderMethod_WithoutNormalizePeak_ReportsWarningAsync()
    {
        // RenderAsync is recognized as a mix path (e.g., PreviewRangeRenderer.RenderAsync downmix)
        // and must be flagged when normalizePeak is not passed.
        const string Source = SourceWavePcm16 + """

            internal sealed class AudioRenderer
            {
                public void RenderAsync()
                {
                    var samples = new float[0];
                    var _ = Trackdub.Media.Waveforms.WavePcm16.WriteSamplesAsync(
                        "a.wav", samples, sampleRate: 48000, channelCount: 2);
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await GetDiagnosticsAsync(Source);
        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "TRACKDUB001");
        Assert.Contains("RenderAsync", diagnostic.GetMessage());
    }

    [Fact]
    public async Task MixMethod_CallsWriteMonoAsync_NoDiagnosticAsync()
    {
        // WriteMonoAsync has no normalizePeak parameter and cannot resolve TRACKDUB001,
        // so the analyzer excludes it from diagnosis even in Mix-like method bodies.
        const string Source = SourceWavePcm16 + """

            internal sealed class AudioMixer
            {
                public void BlendSilence()
                {
                    var samples = new float[0];
                    var _ = Trackdub.Media.Waveforms.WavePcm16.WriteMonoAsync(
                        "a.wav", samples, sampleRate: 48000);
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await GetDiagnosticsAsync(Source);
        Assert.DoesNotContain(diagnostics, d => d.Id == "TRACKDUB001");
    }

    private const string SourceWavePcm16 = """
        namespace Trackdub.Media.Waveforms
        {
            public static class WavePcm16
            {
                public static System.Threading.Tasks.Task WriteSamplesAsync(
                    string path,
                    float[] samples,
                    int sampleRate,
                    int channelCount,
                    bool normalizePeak = false,
                    System.Threading.CancellationToken cancellationToken = default)
                {
                    return System.Threading.Tasks.Task.CompletedTask;
                }

                public static System.Threading.Tasks.Task WriteMonoAsync(
                    string path,
                    float[] samples,
                    int sampleRate,
                    System.Threading.CancellationToken cancellationToken = default)
                {
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            }
        }
        """;

    /// <summary>
    /// Performs a single-shot in-process C# compilation against the supplied source with the
    /// <see cref="WavePcm16MultiSourceMixOptInAnalyzer"/> registered, then returns the set of
    /// diagnostics emitted by the analyzer. No third-party harness — uses CSharpCompilation +
    /// CompilationWithAnalyzers directly per Roslyn 4.x best practice.
    /// </summary>
    private static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source);

        string trustedAssembliesLocation = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        MetadataReference[] references =
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Join(trustedAssembliesLocation, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        ];

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "Trackdub.Analyzers.Tests.InProcess",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzer = new WavePcm16MultiSourceMixOptInAnalyzer();
        CompilationWithAnalyzers compilationWithAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));

        return await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
    }
}
