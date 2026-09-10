using System.Text.Json;
using System.Text.Json.Serialization;

using Trackdub.Contracts;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

/// <summary>
/// Enumerates execution-provider kinds with canonical tags, aliases, availability, and remediation.
/// Does not install anything except documenting the existing TRT-RTX installer path.
/// </summary>
internal static class ProvidersListHandler
{
    public static async Task<int> ListAsync(
        TrackdubSessionFactory factory,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        IHardwareProfileProvider hardwareProfileProvider =
            factory.GetRequiredService<IHardwareProfileProvider>();
        IExecutionProviderDiscovery discovery =
            factory.GetRequiredService<IExecutionProviderDiscovery>();

        HardwareProfile hardwareProfile = await hardwareProfileProvider
            .GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ExecutionProviderAvailability> availabilities = await discovery
            .DiscoverAsync(hardwareProfile, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<ExecutionProviderKind, string> remediations = await BuildRemediationsAsync(
                factory,
                cancellationToken)
            .ConfigureAwait(false);

        var providers = new List<ProviderListRow>();
        foreach (ExecutionProviderKind kind in Enum.GetValues<ExecutionProviderKind>())
        {
            if (kind == default)
            {
                continue;
            }

            ExecutionProviderAvailability? availability = availabilities
                .FirstOrDefault(candidate => candidate.Provider == kind);

            bool isAvailable = kind is ExecutionProviderKind.Cpu
                || (availability?.IsAvailable ?? false);

            providers.Add(new ProviderListRow
            {
                Kind = kind.ToString(),
                CanonicalTag = ExecutionProviderTokens.ToCanonicalTag(kind),
                Aliases = ExecutionProviderTokens.GetAliases(kind).ToArray(),
                Available = isAvailable,
                Detail = availability?.Detail,
                Remediation = remediations.GetValueOrDefault(kind)
                    ?? DefaultRemediation(kind, isAvailable),
                Installer = kind is ExecutionProviderKind.TensorRTRtx
                    ? "trackdub providers trt-rtx install --accept-license"
                    : null,
            });
        }

        var payload = new ProvidersListOutput
        {
            Providers = providers
                .OrderBy(row => row.CanonicalTag, StringComparer.Ordinal)
                .ToArray(),
        };

        string json = JsonSerializer.Serialize(payload, CliJsonOptions.Default);
        await output.WriteLineAsync(json).ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    private static async Task<Dictionary<ExecutionProviderKind, string>> BuildRemediationsAsync(
        TrackdubSessionFactory factory,
        CancellationToken cancellationToken)
    {
        var remediations = new Dictionary<ExecutionProviderKind, string>();

        ITensorRtRtxRuntimeReadinessService trt =
            factory.GetRequiredService<ITensorRtRtxRuntimeReadinessService>();
        TensorRtRtxRuntimeReadinessSnapshot trtSnapshot = await trt
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        if (!trtSnapshot.IsReady)
        {
            remediations[ExecutionProviderKind.TensorRTRtx] =
                trtSnapshot.InstallHint
                ?? "Run trackdub providers trt-rtx status, then trackdub providers trt-rtx install --accept-license.";
        }

        IMigraphxRuntimeReadinessService migraphx =
            factory.GetRequiredService<IMigraphxRuntimeReadinessService>();
        MigraphxRuntimeReadinessSnapshot migraphxSnapshot = await migraphx
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        if (!migraphxSnapshot.IsReady)
        {
            remediations[ExecutionProviderKind.Migraphx] =
                migraphxSnapshot.InstallHint
                ?? "Install the Windows ML MIGraphX catalog package or a ROCm ONNX Runtime build on Linux.";
        }

        IOpenVinoCatalogRuntimeReadinessService openVinoCatalog =
            factory.GetRequiredService<IOpenVinoCatalogRuntimeReadinessService>();
        WinMlCatalogRuntimeReadinessSnapshot openVinoSnapshot = await openVinoCatalog
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        if (!openVinoSnapshot.IsReady)
        {
            remediations[ExecutionProviderKind.OpenVinoCatalog] =
                openVinoSnapshot.InstallHint
                ?? "Accept the Intel OpenVINO license in settings and install the Windows ML OpenVINO catalog EP.";
        }

        IQnnCatalogRuntimeReadinessService qnn =
            factory.GetRequiredService<IQnnCatalogRuntimeReadinessService>();
        WinMlCatalogRuntimeReadinessSnapshot qnnSnapshot = await qnn
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        if (!qnnSnapshot.IsReady)
        {
            remediations[ExecutionProviderKind.Qnn] =
                qnnSnapshot.InstallHint
                ?? "Accept the Qualcomm QNN license in settings and install the Windows ML QNN catalog EP.";
        }

        IVitisAiCatalogRuntimeReadinessService vitisAi =
            factory.GetRequiredService<IVitisAiCatalogRuntimeReadinessService>();
        WinMlCatalogRuntimeReadinessSnapshot vitisSnapshot = await vitisAi
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        if (!vitisSnapshot.IsReady)
        {
            remediations[ExecutionProviderKind.VitisAi] =
                vitisSnapshot.InstallHint
                ?? "Accept the AMD Ryzen AI license in settings and install the Windows ML VitisAI catalog EP.";
        }

        return remediations;
    }

    private static string DefaultRemediation(ExecutionProviderKind kind, bool isAvailable)
    {
        if (isAvailable)
        {
            return $"Ready. Pin with --execution-provider {ExecutionProviderTokens.ToCanonicalTag(kind)}.";
        }

        return kind switch
        {
            ExecutionProviderKind.CoreMl => "CoreML requires macOS.",
            ExecutionProviderKind.DirectMl => "DirectML requires Windows with a compatible GPU and Windows ML.",
            ExecutionProviderKind.Cuda => "Native CUDA requires a compatible NVIDIA ORT build (Linux; advanced Windows).",
            ExecutionProviderKind.TensorRt => "Native TensorRT requires libnvinfer and a TensorRT-capable ORT build.",
            ExecutionProviderKind.OpenVino => "Standalone OpenVINO requires the OpenVINO runtime component.",
            ExecutionProviderKind.Dnnl => "oneDNN / DNNL requires the onnxruntime-dnnl package on this host.",
            _ => $"Provider {ExecutionProviderTokens.ToCanonicalTag(kind)} is not available on this machine.",
        };
    }

    private sealed class ProvidersListOutput
    {
        [JsonPropertyName("providers")]
        public required ProviderListRow[] Providers { get; init; }
    }

    private sealed class ProviderListRow
    {
        [JsonPropertyName("kind")]
        public required string Kind { get; init; }

        [JsonPropertyName("canonicalTag")]
        public required string CanonicalTag { get; init; }

        [JsonPropertyName("aliases")]
        public required string[] Aliases { get; init; }

        [JsonPropertyName("available")]
        public required bool Available { get; init; }

        [JsonPropertyName("detail")]
        public string? Detail { get; init; }

        [JsonPropertyName("remediation")]
        public required string Remediation { get; init; }

        [JsonPropertyName("installer")]
        public string? Installer { get; init; }
    }
}
