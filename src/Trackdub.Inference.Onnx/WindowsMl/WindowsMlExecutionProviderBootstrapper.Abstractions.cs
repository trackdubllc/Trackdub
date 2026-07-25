namespace Trackdub.Inference.Onnx.WindowsMl;

public enum WindowsMlBootstrapMode
{
    RegisterInstalledCertified,
    EnsureAndRegisterCertified,
    EnsureAllCertifiedCatalog
}

public sealed record WindowsMlBootstrapResult(
    WindowsMlBootstrapMode Mode,
    bool Succeeded,
    string? FailureReason);
