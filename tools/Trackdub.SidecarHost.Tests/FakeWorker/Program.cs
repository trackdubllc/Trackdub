// Scripted protocol-v1 worker for host tests: health -> alive; load -> loaded;
// infer -> ok with a 1-sample silent int16 "audio" tensor. Refuses unknown ops.
// Env var FAKE_PROTOCOL_VERSION overrides the echoed protocolVersion (for the
// host's version-stamp refusal test); defaults to 1.
using System.Text.Json;

int protocolVersion = int.TryParse(Environment.GetEnvironmentVariable("FAKE_PROTOCOL_VERSION"), out int v) ? v : 1;

while (true)
{
    string? line = Console.ReadLine();
    if (line is null) return;
    JsonDocument doc;
    try
    {
        doc = JsonDocument.Parse(line);
    }
    catch
    {
        Console.WriteLine(JsonSerializer.Serialize(new { id = (string?)null, status = "error", reason = "invalid-json", protocolVersion }));
        continue;
    }

    string? id = doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
    string op = doc.RootElement.TryGetProperty("op", out var opEl) && opEl.ValueKind == JsonValueKind.String ? opEl.GetString()! : "";
    switch (op)
    {
        case "health":
            Console.WriteLine(JsonSerializer.Serialize(new { id, status = "alive", modelLoaded = false, activeProvider = "none", protocolVersion }));
            break;
        case "load":
            Console.WriteLine(JsonSerializer.Serialize(new { id, status = "loaded", activeProvider = "cpu", model = "fake", protocolVersion }));
            break;
        case "infer":
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                id,
                status = "ok",
                outputs = new { audio = new { dtype = "int16", shape = new long[] { 1 }, data = Convert.ToBase64String(new byte[2]) } },
                sampleRate = 24000,
                protocolVersion,
            }));
            break;
        default:
            Console.WriteLine(JsonSerializer.Serialize(new { id, status = "error", reason = "unknown-op", protocolVersion }));
            break;
    }
}
