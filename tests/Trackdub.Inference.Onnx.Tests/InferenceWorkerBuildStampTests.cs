using System.Reflection;
using System.Reflection.Emit;
using Trackdub.Inference.Onnx.Worker;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class InferenceWorkerBuildStampTests
{
    [Fact]
    public void Build_stamp_uses_the_source_revision_shared_by_both_compilations()
    {
        string? informational = typeof(InferenceWorkerProtocol).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.NotNull(informational);
        Assert.Contains('+', informational);
        Assert.Equal(informational, InferenceWorkerProtocol.BuildStamp);
    }

    [Fact]
    public void Build_stamp_falls_back_to_the_module_id_without_a_source_revision()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("StampProbe"),
            AssemblyBuilderAccess.Run);
        assembly.DefineDynamicModule("StampProbe");

        Assert.Equal(
            assembly.ManifestModule.ModuleVersionId.ToString("N"),
            InferenceWorkerProtocol.ComputeBuildStamp(assembly));
    }
}
