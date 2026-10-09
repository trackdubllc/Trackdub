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

    [Fact]
    public void RecordEquality_DifferentEstimatedVramMb_AreEqual()
    {
        // Admission sizing is not identity: a re-measured sidecar must reuse the pooled session.
        var a = new SessionPoolKey("madlad", null, null, ExecutionProviderKind.TensorRTRtx, "abc", 0, "encoder") { EstimatedVramMb = 3314 };
        var b = a with { EstimatedVramMb = 5226 };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void RecordEquality_DifferentOpenVinoCpuProxy_NotEqual()
    {
        var a = new SessionPoolKey("kokoro", null, null, ExecutionProviderKind.OpenVino, "abc", 0, "default");
        var b = a with { UseOpenVinoCpuProxy = true };

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

    // ── Content identity ────────────────────────────────────────────────────

    [Fact]
    public void ModelContentHash_UnchangedFile_IsCachedAndKeysAreEqual()
    {
        string path = Path.Join(Path.GetTempPath(), $"spk-{Guid.NewGuid():N}.onnx");
        try
        {
            File.WriteAllBytes(path, [0x01, 0x02, 0x03, 0x04]);

            string? first = SessionPoolKey.HashModelContent(path);
            string? second = SessionPoolKey.HashModelContent(path);

            Assert.NotNull(first);
            Assert.Equal(first, second);

            var key1 = SessionPoolKey.ForSingle("eng", path, ExecutionProviderKind.Cpu);
            var key2 = SessionPoolKey.ForSingle("eng", path, ExecutionProviderKind.Cpu);
            Assert.Equal(first, key1.ModelContentHash);
            Assert.Equal(key1, key2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ModelContentHash_SamePathReplacement_ChangesHashAndKey()
    {
        string path = Path.Join(Path.GetTempPath(), $"spk-{Guid.NewGuid():N}.onnx");
        try
        {
            File.WriteAllBytes(path, [0x01, 0x02, 0x03, 0x04]);
            var before = SessionPoolKey.ForSingle("eng", path, ExecutionProviderKind.Cpu);

            // Same path, different content and length → the helper-built key must change.
            File.WriteAllBytes(path, [0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F]);
            var after = SessionPoolKey.ForSingle("eng", path, ExecutionProviderKind.Cpu);

            Assert.NotNull(after.ModelContentHash);
            Assert.NotEqual(before.ModelContentHash, after.ModelContentHash);
            Assert.Equal(before.PathHash, after.PathHash);
            Assert.NotEqual(before, after);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ModelContentHash_ReplacementWithPreservedMetadata_RehashesAfterInvalidation()
    {
        string path = Path.Join(Path.GetTempPath(), $"spk-{Guid.NewGuid():N}.onnx");
        DateTime timestamp = DateTime.UtcNow.AddMinutes(-5);
        try
        {
            File.WriteAllBytes(path, [0x01, 0x02, 0x03, 0x04]);
            File.SetLastWriteTimeUtc(path, timestamp);
            SessionPoolKey before = await SessionPoolKey.CreateAsync(
                "eng", null, null, ExecutionProviderKind.Cpu, path, null, "default", null, CancellationToken.None);

            File.WriteAllBytes(path, [0x05, 0x06, 0x07, 0x08]);
            File.SetLastWriteTimeUtc(path, timestamp);
            SessionPoolKey staleUntilInvalidated = await SessionPoolKey.CreateAsync(
                "eng", null, null, ExecutionProviderKind.Cpu, path, null, "default", null, CancellationToken.None);
            Assert.Equal(before.ModelContentHash, staleUntilInvalidated.ModelContentHash);

            new SessionPoolModelContentHashCacheInvalidator().Invalidate(path);
            SessionPoolKey after = await SessionPoolKey.CreateAsync(
                "eng", null, null, ExecutionProviderKind.Cpu, path, null, "default", null, CancellationToken.None);

            Assert.NotEqual(before.ModelContentHash, after.ModelContentHash);
            Assert.NotEqual(before, after);
        }
        finally
        {
            File.Delete(path);
            new SessionPoolModelContentHashCacheInvalidator().Invalidate(path);
        }
    }

    [Fact]
    public async Task CreateAsync_ModelContentHash_RespectsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SessionPoolKey.CreateAsync(
            "eng", null, null, ExecutionProviderKind.Cpu, "large-model.onnx", null, "default", null,
            cancellation.Token));
    }

    [Fact]
    public void ModelContentHash_SameBytesDifferentPaths_SameDigestDifferentKeys()
    {
        string pathA = Path.Join(Path.GetTempPath(), $"spk-{Guid.NewGuid():N}a.onnx");
        string pathB = Path.Join(Path.GetTempPath(), $"spk-{Guid.NewGuid():N}b.onnx");
        try
        {
            byte[] bytes = [0x10, 0x20, 0x30];
            File.WriteAllBytes(pathA, bytes);
            File.WriteAllBytes(pathB, bytes);

            var keyA = SessionPoolKey.ForSingle("eng", pathA, ExecutionProviderKind.Cpu);
            var keyB = SessionPoolKey.ForSingle("eng", pathB, ExecutionProviderKind.Cpu);

            Assert.Equal(keyA.ModelContentHash, keyB.ModelContentHash);
            Assert.NotEqual(keyA.PathHash, keyB.PathHash);
            Assert.NotEqual(keyA, keyB);
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    [Fact]
    public void ModelContentHash_MissingFile_NullHashKeepsPathIdentity()
    {
        string path = Path.Join(Path.GetTempPath(), $"spk-missing-{Guid.NewGuid():N}.onnx");

        var key1 = SessionPoolKey.ForSingle("eng", path, ExecutionProviderKind.Cpu);
        var key2 = SessionPoolKey.ForSingle("eng", path, ExecutionProviderKind.Cpu);

        Assert.Null(key1.ModelContentHash);
        Assert.Null(key2.ModelContentHash);
        Assert.Equal(key1, key2); // deterministic path identity still applies
    }

    [Fact]
    public void RecordEquality_ModelContentHash_Discriminates()
    {
        var baseline = new SessionPoolKey(
            "eng", "m", null, ExecutionProviderKind.Cpu, "h", 0, "default");
        var withHash = new SessionPoolKey(
            "eng", "m", null, ExecutionProviderKind.Cpu, "h", 0, "default",
            modelContentHash: "abc123");
        var withSameHash = new SessionPoolKey(
            "eng", "m", null, ExecutionProviderKind.Cpu, "h", 0, "default",
            modelContentHash: "ABC123"); // normalised to lowercase
        var withOtherHash = new SessionPoolKey(
            "eng", "m", null, ExecutionProviderKind.Cpu, "h", 0, "default",
            modelContentHash: "def456");

        Assert.NotEqual(baseline, withHash);
        Assert.Equal(withHash, withSameHash);
        Assert.NotEqual(withHash, withOtherHash);
    }

    [Fact]
    public void StableComparer_DistinguishesContentHash()
    {
        var x = new SessionPoolKey(
            "eng", "m", null, ExecutionProviderKind.Cpu, "h", 0, "default",
            modelContentHash: "aaa");
        var y = new SessionPoolKey(
            "eng", "m", null, ExecutionProviderKind.Cpu, "h", 0, "default",
            modelContentHash: "bbb");

        Assert.NotEqual(0, SessionPoolKey.StableComparer.Compare(x, y));
        Assert.Equal(0, SessionPoolKey.StableComparer.Compare(x, x));
    }
}
