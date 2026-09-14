namespace Trackdub.Contracts;

public sealed record EspeakNgHealthStatus(
    bool Available,
    string? ExecutablePath,
    string? ErrorMessage);

public interface IEspeakNgHealthCheck
{
    EspeakNgHealthStatus CheckAvailability();
}
