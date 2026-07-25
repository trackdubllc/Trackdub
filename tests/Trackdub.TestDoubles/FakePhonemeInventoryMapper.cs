using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakePhonemeInventoryMapper : IPhonemeInventoryMapper
{
    public bool ReturnNullForAllMappings { get; set; }

    /// <summary>By default returns the raw symbol unchanged (identity mapping).</summary>
    public string? MapSymbol(string rawSymbol, string sourceInventory, string targetInventory)
        => ReturnNullForAllMappings ? null : rawSymbol;
}
