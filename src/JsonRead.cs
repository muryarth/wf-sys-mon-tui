using System.Text.Json;

namespace WfSysMon;

// Leitura tolerante de JSON escrito por outros programas, cujo formato pode mudar: um valor de tipo
// inesperado vira ausente. Os metodos do JsonElement lancariam excecao nesses casos.
static class JsonRead
{
    static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds() - (long)TimeSpan.FromDays(2).TotalSeconds;

    static bool Find(JsonElement e, string name, out JsonElement value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value);
    }

    public static JsonElement? Obj(JsonElement e, string name) =>
        Find(e, name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static string? Str(JsonElement e, string name) =>
        Find(e, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool? Bool(JsonElement e, string name) =>
        Find(e, name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static double? Num(JsonElement e, string name) =>
        Find(e, name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    // Contadores: ausente ou invalido conta como zero.
    public static long Long(JsonElement e, string name) =>
        Find(e, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    public static DateTimeOffset? Date(JsonElement e, string name) =>
        Find(e, name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var d) ? d : null;

    // Fora do intervalo que uma data consegue representar tambem conta como ausente.
    public static DateTimeOffset? UnixSeconds(JsonElement e, string name) =>
        Find(e, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var s) && s >= 0 && s <= MaxUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(s)
            : null;
}
