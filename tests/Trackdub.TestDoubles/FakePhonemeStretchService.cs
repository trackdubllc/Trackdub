using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakePhonemeStretchService : IPhonemeStretchService
{
    public bool ReturnNull { get; set; }
    public TimeSpan AlignedDurationToReturn { get; set; } = TimeSpan.FromSeconds(1.0);
    public bool ThrowOnStretch { get; set; }
    public int CallCount { get; private set; }
    public string? LastInputPath { get; private set; }
    public string? LastOutputPath { get; private set; }

    public Task<TimeSpan?> StretchAsync(
        string inputPath,
        string outputPath,
        IReadOnlyList<PhonemeStretchPlan> plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastInputPath = inputPath;
        LastOutputPath = outputPath;
        CallCount++;

        if (ThrowOnStretch)
            throw new InvalidOperationException("FakePhonemeStretchService: simulated failure.");

        if (ReturnNull)
            return Task.FromResult<TimeSpan?>(null);

        // Simulate output file creation so artifact commit can proceed.
        var dir = Path.GetDirectoryName(outputPath);
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllBytes(outputPath, []);

        return Task.FromResult<TimeSpan?>(AlignedDurationToReturn);
    }
}
