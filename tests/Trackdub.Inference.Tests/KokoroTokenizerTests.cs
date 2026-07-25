using Trackdub.Inference.Onnx.Kokoro;

namespace Trackdub.Inference.Tests;

public sealed class KokoroTokenizerTests : IDisposable
{
    private readonly List<string> tempFiles = [];

    // ── Load ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Load_MissingTokenizerJson_ThrowsFileNotFoundException()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            await Assert.ThrowsAsync<FileNotFoundException>(
                async () => await KokoroTokenizer.LoadAsync(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Load_ValidTokenizerJson_Succeeds()
    {
        string dir = WriteTokenizerJson(new Dictionary<string, int>
        {
            ["a"] = 1,
            ["b"] = 2,
            ["c"] = 3
        });

        KokoroTokenizer tokenizer = await KokoroTokenizer.LoadAsync(dir);

        Assert.NotNull(tokenizer);
    }

    [Fact]
    public async Task Load_IgnoresNonIntegerVocabValues()
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "Trackdub.KokoroTokenizerTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        tempFiles.Add(dir);

        string json = """
            {
              "model": {
                "vocab": {
                  "a": 1,
                  "b": "not_an_int"
                }
              }
            }
            """;

        File.WriteAllText(Path.Combine(dir, "tokenizer.json"), json);

        KokoroTokenizer tokenizer = await KokoroTokenizer.LoadAsync(dir);
        long[] tokens = tokenizer.Encode("ab");

        Assert.Equal(3, tokens.Length);
        Assert.Equal(1L, tokens[1]);
    }

    // ── Encode ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Encode_EmptyString_ReturnsBosEosOnly()
    {
        string dir = WriteTokenizerJson(new Dictionary<string, int>
        {
            ["a"] = 1
        });

        KokoroTokenizer tokenizer = await KokoroTokenizer.LoadAsync(dir);
        long[] tokens = tokenizer.Encode("");

        Assert.Equal(2, tokens.Length);
        Assert.Equal(0L, tokens[0]); // BOS
        Assert.Equal(0L, tokens[1]); // EOS
    }

    [Fact]
    public async Task Encode_KnownCharacters_WrapsWithBosEos()
    {
        string dir = WriteTokenizerJson(new Dictionary<string, int>
        {
            ["a"] = 1,
            ["b"] = 2,
            ["c"] = 3
        });

        KokoroTokenizer tokenizer = await KokoroTokenizer.LoadAsync(dir);
        long[] tokens = tokenizer.Encode("abc");

        Assert.Equal(5, tokens.Length);
        Assert.Equal(0L, tokens[0]);  // BOS ($)
        Assert.Equal(1L, tokens[1]);  // 'a'
        Assert.Equal(2L, tokens[2]);  // 'b'
        Assert.Equal(3L, tokens[3]);  // 'c'
        Assert.Equal(0L, tokens[4]);  // EOS ($)
    }

    [Fact]
    public async Task Encode_UnknownCharacters_AreSkipped()
    {
        string dir = WriteTokenizerJson(new Dictionary<string, int>
        {
            ["a"] = 1,
            ["c"] = 3
        });

        KokoroTokenizer tokenizer = await KokoroTokenizer.LoadAsync(dir);
        long[] tokens = tokenizer.Encode("abc");

        // 'b' not in vocab → skipped
        Assert.Equal(4, tokens.Length);
        Assert.Equal(0L, tokens[0]);  // BOS
        Assert.Equal(1L, tokens[1]);  // 'a'
        Assert.Equal(3L, tokens[2]);  // 'c'
        Assert.Equal(0L, tokens[3]);  // EOS
    }

    [Fact]
    public async Task Encode_AllCharactersUnknown_ReturnsBosEosOnly()
    {
        string dir = WriteTokenizerJson(new Dictionary<string, int>
        {
            ["x"] = 99
        });

        KokoroTokenizer tokenizer = await KokoroTokenizer.LoadAsync(dir);
        long[] tokens = tokenizer.Encode("abcdef");

        Assert.Equal(2, tokens.Length); // BOS + EOS only
        Assert.Equal(0L, tokens[0]);
        Assert.Equal(0L, tokens[1]);
    }

    [Fact]
    public async Task Encode_LongInput_TruncatesAt512Tokens()
    {
        // Build a 1-char vocab and feed 600 chars
        string dir = WriteTokenizerJson(new Dictionary<string, int>
        {
            ["a"] = 1
        });

        KokoroTokenizer tokenizer = await KokoroTokenizer.LoadAsync(dir);
        string longInput = new string('a', 600);

        long[] tokens = tokenizer.Encode(longInput);

        // Max sequence is 512 (BOS + up to 510 tokens + EOS)
        Assert.Equal(512, tokens.Length);
        Assert.Equal(0L, tokens[0]);           // BOS
        Assert.Equal(0L, tokens[511]);         // EOS
    }

    [Fact]
    public async Task Encode_ExactlyAtLimit_ProducesExactly512Tokens()
    {
        string dir = WriteTokenizerJson(new Dictionary<string, int>
        {
            ["a"] = 1
        });

        KokoroTokenizer tokenizer = await KokoroTokenizer.LoadAsync(dir);
        // 510 'a's + BOS + EOS = 512
        string input = new string('a', 510);

        long[] tokens = tokenizer.Encode(input);

        Assert.Equal(512, tokens.Length);
    }

    [Fact]
    public async Task Encode_BosAndEosTokenIdIsZero()
    {
        string dir = WriteTokenizerJson(new Dictionary<string, int>
        {
            ["a"] = 1
        });

        KokoroTokenizer tokenizer = await KokoroTokenizer.LoadAsync(dir);
        long[] tokens = tokenizer.Encode("a");

        Assert.Equal(0L, tokens[0]);
        Assert.Equal(0L, tokens[tokens.Length - 1]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string WriteTokenizerJson(Dictionary<string, int> vocab)
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "Trackdub.KokoroTokenizerTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        tempFiles.Add(dir); // track for cleanup

        var vocabEntries = string.Join(",\n    ",
            vocab.Select(kv => $"\"{EscapeJson(kv.Key)}\": {kv.Value}"));

        string json = $$"""
            {
              "model": {
                "vocab": {
                  {{vocabEntries}}
                }
              }
            }
            """;

        File.WriteAllText(Path.Combine(dir, "tokenizer.json"), json);
        return dir;
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public void Dispose()
    {
        foreach (string path in tempFiles)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
