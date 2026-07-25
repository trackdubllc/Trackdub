using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Onnx.Dnnl;

public sealed class DnnlReadinessProbe : IDnnlReadinessProbe
{
    public Task<DnnlReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default)
    {
        _ = allowProviderDownloads;
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSupportedRid(out string ridDetail))
        {
            return Task.FromResult(new DnnlReadinessReport(
                DnnlProviderIds.NativeOrt,
                DnnlReadinessBlocker.UnsupportedRid,
                IsSupportedRid: false,
                IsOrtProviderListed: false,
                CanAppendSessionOptions: false,
                SmokeTestPassed: false,
                Detail: ridDetail));
        }

        bool providerListed = DnnlOrtProbe.IsProviderListed();
        if (!providerListed)
        {
            return Task.FromResult(new DnnlReadinessReport(
                DnnlProviderIds.NativeOrt,
                DnnlReadinessBlocker.OrtProviderUnavailable,
                IsSupportedRid: true,
                IsOrtProviderListed: false,
                CanAppendSessionOptions: false,
                SmokeTestPassed: false,
                Detail: "DnnlExecutionProvider is not listed by loaded ONNX Runtime. Use Trackdub.OnnxRuntime.Dnnl.Native assets for this runtime flavor."));
        }

        using var options = new SessionOptions();
        if (!DnnlSessionOptionsExtensions.TryAppendDnnlProvider(options, out string? appendFailure))
        {
            return Task.FromResult(new DnnlReadinessReport(
                DnnlProviderIds.NativeOrt,
                DnnlReadinessBlocker.AppendFailed,
                IsSupportedRid: true,
                IsOrtProviderListed: true,
                CanAppendSessionOptions: false,
                SmokeTestPassed: false,
                Detail: $"AppendExecutionProvider_Dnnl failed: {appendFailure ?? "unknown failure"}"));
        }

        DnnlSmokeResult smoke = RunSmokeSession(options);
        return Task.FromResult(new DnnlReadinessReport(
            DnnlProviderIds.NativeOrt,
            smoke.Blocker,
            IsSupportedRid: true,
            IsOrtProviderListed: true,
            CanAppendSessionOptions: true,
            SmokeTestPassed: smoke.Succeeded,
            Detail: smoke.Detail));
    }

    private static DnnlSmokeResult RunSmokeSession(SessionOptions options)
    {
        try
        {
            using var session = new InferenceSession(BuildMatMulOnnxModel(), options);
            IReadOnlyList<OrtEpDevice> devices = session.GetEpDeviceForInputs();
            if (devices.Any(device => DnnlOrtProbe.IsDnnlExecutionProviderName(device.EpName)))
            {
                return new DnnlSmokeResult(
                    true,
                    DnnlReadinessBlocker.None,
                    "DnnlExecutionProvider appended and selected by MatMul-model smoke session.");
            }

            string listedProviders = devices.Count == 0
                ? "none"
                : string.Join(", ", devices.Select(device => device.EpName));
            return new DnnlSmokeResult(
                false,
                DnnlReadinessBlocker.SmokeSessionProviderMismatch,
                $"DNNL smoke session loaded but selected provider was not DNNL. Effective providers: {listedProviders}.");
        }
        catch (Exception ex) when (ex is OnnxRuntimeException
            or InvalidOperationException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or FileLoadException)
        {
            return new DnnlSmokeResult(
                false,
                DnnlReadinessBlocker.SmokeSessionFailed,
                $"DNNL smoke session failed: {ex.Message}");
        }
    }

    private static bool IsSupportedRid(out string detail)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            detail = $"DNNL native runtime initially supports x64 only; current process architecture is {RuntimeInformation.ProcessArchitecture}.";
            return false;
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            detail = "DNNL native runtime RID is supported.";
            return true;
        }

        detail = $"DNNL native runtime is not packaged for {RuntimeInformation.OSDescription}.";
        return false;
    }

    private static byte[] BuildMatMulOnnxModel() =>
    [
        0x08, 0x07, 0x3A, 0x54, 0x0A, 0x11, 0x0A, 0x01, 0x78, 0x0A, 0x01, 0x77, 0x12, 0x01,
        0x79, 0x22, 0x06, 0x4D, 0x61, 0x74, 0x4D, 0x75, 0x6C, 0x12, 0x04, 0x74, 0x65, 0x73,
        0x74, 0x2A, 0x13, 0x08, 0x02, 0x08, 0x01, 0x10, 0x01, 0x42, 0x01, 0x77, 0x4A, 0x08,
        0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x80, 0x3F, 0x5A, 0x11, 0x0A, 0x01, 0x78, 0x12,
        0x0C, 0x08, 0x01, 0x12, 0x08, 0x0A, 0x02, 0x08, 0x01, 0x0A, 0x02, 0x08, 0x02, 0x62,
        0x11, 0x0A, 0x01, 0x79, 0x12, 0x0C, 0x08, 0x01, 0x12, 0x08, 0x0A, 0x02, 0x08, 0x01,
        0x0A, 0x02, 0x08, 0x01, 0x42, 0x04, 0x0A, 0x00, 0x10, 0x09,
    ];

    private sealed record DnnlSmokeResult(
        bool Succeeded,
        DnnlReadinessBlocker Blocker,
        string Detail);
}
