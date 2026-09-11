using Trackdub.Contracts;

namespace Trackdub.Inference.Onnx.Kokoro;

public sealed class EspeakNgHealthCheck : IEspeakNgHealthCheck
{
    private const string EspeakDataDirectoryName = "espeak-ng-data";
    private const string EspeakDataPathVariableName = "ESPEAK_DATA_PATH";

    public EspeakNgHealthStatus CheckAvailability()
    {
        string path;
        try
        {
            path = EspeakNgPathResolver.Resolve();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new EspeakNgHealthStatus(false, null, ex.Message);
        }

        if (!File.Exists(path))
        {
            return new EspeakNgHealthStatus(
                false,
                path,
                $"Resolved eSpeak-NG path does not exist: {path}");
        }

        if (!HasPhonemeData(path))
        {
            string exeName = Path.GetFileName(path);
            return new EspeakNgHealthStatus(
                false,
                path,
                $"Found {exeName} at {path} but {EspeakDataDirectoryName} is missing. Place the data folder next to the executable or set {EspeakDataPathVariableName}.");
        }

        return new EspeakNgHealthStatus(true, path, null);
    }

    private static bool HasPhonemeData(string executablePath)
    {
        string? dataPath = Environment.GetEnvironmentVariable(EspeakDataPathVariableName);
        if (!string.IsNullOrWhiteSpace(dataPath) && Directory.Exists(dataPath))
        {
            return true;
        }

        if (Path.IsPathRooted(EspeakDataDirectoryName))
            throw new InvalidOperationException($"{nameof(EspeakDataDirectoryName)} must be a relative path, but got: {EspeakDataDirectoryName}");

        string? executableDirectory = Path.GetDirectoryName(executablePath);
        return !string.IsNullOrWhiteSpace(executableDirectory) &&
               Directory.Exists(Path.Combine(executableDirectory, EspeakDataDirectoryName));
    }
}
