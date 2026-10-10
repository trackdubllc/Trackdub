using System.Reflection;
using Microsoft.ML.OnnxRuntimeGenAI;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: Trackdub.GenAiProbe <GenAI model directory>");
    return 2;
}
try
{
    var assembly = Assembly.Load("Trackdub.Inference.Onnx");
    var guard = assembly.GetType("Trackdub.Inference.Onnx.Runtime.GenAiNativeCompatibility", throwOnError: true)!;
    guard.GetMethod("EnsureCompatible", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
    var selection = assembly.GetType("Trackdub.Inference.Onnx.Runtime.GenAiNativeRuntimeSelection", throwOnError: true)!;
    Console.WriteLine(selection.GetMethod("Describe", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null));
    Console.Out.Flush();
    using var oga = new OgaHandle();
    using var config = new Config(Path.GetFullPath(args[0]));
    config.ClearProviders();
    using var model = new Model(config);
    Console.WriteLine("GenAI CPU model construction succeeded.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine((ex is TargetInvocationException ? ex.InnerException ?? ex : ex).ToString());
    return 1;
}
