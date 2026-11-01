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
            if (JsonRead.Str(root, "type") != "session.usage_record") return;
            if (JsonRead.Str(root, "id") is not { } id || JsonRead.Date(root, "timestamp") is not { } time) return;
            if (JsonRead.Obj(root, "data") is not { } data || JsonRead.Obj(data, "usage") is not { } usage) return;

            // inputTokens inclui os tokens de cache.
            var cacheRead = JsonRead.Long(usage, "cacheReadTokens");
            var cacheWrite = JsonRead.Long(usage, "cacheWriteTokens");
            var input = Math.Max(0, JsonRead.Long(usage, "inputTokens") - cacheRead - cacheWrite);

            var nano = JsonRead.Obj(usage, "copilotUsage") is { } cost ? JsonRead.Num(cost, "totalNanoAiu") ?? 0 : 0;

            _records[id] = new Record(time.UtcDateTime,
                new CopilotLocalUsage(input, JsonRead.Long(usage, "outputTokens"), cacheRead, cacheWrite, nano / NanoPerCredit));
        }
        catch (JsonException) { /* linha invalida: ignora */ }
    }

    sealed record Record(DateTime TimeUtc, CopilotLocalUsage Usage);
}
