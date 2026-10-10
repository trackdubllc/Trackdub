using System.Reflection;
using System.Reflection.Emit;
using Trackdub.Inference.Onnx.Worker;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class InferenceWorkerBuildStampTests
{
    [Fact]
    public void Build_stamp_uses_the_stamped_source_revision_when_present()
    {
        AssemblyBuilder assembly = CreateAssembly(revision: "0123456789abcdef");

        Assert.Equal("revision:0123456789abcdef", InferenceWorkerProtocol.ComputeBuildStamp(assembly));
    }

    [Fact]
    public void Build_stamp_falls_back_to_the_shared_assembly_version_without_a_revision()
    {
        AssemblyBuilder first = CreateAssembly(revision: null);
        AssemblyBuilder second = CreateAssembly(revision: null);

        // Separate compilations differ in module ID but must still pair.
        Assert.NotEqual(first.ManifestModule.ModuleVersionId, second.ManifestModule.ModuleVersionId);
        Assert.Equal("unrevised:1.2.3.4", InferenceWorkerProtocol.ComputeBuildStamp(first));
        Assert.Equal(InferenceWorkerProtocol.ComputeBuildStamp(first), InferenceWorkerProtocol.ComputeBuildStamp(second));
    }

    [Fact]
    public void Build_stamp_ignores_informational_version_metadata()
    {
        AssemblyBuilder assembly = CreateAssembly(revision: null, informationalVersion: "1.2.3+local");

        Assert.Equal("unrevised:1.2.3.4", InferenceWorkerProtocol.ComputeBuildStamp(assembly));
    }

    [Fact]
    public void Current_build_stamp_matches_this_assemblys_stamped_revision()
    {
        string? revision = typeof(InferenceWorkerProtocol).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == InferenceWorkerProtocol.SourceRevisionMetadataKey)?.Value;

        Assert.Equal(
            string.IsNullOrWhiteSpace(revision)
                ? "unrevised:" + typeof(InferenceWorkerProtocol).Assembly.GetName().Version
                : "revision:" + revision,
            InferenceWorkerProtocol.BuildStamp);
    }

    private static AssemblyBuilder CreateAssembly(string? revision, string? informationalVersion = null)
    {
        var attributes = new List<CustomAttributeBuilder>();
        if (revision is not null)
        {
            attributes.Add(new CustomAttributeBuilder(
                typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!,
                [InferenceWorkerProtocol.SourceRevisionMetadataKey, revision]));
        }

        if (informationalVersion is not null)
        {
            attributes.Add(new CustomAttributeBuilder(
                typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
                [informationalVersion]));
        }

        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("StampProbe") { Version = new Version(1, 2, 3, 4) },
            AssemblyBuilderAccess.Run,
            attributes);
        assembly.DefineDynamicModule("StampProbe");
        return assembly;
    }
}
