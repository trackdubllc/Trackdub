using System.Runtime.Versioning;

namespace Trackdub.Inference.Onnx.Runtime;

[SupportedOSPlatform("linux")]
public interface ISysfsReader
{
    IEnumerable<string> EnumerateDirectories(string path);
    bool FileExists(string path);
    string? ReadAllText(string path);
    bool DirectoryExists(string path);
}
