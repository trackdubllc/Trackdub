using System.Text.Json;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Pipeline;
using Xunit;

namespace Trackdub.Application.Tests.Pipeline;

/// <summary>
/// Record shape + JSON round-trip coverage for <see cref="PipelineTransientFault"/>.
/// Mirrors spec §11.3. Lives under Application.Tests because the project already
/// references Trackdub.Contracts + Trackdub.Domain; creating a parallel test
/// project for two tests would violate AGENTS.md "no upward deps" discipline.
/// </summary>
public sealed class PipelineTransientFaultTests
{
    [Fact]
    public void Constructor_rejects_null_or_whitespace_stage_name()
    {
        Assert.Throws<ArgumentNullException>(() => new PipelineTransientFault(
            Guid.NewGuid(),
            stageName: null!,
            TransientFailureKind.Unknown,
            "detail",
            DateTimeOffset.UtcNow,
            attemptNumber: 1));

        Assert.Throws<ArgumentException>(() => new PipelineTransientFault(
            Guid.NewGuid(),
            stageName: "   ",
            TransientFailureKind.Unknown,
            "detail",
            DateTimeOffset.UtcNow,
            attemptNumber: 1));
    }

    [Fact]
    public void Constructor_rejects_negative_attempt_number()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineTransientFault(
            Guid.NewGuid(),
            "Asr",
            TransientFailureKind.SqliteBusy,
            "detail",
            DateTimeOffset.UtcNow,
            attemptNumber: -1));
    }

    [Fact]
    public void Detail_normalizes_null_to_empty_string()
    {
        PipelineTransientFault fault = new(
            Guid.NewGuid(),
            "Export",
            TransientFailureKind.DirectoryLock,
            detail: null!,
            DateTimeOffset.UtcNow,
            attemptNumber: 0);

        Assert.Equal(string.Empty, fault.Detail);
    }

    [Fact]
    public void Context_is_preserved_when_provided()
    {
        var ctx = new Dictionary<string, string>
        {
            ["Path"] = "C:/projects/demo/audio.wav",
            ["HResult"] = "0x80070020",
        };

        PipelineTransientFault fault = new(
            Guid.NewGuid(),
            "Vad",
            TransientFailureKind.DirectoryLock,
            "shared access",
            DateTimeOffset.UtcNow,
            attemptNumber: 2,
            context: ctx);

        Assert.NotNull(fault.Context);
        Assert.Equal("C:/projects/demo/audio.wav", fault.Context!["Path"]);
        Assert.Equal("0x80070020", fault.Context["HResult"]);
    }

    [Fact]
    public void Json_round_trip_preserves_all_public_fields()
    {
        DateTimeOffset happenedAt = new(2026, 7, 22, 12, 34, 56, TimeSpan.Zero);
        Guid projectId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var ctx = new Dictionary<string, string>
        {
            ["ExitCode"] = "137",
        };

        PipelineTransientFault source = new(
            projectId,
            "Tts",
            TransientFailureKind.MemoryExhausted,
            "ort out of memory",
            happenedAt,
            attemptNumber: 3,
            context: ctx);

        string json = JsonSerializer.Serialize(source);
        PipelineTransientFault? roundTripped = JsonSerializer.Deserialize<PipelineTransientFault>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(source.ProjectId, roundTripped!.ProjectId);
        Assert.Equal(source.StageName, roundTripped.StageName);
        Assert.Equal(source.Kind, roundTripped.Kind);
        Assert.Equal(source.Detail, roundTripped.Detail);
        Assert.Equal(source.HappenedAt, roundTripped.HappenedAt);
        Assert.Equal(source.AttemptNumber, roundTripped.AttemptNumber);
        Assert.NotNull(roundTripped.Context);
        Assert.Equal("137", roundTripped.Context!["ExitCode"]);
    }

    [Fact]
    public void Json_round_trip_with_null_context_yields_null_context()
    {
        PipelineTransientFault source = new(
            Guid.NewGuid(),
            "Export",
            TransientFailureKind.UserCancellation,
            "cancelled",
            DateTimeOffset.UtcNow,
            attemptNumber: 0);

        string json = JsonSerializer.Serialize(source);
        PipelineTransientFault? roundTripped = JsonSerializer.Deserialize<PipelineTransientFault>(json);

        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped!.Context);
    }
}
