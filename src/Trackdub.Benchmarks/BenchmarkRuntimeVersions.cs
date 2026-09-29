namespace Trackdub.Benchmarks;

/// <summary>
/// Captures runtime version evidence shared by the dubbing benchmark runners.
/// </summary>
internal static class BenchmarkRuntimeVersions
{
    public static Dictionary<string, string> Capture()
    {
        var runtimeVersions = new Dictionary<string, string>
        {
            ["dotnet"] = Environment.Version.ToString(),
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        };
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? name = assembly.GetName().Name;
            if (name is "Microsoft.ML.OnnxRuntime" or "Microsoft.ML.OnnxRuntimeGenAI" or
                "Microsoft.ML.OnnxRuntimeGenAI.Managed")
                runtimeVersions[name] = assembly.GetName().Version?.ToString() ?? "unknown";
        }
        return runtimeVersions;
    }
}
