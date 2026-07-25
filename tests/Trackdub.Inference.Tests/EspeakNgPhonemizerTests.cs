using Trackdub.Inference.Onnx.Kokoro;

namespace Trackdub.Inference.Tests;

public sealed class EspeakNgPhonemizerTests : IDisposable
{
    private readonly List<string> tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Bundled data path resolution ───────────────────────────────────────────

    [Fact]
    public void TryGetBundledEspeakDataDirectory_DataFolderNextToExecutable_ReturnsExecutableDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"espeak-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "espeak-ng-data"));
        tempDirs.Add(dir);
        string executablePath = Path.Combine(dir, "espeak-ng.exe");

        // The standalone Windows build crashes without ESPEAK_DATA_PATH; the bundled
        // data folder next to the executable must be detected so Phonemize can set it.
        Assert.Equal(dir, EspeakNgPhonemizer.TryGetBundledEspeakDataDirectory(executablePath));
    }

    [Fact]
    public void TryGetBundledEspeakDataDirectory_NoDataFolder_ReturnsNull()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"espeak-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);
        string executablePath = Path.Combine(dir, "espeak-ng.exe");

        Assert.Null(EspeakNgPhonemizer.TryGetBundledEspeakDataDirectory(executablePath));
    }

    // ── Constructor validation ─────────────────────────────────────────────────

    [Fact]
    public void Constructor_DefaultExecutablePath_DoesNotThrow()
    {
        // Should construct without error; actual process invocation is not tested here.
        using IDisposable env = SetEnvironmentVariable(
            EspeakNgPathResolver.EnvironmentVariableName,
            CreateFakeExecutable());

        var phonemizer = new EspeakNgPhonemizer();

        Assert.NotNull(phonemizer);
    }

    [Fact]
    public void Constructor_NullExecutablePath_UsesDefaultResolver()
    {
        using IDisposable env = SetEnvironmentVariable(
            EspeakNgPathResolver.EnvironmentVariableName,
            CreateFakeExecutable());

        var phonemizer = new EspeakNgPhonemizer(null!);

        Assert.NotNull(phonemizer);
    }

    [Fact]
    public void Constructor_WhitespaceExecutablePath_UsesDefaultResolver()
    {
        using IDisposable env = SetEnvironmentVariable(
            EspeakNgPathResolver.EnvironmentVariableName,
            CreateFakeExecutable());

        var phonemizer = new EspeakNgPhonemizer("   ");

        Assert.NotNull(phonemizer);
    }

    [Fact]
    public void Constructor_EmptyExecutablePath_UsesDefaultResolver()
    {
        using IDisposable env = SetEnvironmentVariable(
            EspeakNgPathResolver.EnvironmentVariableName,
            CreateFakeExecutable());

        var phonemizer = new EspeakNgPhonemizer("");

        Assert.NotNull(phonemizer);
    }

    // ── Phonemize input validation ─────────────────────────────────────────────

    [Fact]
    public void Phonemize_EmptyText_ThrowsArgumentException()
    {
        var phonemizer = new EspeakNgPhonemizer(CreateFakeExecutable());

        Assert.Throws<ArgumentException>(
            () => phonemizer.Phonemize("", "en-us"));
    }

    [Fact]
    public void Phonemize_WhitespaceText_ThrowsArgumentException()
    {
        var phonemizer = new EspeakNgPhonemizer(CreateFakeExecutable());

        Assert.Throws<ArgumentException>(
            () => phonemizer.Phonemize("   ", "en-us"));
    }

    [Fact]
    public void Phonemize_EmptyLanguageCode_ThrowsArgumentException()
    {
        var phonemizer = new EspeakNgPhonemizer(CreateFakeExecutable());

        Assert.Throws<ArgumentException>(
            () => phonemizer.Phonemize("Hello world", ""));
    }

    [Fact]
    public void Phonemize_WhitespaceLanguageCode_ThrowsArgumentException()
    {
        var phonemizer = new EspeakNgPhonemizer(CreateFakeExecutable());

        Assert.Throws<ArgumentException>(
            () => phonemizer.Phonemize("Hello world", "   "));
    }

    // ── Language code pattern validation ──────────────────────────────────────

    [Theory]
    [InlineData("en")]
    [InlineData("en-us")]
    [InlineData("en_US")]
    [InlineData("zh-TW")]
    [InlineData("pt-BR")]
    [InlineData("123")]
    public void Phonemize_ValidLanguageCodePattern_DoesNotThrowValidationError(string languageCode)
    {
        // These should pass pattern validation (they may fail later if the process can't start,
        // but the ArgumentException from pattern validation must not fire).
        var phonemizer = new EspeakNgPhonemizer(CreateFakeExecutable());

        Exception? thrown = Record.Exception(
            () => phonemizer.Phonemize("Hello", languageCode));

        // Should NOT be an ArgumentException (validation error) — it may be
        // Win32Exception/IOException from process launch, which is acceptable.
        Assert.True(
            thrown is null || thrown is not ArgumentException,
            $"Expected no ArgumentException but got {thrown?.GetType().Name}: {thrown?.Message}");
    }

    [Theory]
    [InlineData("en us")]          // space inside
    [InlineData("en.us")]          // dot not allowed
    [InlineData("en/us")]          // slash not allowed
    [InlineData("en;us")]          // semicolon not allowed
    [InlineData("(en)")]           // parentheses not allowed
    public void Phonemize_InvalidLanguageCodePattern_ThrowsArgumentException(string languageCode)
    {
        var phonemizer = new EspeakNgPhonemizer(CreateFakeExecutable());

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => phonemizer.Phonemize("Hello", languageCode));

        Assert.Contains("Language code may only contain", ex.Message, StringComparison.Ordinal);
        Assert.Equal("languageCode", ex.ParamName);
    }

    [Fact]
    public void Phonemize_InvalidLanguageCode_ExceptionNamedCorrectly()
    {
        var phonemizer = new EspeakNgPhonemizer(CreateFakeExecutable());

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => phonemizer.Phonemize("Hello", "bad language code"));

        Assert.Equal("languageCode", ex.ParamName);
    }

    [Fact]
    public void CreateExitException_DllMissingExitCode_ReturnsActionableMessage()
    {
        InvalidOperationException exception = EspeakNgPhonemizer.CreateExitException(
            unchecked((int)0xC0000135),
            @"D:\Dev\Trackdub\tools\espeak-ng\espeak-ng.exe");

        Assert.Contains("dependent DLL", exception.Message, StringComparison.Ordinal);
        Assert.Contains("full eSpeak-NG runtime folder", exception.Message, StringComparison.Ordinal);
        Assert.Contains("espeak-ng-data", exception.Message, StringComparison.Ordinal);
    }

    private string CreateFakeExecutable()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);
        string executablePath = Path.Combine(dir, "espeak-ng.exe");
        File.WriteAllBytes(executablePath, []);
        return executablePath;
    }

    private static IDisposable SetEnvironmentVariable(string name, string? value) =>
        new EnvironmentVariableScope(name, value);

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string name;
        private readonly string? previousValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            this.name = name;
            previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(name, previousValue);
        }
    }
}
