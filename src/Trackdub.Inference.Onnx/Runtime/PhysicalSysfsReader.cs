using System.Runtime.Versioning;

namespace Trackdub.Inference.Onnx.Runtime;

[SupportedOSPlatform("linux")]
public sealed class PhysicalSysfsReader : ISysfsReader
{
    public IEnumerable<string> EnumerateDirectories(string path) =>
        Directory.Exists(path) ? Directory.EnumerateDirectories(path) : [];

    public bool FileExists(string path) => File.Exists(path);

    public string? ReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);
}
