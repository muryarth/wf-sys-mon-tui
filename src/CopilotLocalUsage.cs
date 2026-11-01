using System.Text.Json;

namespace WfSysMon;

// Credits em AI credits (AIC).
record CopilotLocalUsage(long Input, long Output, long CacheRead, long CacheWrite, double Credits)
{
    public static readonly CopilotLocalUsage Zero = new(0, 0, 0, 0, 0);

    public long Total => Input + Output + CacheRead + CacheWrite;

    public CopilotLocalUsage Add(CopilotLocalUsage o) =>
        new(Input + o.Input, Output + o.Output, CacheRead + o.CacheRead, CacheWrite + o.CacheWrite, Credits + o.Credits);
}

// Tokens reais do Copilot, lidos dos eventos "session.usage_record" que o Copilot CLI grava por requisicao
// em ~/.copilot/session-state/<sessao>/events.jsonl. A API do Copilot so informa creditos, e outras
// superficies (como o VS Code) nao gravam esses eventos. O formato nao e documentado e pode mudar.
sealed class CopilotLocalTokens(string? root = null)
{
    const double NanoPerCredit = 1_000_000_000;

    readonly string _root = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");
    readonly JsonlTail _tail = new();
    readonly Dictionary<string, Record> _records = [];

    public CopilotLocalUsage Read(DateTime cycleStartUtc)
    {
        if (Directory.Exists(_root))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var path in Directory.EnumerateFiles(_root, "events.jsonl", options))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) >= cycleStartUtc) _tail.Read(path, ProcessLine);
                }
                catch (IOException) { /* arquivo em uso: tenta de novo na proxima leitura */ }
                catch (UnauthorizedAccessException) { }
            }
        }

        return _records.Values
            .Where(r => r.TimeUtc >= cycleStartUtc)
            .Aggregate(CopilotLocalUsage.Zero, (sum, r) => sum.Add(r.Usage));
    }

    void ProcessLine(ReadOnlyMemory<byte> line)
    {
        if (line.Span.IndexOf("session.usage_record"u8) < 0) return;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "type") != "session.usage_record") return;
            if (Str(root, "id") is not { } id || !root.TryGetProperty("timestamp", out var ts) || !ts.TryGetDateTimeOffset(out var time)) return;
            if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("usage", out var usage)) return;

            // inputTokens inclui os tokens de cache.
            var cacheRead = Long(usage, "cacheReadTokens");
            var cacheWrite = Long(usage, "cacheWriteTokens");
            var input = Math.Max(0, Long(usage, "inputTokens") - cacheRead - cacheWrite);

            var nano = usage.TryGetProperty("copilotUsage", out var cost) && cost.ValueKind == JsonValueKind.Object
                       && cost.TryGetProperty("totalNanoAiu", out var n) && n.ValueKind == JsonValueKind.Number
                ? n.GetDouble()
                : 0;

            _records[id] = new Record(time.UtcDateTime,
                new CopilotLocalUsage(input, Long(usage, "outputTokens"), cacheRead, cacheWrite, nano / NanoPerCredit));
        }
        catch (JsonException) { /* linha invalida: ignora */ }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    sealed record Record(DateTime TimeUtc, CopilotLocalUsage Usage);
}
