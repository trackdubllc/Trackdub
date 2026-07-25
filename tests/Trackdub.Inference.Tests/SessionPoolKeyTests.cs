using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Unit tests for <see cref="SessionPoolKey"/> equality, path hashing, and factory methods.
/// These tests are pure (no I/O, no ONNX runtime).
/// </summary>
public sealed class SessionPoolKeyTests
{
    // ── Equality ──────────────────────────────────────────────────────────────

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new SessionPoolKey("kokoro", "model-id", "q4", ExecutionProviderKind.Cpu, "abc123", 0, "default");
        var b = new SessionPoolKey("kokoro", "model-id", "q4", ExecutionProviderKind.Cpu, "abc123", 0, "default");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void RecordEquality_EngineFamilyDifferentCase_AreEqual()
    {
        // EngineFamily is normalised to lowercase on construction so keys with different
        // casing for the same logical engine are equal (consistent with EvictModelAsync semantics).
        var a = new SessionPoolKey("Kokoro", null, null, ExecutionProviderKind.Cpu, "abc", 0, "default");
        var b = new SessionPoolKey("kokoro", null, null, ExecutionProviderKind.Cpu, "abc", 0, "default");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal("kokoro", a.EngineFamily);
    }

    [Fact]
    public void RecordEquality_ModelIdDifferentCase_AreEqual()
    {
        var a = new SessionPoolKey("kokoro", "Model-ID", null, ExecutionProviderKind.Cpu, "abc", 0, "default");
        var b = new SessionPoolKey("kokoro", "model-id", null, ExecutionProviderKind.Cpu, "abc", 0, "default");

        Assert.Equal(a, b);
        Assert.Equal("model-id", a.ModelId);
    }

    [Fact]
    public void RecordEquality_VariantDifferentCase_AreEqual()
    {
        var a = new SessionPoolKey("kokoro", null, "Q4", ExecutionProviderKind.Cpu, "abc", 0, "default");
        var b = new SessionPoolKey("kokoro", null, "q4", ExecutionProviderKind.Cpu, "abc", 0, "default");

        Assert.Equal(a, b);
        Assert.Equal("q4", a.Variant);
    }

    [Fact]
    public void RecordEquality_GraphRoleDifferentCase_AreEqual()
    {
        var a = new SessionPoolKey("kokoro", null, null, ExecutionProviderKind.Cpu, "abc", 0, "Default");
        var b = new SessionPoolKey("kokoro", null, null, ExecutionProviderKind.Cpu, "abc", 0, "default");

        Assert.Equal(a, b);
        Assert.Equal("default", a.GraphRole);
    }

