using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.Kokoro;

namespace Trackdub.Inference.Tests;

public sealed class KokoroHelperComponentTests : IDisposable
{
    private readonly List<string> tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ─── KokoroTokenizer ────────────────────────────────────────────────────────

    [Fact]
    public async Task KokoroTokenizer_Encode_WrapsWithBosEos()
    {
        string dir = CreateTempDir();
        WriteMinimalTokenizerJson(dir, new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 });
        var tokenizer = await KokoroTokenizer.LoadAsync(dir);

        long[] tokens = tokenizer.Encode("ab");

        // BOS(0) + 'a'(1) + 'b'(2) + EOS(0)
        Assert.Equal(4, tokens.Length);
        Assert.Equal(0L, tokens[0]);
        Assert.Equal(1L, tokens[1]);
        Assert.Equal(2L, tokens[2]);
        Assert.Equal(0L, tokens[3]);
    }

    [Fact]
    public async Task KokoroTokenizer_Encode_SkipsUnknownChars()
    {
        string dir = CreateTempDir();
        WriteMinimalTokenizerJson(dir, new Dictionary<string, int> { ["a"] = 5 });
        var tokenizer = await KokoroTokenizer.LoadAsync(dir);

        long[] tokens = tokenizer.Encode("aXa");

        // BOS(0) + 'a'(5) + skip 'X' + 'a'(5) + EOS(0)
        Assert.Equal(4, tokens.Length);
        Assert.Equal(5L, tokens[1]);
        Assert.Equal(5L, tokens[2]);
    }

    [Fact]
    public async Task KokoroTokenizer_Encode_EmptyText_ReturnsBosEosOnly()
    {
        string dir = CreateTempDir();
        WriteMinimalTokenizerJson(dir, new Dictionary<string, int> { ["a"] = 1 });
        var tokenizer = await KokoroTokenizer.LoadAsync(dir);

        long[] tokens = tokenizer.Encode("");

        Assert.Equal(2, tokens.Length);
        Assert.Equal(0L, tokens[0]);
        Assert.Equal(0L, tokens[1]);
    }

    [Fact]
    public async Task KokoroTokenizer_Load_ThrowsWhenFileAbsent()
    {
        string dir = CreateTempDir(); // no tokenizer.json

        await Assert.ThrowsAsync<FileNotFoundException>(async () => await KokoroTokenizer.LoadAsync(dir));
    }

    [Fact]
    public async Task KokoroTokenizer_Encode_TruncatesToMaxSequenceLength()
    {
        // Build a vocab with >512 distinct characters to ensure we can exceed MaxSequenceLength
        var vocab = new Dictionary<string, int>();
        for (int i = 0; i < 600; i++)
        {
            // Use characters starting from a safe Unicode range
            vocab[char.ConvertFromUtf32(0x4E00 + i)] = i + 1; // CJK Unified Ideographs
        }

        string dir = CreateTempDir();
        WriteMinimalTokenizerJson(dir, vocab);
        var tokenizer = await KokoroTokenizer.LoadAsync(dir);

        // Create a string with >512 in-vocab characters to trigger truncation
        string longText = string.Concat(Enumerable.Range(0, 600).Select(i => char.ConvertFromUtf32(0x4E00 + i)));

        long[] tokens = tokenizer.Encode(longText);

        // Should be exactly MaxSequenceLength (512) with BOS and EOS
        Assert.Equal(512, tokens.Length);
        // First token should be BOS (0)
        Assert.Equal(0L, tokens[0]);
        // Last token should be EOS (0)
        Assert.Equal(0L, tokens[^1]);
    }

    // ─── KokoroPcmConverter ─────────────────────────────────────────────────────

    [Fact]
    public void KokoroPcmConverter_EncodePcm16Wav_HasCorrectRiffHeader()
    {
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([], sampleRate: 24_000);

        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);

        Assert.Equal((byte)'W', wav[8]);
        Assert.Equal((byte)'A', wav[9]);
        Assert.Equal((byte)'V', wav[10]);
        Assert.Equal((byte)'E', wav[11]);

        Assert.Equal((byte)'f', wav[12]);
        Assert.Equal((byte)'m', wav[13]);
        Assert.Equal((byte)'t', wav[14]);
        Assert.Equal((byte)' ', wav[15]);

        Assert.Equal((byte)'d', wav[36]);
        Assert.Equal((byte)'a', wav[37]);
        Assert.Equal((byte)'t', wav[38]);
        Assert.Equal((byte)'a', wav[39]);
    }

    [Fact]
    public void KokoroPcmConverter_EncodePcm16Wav_EmptySamples_Returns44ByteHeader()
    {
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([], sampleRate: 24_000);

        Assert.Equal(44, wav.Length);
    }

    [Fact]
    public void KokoroPcmConverter_EncodePcm16Wav_LengthMatchesSampleCount()
    {
        float[] samples = new float[100];
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav(samples, sampleRate: 24_000);

        Assert.Equal(44 + 100 * sizeof(short), wav.Length);
    }

    [Fact]
    public void KokoroPcmConverter_EncodePcm16Wav_PositiveFullScaleSampleClampsTo32767()
    {
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([1.0f], sampleRate: 24_000);

        short sample = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44));
        Assert.Equal((short)32_767, sample);
    }

    [Fact]
    public void KokoroPcmConverter_EncodePcm16Wav_NegativeFullScaleSampleClampsToMinusMaxValue()
    {
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([-1.0f], sampleRate: 24_000);

        short sample = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44));
        Assert.Equal((short)-32_767, sample);
    }

    // ─── KokoroVoicepackLoader ──────────────────────────────────────────────────

    [Fact]
    public void KokoroVoicepackLoader_LoadStyleVector_ReadsCorrectRow()
    {
        const int StyleVectorSize = 256;
        string binPath = Path.GetTempFileName();
        try
        {
            float[] row0 = Enumerable.Range(0, StyleVectorSize).Select(i => (float)i).ToArray();
            float[] row1 = Enumerable.Range(1000, StyleVectorSize).Select(i => (float)i).ToArray();
            using (var writer = new BinaryWriter(File.OpenWrite(binPath)))
            {
                foreach (float v in row0) writer.Write(v);
                foreach (float v in row1) writer.Write(v);
            }

            float[] loaded0 = KokoroVoicepackLoader.LoadStyleVector(binPath, tokenCount: 0);
            Assert.Equal(StyleVectorSize, loaded0.Length);
            Assert.Equal(0f, loaded0[0]);
            Assert.Equal(255f, loaded0[255]);

            float[] loaded1 = KokoroVoicepackLoader.LoadStyleVector(binPath, tokenCount: 1);
            Assert.Equal(1000f, loaded1[0]);
            Assert.Equal(1255f, loaded1[255]);
        }
        finally
        {
            File.Delete(binPath);
        }
    }

    [Fact]
    public void KokoroVoicepackLoader_LoadStyleVector_ThrowsWhenFileTooSmall()
    {
        string binPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(binPath, [1, 2, 3]); // too small for one style vector

            Assert.Throws<InvalidOperationException>(() =>
                KokoroVoicepackLoader.LoadStyleVector(binPath, tokenCount: 0));
        }
        finally
        {
            File.Delete(binPath);
        }
    }

    // ─── KokoroVoiceCatalog ─────────────────────────────────────────────────────

    [Fact]
    public async Task KokoroVoiceCatalog_Load_ReturnsEmptyWhenVoicesDirAbsent()
    {
        string dir = CreateTempDir(); // no "voices" subdirectory

        var catalog = await KokoroVoiceCatalog.LoadAsync(dir);

        Assert.Empty(catalog.GetVoices());
    }

    [Fact]
    public async Task KokoroVoiceCatalog_Load_ParsesVoiceFilesCorrectly()
    {
        string dir = CreateTempDir();
        CreateVoicesDir(dir, "af_heart.bin", "bm_george.bin");

        var catalog = await KokoroVoiceCatalog.LoadAsync(dir);
        IReadOnlyList<VoiceCatalogEntry> voices = catalog.GetVoices();

        Assert.Equal(2, voices.Count);
        Assert.Contains(voices, v => v.VoiceId == "af_heart" && v.LanguageCode == "en-us" && v.Gender == "female");
        Assert.Contains(voices, v => v.VoiceId == "bm_george" && v.LanguageCode == "en-gb" && v.Gender == "male");
    }

    [Fact]
    public async Task KokoroVoiceCatalog_Load_IgnoresFilesWithUnrecognizedNamingConvention()
    {
        string dir = CreateTempDir();
        CreateVoicesDir(dir, "af_heart.bin", "invalid.bin");

        var catalog = await KokoroVoiceCatalog.LoadAsync(dir);

        Assert.Single(catalog.GetVoices());
        Assert.Equal("af_heart", catalog.GetVoices()[0].VoiceId);
    }

    [Fact]
    public async Task KokoroVoiceCatalog_GetVoices_FiltersByLanguageCode()
    {
        string dir = CreateTempDir();
        CreateVoicesDir(dir, "af_heart.bin", "ef_dora.bin");

        var catalog = await KokoroVoiceCatalog.LoadAsync(dir);
        IReadOnlyList<VoiceCatalogEntry> enUs = catalog.GetVoices("en-us");

        Assert.Single(enUs);
        Assert.Equal("af_heart", enUs[0].VoiceId);
    }

    [Fact]
    public async Task KokoroVoiceCatalog_TryGetVoice_ReturnsTrueForKnownVoice()
    {
        string dir = CreateTempDir();
        CreateVoicesDir(dir, "af_heart.bin");

        var catalog = await KokoroVoiceCatalog.LoadAsync(dir);
        bool found = catalog.TryGetVoice("af_heart", out VoiceCatalogEntry? entry);

        Assert.True(found);
        Assert.NotNull(entry);
        Assert.Equal("en-us", entry.LanguageCode);
        Assert.Equal("female", entry.Gender);
    }

    [Fact]
    public async Task KokoroVoiceCatalog_TryGetVoice_ReturnsFalseForUnknownVoice()
    {
        string dir = CreateTempDir();
        CreateVoicesDir(dir); // empty voices dir

        var catalog = await KokoroVoiceCatalog.LoadAsync(dir);

        Assert.False(catalog.TryGetVoice("af_heart", out _));
    }

    // ─── EspeakNgPathResolver ──────────────────────────────────────────────────

    [Fact]
    public void EspeakNgPathResolver_Resolve_PrefersBundledInstallerPath()
    {
        string dir = CreateTempDir();
        string bundledPath = Path.Combine(dir, "runtimes", EspeakRuntimeFolder, "native", "espeak-ng", EspeakExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(bundledPath)!);
        File.WriteAllBytes(bundledPath, []);

        string resolved = EspeakNgPathResolver.Resolve(baseDirectory: dir, environmentVariableValue: string.Empty);

        Assert.Equal(bundledPath, resolved);
    }

    [Fact]
    public void EspeakNgPathResolver_Resolve_UsesEnvironmentVariablePath()
    {
        string dir = CreateTempDir();
        string executablePath = Path.Combine(dir, EspeakExecutableName);
        File.WriteAllBytes(executablePath, []);

        string resolved = EspeakNgPathResolver.Resolve(
            baseDirectory: CreateTempDir(),
            workingDirectory: CreateTempDir(),
            environmentVariableValue: executablePath,
            pathEnvironmentValue: string.Empty);

        Assert.Equal(executablePath, resolved);
    }

    [Fact]
    public void EspeakNgPathResolver_Resolve_UsesDeveloperRepoPath()
    {
        string repoRoot = CreateTempDir();
        string workingDirectory = Path.Combine(repoRoot, "src", "Trackdub.App");
        Directory.CreateDirectory(workingDirectory);
        string executablePath = Path.Combine(repoRoot, "tools", "espeak-ng", EspeakExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllBytes(executablePath, []);

        string resolved = EspeakNgPathResolver.Resolve(
            baseDirectory: CreateTempDir(),
            workingDirectory: workingDirectory,
            environmentVariableValue: string.Empty,
            pathEnvironmentValue: string.Empty);

        Assert.Equal(executablePath, resolved);
    }

    [Fact]
    public void EspeakNgPathResolver_Resolve_FallsBackToPathExecutable()
    {
        string dir = CreateTempDir();
        string executablePath = Path.Combine(dir, EspeakExecutableName);
        File.WriteAllBytes(executablePath, []);

        string resolved = EspeakNgPathResolver.Resolve(
            baseDirectory: CreateTempDir(),
            workingDirectory: CreateTempDir(),
            environmentVariableValue: string.Empty,
            pathEnvironmentValue: dir);

        Assert.Equal(executablePath, resolved);
    }

    [Fact]
    public void EspeakNgPathResolver_Resolve_ThrowsActionableErrorWhenMissing()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            EspeakNgPathResolver.Resolve(
                baseDirectory: CreateTempDir(),
                workingDirectory: CreateTempDir(),
                environmentVariableValue: string.Empty,
                pathEnvironmentValue: string.Empty));

        Assert.Contains("eSpeak-NG is required for Kokoro TTS phonemization", exception.Message, StringComparison.Ordinal);
        Assert.Contains("TRACKDUB_ESPEAK_NG_PATH", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("tools", "espeak-ng", EspeakExecutableName), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EspeakNgPhonemizer_Phonemize_WrapsStartupFailureWithActionableError()
    {
        string dir = CreateTempDir();
        string executablePath = Path.Combine(dir, EspeakExecutableName);
        File.WriteAllBytes(executablePath, []);
        var phonemizer = new EspeakNgPhonemizer(executablePath);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            phonemizer.Phonemize("hello", "en-us"));

        Assert.Contains("eSpeak-NG is required for Kokoro TTS phonemization", exception.Message, StringComparison.Ordinal);
        Assert.Contains("TRACKDUB_ESPEAK_NG_PATH", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);
        return dir;
    }

    private static string EspeakExecutableName =>
        OperatingSystem.IsWindows() ? "espeak-ng.exe" : "espeak-ng";

    private static string EspeakRuntimeFolder
    {
        get
        {
            string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64
                    ? "arm64"
                    : "x64";
            if (OperatingSystem.IsWindows()) return $"win-{architecture}";
            if (OperatingSystem.IsMacOS()) return $"osx-{architecture}";
            return $"linux-{architecture}";
        }
    }

    private static void CreateVoicesDir(string modelRoot, params string[] binFileNames)
    {
        string voicesDir = Path.Combine(modelRoot, "voices");
        Directory.CreateDirectory(voicesDir);
        foreach (string name in binFileNames)
        {
            File.WriteAllBytes(Path.Combine(voicesDir, name), []);
        }
    }

    private static void WriteMinimalTokenizerJson(string dir, Dictionary<string, int> vocab)
    {
        var obj = new { model = new { vocab } };
        string json = JsonSerializer.Serialize(obj);
        File.WriteAllText(Path.Combine(dir, "tokenizer.json"), json, Encoding.UTF8);
    }

}
