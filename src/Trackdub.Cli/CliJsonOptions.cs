using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trackdub.Cli;

internal static class CliJsonOptions
{
    internal static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