    [Fact]
    public void RecordEquality_DifferentEngineFamily_NotEqual()
    {
        var a = new SessionPoolKey("kokoro", null, null, ExecutionProviderKind.Cpu, "abc", 0, "default");
        var b = new SessionPoolKey("whisper-onnx", null, null, ExecutionProviderKind.Cpu, "abc", 0, "default");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentGraphRole_NotEqual()
    {
        var a = new SessionPoolKey("whisper-onnx", "model", null, ExecutionProviderKind.Cpu, "abc", 0, "encoder");
        var b = new SessionPoolKey("whisper-onnx", "model", null, ExecutionProviderKind.Cpu, "abc", 0, "decoder");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentProvider_NotEqual()
    {
        var a = new SessionPoolKey("kokoro", null, null, ExecutionProviderKind.Cpu, "abc", 0, "default");
        var b = new SessionPoolKey("kokoro", null, null, ExecutionProviderKind.DirectMl, "abc", 0, "default");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentDeviceId_NotEqual()
    {
        var a = new SessionPoolKey("kokoro", null, null, ExecutionProviderKind.Cpu, "abc", 0, "default");
        var b = new SessionPoolKey("kokoro", null, null, ExecutionProviderKind.Cpu, "abc", 1, "default");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentOptionsFingerprint_NotEqual()
    {
        var a = new SessionPoolKey("kokoro", null, null, ExecutionProviderKind.Cpu, "abc", 0, "default", "options-a");
        var b = new SessionPoolKey("kokoro", null, null, ExecutionProviderKind.Cpu, "abc", 0, "default", "options-b");

        Assert.NotEqual(a, b);
    }

    // ── HashPath ──────────────────────────────────────────────────────────────

    [Fact]
    public void HashPath_SamePath_ReturnsSameHash()
    {
        string hash1 = SessionPoolKey.HashPath(@"C:\Models\model.onnx");
        string hash2 = SessionPoolKey.HashPath(@"C:\Models\model.onnx");

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HashPath_DifferentCase_IsWindowsSameHash_OtherwiseDifferent()
    {
        // On Windows, paths are typically case-insensitive, so paths that differ only
        // in casing produce the same hash by default and reuse sessions correctly.
        // On macOS and Linux the path is hashed as-is (case-sensitive), so the hashes differ.
        string hash1 = SessionPoolKey.HashPath(@"C:\models\Model.onnx");
        string hash2 = SessionPoolKey.HashPath(@"C:\MODELS\model.ONNX");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(hash1, hash2);
        }
        else
        {
            Assert.NotEqual(hash1, hash2);
        }
    }

    [Fact]
    public void HashPath_DifferentCase_CanPreserveWindowsPathCaseWithAppContextSwitch()
    {
        AppContext.SetSwitch("Trackdub.Inference.Onnx.SessionPoolKey.PreserveWindowsPathCase", true);
        try
        {
            string hash1 = SessionPoolKey.HashPath(@"C:\models\Model.onnx");
            string hash2 = SessionPoolKey.HashPath(@"C:\MODELS\model.ONNX");

            Assert.NotEqual(hash1, hash2);
        }
        finally
        {
            AppContext.SetSwitch("Trackdub.Inference.Onnx.SessionPoolKey.PreserveWindowsPathCase", false);
        }
    }

    [Fact]
    public void HashPath_DifferentPaths_ReturnDifferentHashes()
    {
        string hash1 = SessionPoolKey.HashPath(@"C:\Models\encoder.onnx");
        string hash2 = SessionPoolKey.HashPath(@"C:\Models\decoder.onnx");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void HashPath_ReturnsLowercaseHex()
    {
        string hash = SessionPoolKey.HashPath(@"C:\Models\model.onnx");

        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.All(hash, c => Assert.True(char.IsAsciiHexDigit(c)));
    }

    // ── HashOptions ─────────────────────────────────────────────────────────

    [Fact]
    public void HashOptions_NullOrEmpty_ReturnsDefaultFingerprint()
    {
        Assert.Equal(SessionPoolKey.HashOptions(null), SessionPoolKey.HashOptions(new Dictionary<string, string>()));
        Assert.Equal("default", SessionPoolKey.HashOptions(null));
    }

    [Fact]
    public void HashOptions_SamePairsInDifferentOrder_ReturnsSameHash()
    {
        var first = new Dictionary<string, string>
        {
            ["enable_cuda_graph"] = "1",
            ["nv_runtime_cache_path"] = @"C:\cache"
        };
        var second = new Dictionary<string, string>
        {
            ["nv_runtime_cache_path"] = @"C:\cache",
            ["enable_cuda_graph"] = "1"
        };

        Assert.Equal(SessionPoolKey.HashOptions(first), SessionPoolKey.HashOptions(second));
    }

    [Fact]
    public void HashOptions_DifferentValues_ReturnsDifferentHashes()
    {
        var first = new Dictionary<string, string> { ["enable_cuda_graph"] = "1" };
        var second = new Dictionary<string, string> { ["enable_cuda_graph"] = "0" };

        Assert.NotEqual(SessionPoolKey.HashOptions(first), SessionPoolKey.HashOptions(second));
    }

    // ── Factory helpers ───────────────────────────────────────────────────────

    [Fact]
    public void ForSingle_SetsGraphRoleToDefault()
    {
        var key = SessionPoolKey.ForSingle("kokoro", @"C:\Models\model.onnx", ExecutionProviderKind.Cpu);

        Assert.Equal("default", key.GraphRole);
        Assert.Equal("default", key.OptionsFingerprint);
        Assert.Equal("kokoro", key.EngineFamily);
        Assert.Equal(ExecutionProviderKind.Cpu, key.Provider);
    }

    [Fact]
    public void ForSingle_DifferentOptionsFingerprint_ProducesDifferentKeys()
    {
        var first = SessionPoolKey.ForSingle(
            "kokoro",
            @"C:\Models\model.onnx",
            ExecutionProviderKind.Cpu,
            optionsFingerprint: "options-a");
        var second = SessionPoolKey.ForSingle(
            "kokoro",
            @"C:\Models\model.onnx",
            ExecutionProviderKind.Cpu,
            optionsFingerprint: "options-b");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ForEncoder_SetsGraphRoleToEncoder()
    {
        var key = SessionPoolKey.ForEncoder("whisper-onnx", @"C:\Models\encoder.onnx", ExecutionProviderKind.Cpu);

        Assert.Equal("encoder", key.GraphRole);
    }

    [Fact]
    public void ForDecoder_SetsGraphRoleToDecoder()
    {
        var key = SessionPoolKey.ForDecoder("whisper-onnx", @"C:\Models\decoder.onnx", ExecutionProviderKind.Cpu);

        Assert.Equal("decoder", key.GraphRole);
    }

    [Fact]
    public void ForEncoder_AndForDecoder_DifferentPaths_ProduceDifferentKeys()
    {
        var enc = SessionPoolKey.ForEncoder("whisper-onnx", @"C:\Models\encoder.onnx", ExecutionProviderKind.Cpu);
        var dec = SessionPoolKey.ForDecoder("whisper-onnx", @"C:\Models\decoder.onnx", ExecutionProviderKind.Cpu);

        // Different paths produce different path hashes even if graph roles were the same.
        Assert.NotEqual(enc, dec);
    }

    [Fact]
    public void ForEncoder_AndForDecoder_SamePath_ProduceDifferentKeys()
    {
        // If (hypothetically) encoder and decoder shared a path, the graph role must still
        // differentiate the key so they do not share the same pool slot.
        const string path = @"C:\Models\shared.onnx";
        var enc = SessionPoolKey.ForEncoder("whisper-onnx", path, ExecutionProviderKind.Cpu);
        var dec = SessionPoolKey.ForDecoder("whisper-onnx", path, ExecutionProviderKind.Cpu);

        Assert.NotEqual(enc, dec);
    }

    // ── Chatterbox four-graph helpers ─────────────────────────────────────────

    [Fact]
    public void ForChatterboxSpeechEncoder_SetsCorrectRoleAndFamily()
    {
        var key = SessionPoolKey.ForChatterboxSpeechEncoder(@"C:\Models\speech_encoder.onnx", ExecutionProviderKind.Cpu);

        Assert.Equal("speech-encoder", key.GraphRole);
        Assert.Equal("chatterbox", key.EngineFamily);
    }

    [Fact]
    public void ForChatterboxEmbedTokens_SetsCorrectRole()
    {
        var key = SessionPoolKey.ForChatterboxEmbedTokens(@"C:\Models\embed_tokens.onnx", ExecutionProviderKind.Cpu);

        Assert.Equal("embed-tokens", key.GraphRole);
    }

    [Fact]
    public void ForChatterboxLanguageModel_SetsCorrectRole()
    {
        var key = SessionPoolKey.ForChatterboxLanguageModel(@"C:\Models\lm.onnx", ExecutionProviderKind.Cpu);

        Assert.Equal("lm", key.GraphRole);
    }

    [Fact]
    public void ForChatterboxConditionalDecoder_SetsCorrectRole()
    {
        var key = SessionPoolKey.ForChatterboxConditionalDecoder(@"C:\Models\decoder.onnx", ExecutionProviderKind.Cpu);

        Assert.Equal("conditional-decoder", key.GraphRole);
    }

    [Fact]
    public void ChatterboxFourGraphRoles_AllProduceDifferentKeys_WhenSamePath()
    {
        // Chatterbox may share path roots; graph role must uniquely identify each session slot.
        const string path = @"C:\Models\chatterbox.onnx";
        var speechEnc = SessionPoolKey.ForChatterboxSpeechEncoder(path, ExecutionProviderKind.Cpu);
        var embedTok = SessionPoolKey.ForChatterboxEmbedTokens(path, ExecutionProviderKind.Cpu);
        var lm = SessionPoolKey.ForChatterboxLanguageModel(path, ExecutionProviderKind.Cpu);
        var condDec = SessionPoolKey.ForChatterboxConditionalDecoder(path, ExecutionProviderKind.Cpu);

        var allFour = new[] { speechEnc, embedTok, lm, condDec };

        // All four keys must be distinct.
        Assert.Equal(4, allFour.Distinct().Count());
    }

    [Fact]
    public void ChatterboxFourGraphRoles_AllHaveChatterboxEngineFamily()
    {
        const string path = @"C:\Models\chatterbox.onnx";
        var keys = new[]
        {
            SessionPoolKey.ForChatterboxSpeechEncoder(path, ExecutionProviderKind.Cpu),
            SessionPoolKey.ForChatterboxEmbedTokens(path, ExecutionProviderKind.Cpu),
            SessionPoolKey.ForChatterboxLanguageModel(path, ExecutionProviderKind.Cpu),
            SessionPoolKey.ForChatterboxConditionalDecoder(path, ExecutionProviderKind.Cpu),
        };

        Assert.All(keys, k => Assert.Equal("chatterbox", k.EngineFamily));
    }
}
