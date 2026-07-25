using Trackdub.Domain;
using System.Text.Json.Serialization;

namespace Trackdub.Inference.Runtime.Planning;

public sealed record StageRuntimePlan
{
    public RuntimeStage Stage { get; init; }

    public StageRuntimePlanStatus Status { get; init; }

    public string? ModelId { get; init; }

    public string? ModelAlias { get; init; }

    public string? EngineFamily { get; init; }

    public string? ModelTier { get; init; }

    public string? Variant { get; init; }

    public ExecutionProviderKind? ExecutionProvider { get; init; }

    public RuntimeModelIntegrityStatus ModelIntegrityStatus { get; init; }

    [JsonIgnore]
    public string? ModelEntryPath { get; init; }

    [JsonIgnore]
    public string? ModelRootPath { get; init; }

    [JsonIgnore]
    public string? ModelEntryRelativePath { get; init; }

    [JsonIgnore]
    public IReadOnlyList<string> RequiredModelRelativePaths { get; init; } = [];

    [JsonIgnore]
    public bool IsLocalOptimizedVariant { get; init; }

    public int? DeviceIndex { get; init; }

    public string? DeviceAdapterDescription { get; init; }

    public RuntimePlanFallback? Fallback { get; init; }

    public IReadOnlyList<RuntimePlanWarning> Warnings { get; init; } = [];
}

public sealed record RuntimePlanFallback(
    RuntimePlanFallbackCode Code,
    string? Detail = null);

public sealed record RuntimePlanWarning(
    RuntimePlanWarningCode Code,
    string? Detail = null);

public sealed record HardwareProfile(
    string OperatingSystem,
    string Architecture,
    bool HasGpu,
    string? GpuDescription = null,
    IReadOnlyList<DeviceEntry>? Devices = null,
    string? CpuName = null,
    long TotalRamMb = 0,
    long DedicatedVramMb = 0,
    NvidiaGpuArchitectureBucket NvidiaGpuArchitecture = NvidiaGpuArchitectureBucket.Unknown);

public sealed record ExecutionProviderAvailability(
    ExecutionProviderKind Provider,
    bool IsAvailable,
    string? Detail = null);

public sealed record ExecutionProviderSmokeTestRequest(
    RuntimeStage Stage,
    string ModelId,
    string ModelAlias,
    string? EngineFamily,
    string Variant,
    ExecutionProviderKind ExecutionProvider,
    string ModelRootPath,
    string EntryPath);

public sealed record ExecutionProviderSmokeTestResult(
    bool Passed,
    string? Detail = null);
