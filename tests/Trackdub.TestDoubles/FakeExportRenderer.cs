using Trackdub.Contracts;

namespace Trackdub.TestDoubles;

public sealed class FakeExportRenderer : IExportRenderer
{
    private readonly List<ExportPlan> calls = [];

    public IReadOnlyList<ExportPlan> Calls => calls;

    public byte[] OutputBytes { get; set; } = [0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109];

    public Task<ExportRenderResult> RenderAsync(
        ExportPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.OutputPath);
        cancellationToken.ThrowIfCancellationRequested();

        calls.Add(plan);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(plan.OutputPath))!);
        File.WriteAllBytes(plan.OutputPath, OutputBytes);
        return Task.FromResult(new ExportRenderResult(plan.OutputPath, Warnings: []));
    }
}
