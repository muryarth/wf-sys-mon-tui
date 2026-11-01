using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WfSysMon;

// Precos em US$ por 1M de tokens. LongFactor multiplica o custo de prompts acima de LongThreshold tokens.
record ClaudePrice(string Id, string Name, double Input, double Write5m, double Write1h, double CacheRead, double Output,
    long LongThreshold = 0, double LongFactor = 1);

// Preco de lista da API, usado para estimar o custo do uso (nao e o que um plano de assinatura cobra).
// Lido da pagina oficial, com cache local de 24h. Sem rede nem cache, a tabela fica vazia e o painel mostra "?".
static partial class ClaudePricing
{
    const string DocsUrl = "https://platform.claude.com/docs/en/about-claude/pricing.md";

    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    static readonly string CachePath = Path.Combine(AppPaths.DataDir, "claude-prices.json");

    static volatile CacheFile _current = new([], DateTime.MinValue);

    public static bool Available => _current.Prices.Count > 0;
    public static DateTime UpdatedAt => _current.UpdatedAt;

    // Aceita sufixos de data no id (ex.: claude-haiku-4-5-20251001); vence o id mais especifico.
    public static ClaudePrice? Find(string model) =>
        _current.Prices.Where(p => model == p.Id || model.StartsWith(p.Id + "-", StringComparison.Ordinal))
            .MaxBy(p => p.Id.Length);

    public static double Cost(ClaudePrice price, long input, long output, long cacheRead, long write5m, long write1h, string? speed, string? geo)
    {
        var usd = (input * price.Input + output * price.Output + cacheRead * price.CacheRead
                   + write5m * price.Write5m + write1h * price.Write1h) / 1_000_000;

        if (price.LongThreshold > 0 && input + cacheRead + write5m + write1h > price.LongThreshold) usd *= price.LongFactor;
        if (speed == "fast") usd *= 2;
        if (geo == "us") usd *= 1.1;
        return usd;
    }

    public static void LoadCache()
    {
        try
        {
            if (File.Exists(CachePath) && JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath)) is { Prices.Count: > 0 } cache)
                _current = cache;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // cache corrompido: busca de novo
        }
    }

    public static async Task FetchAsync(HttpClient http, CancellationToken ct)
    {
        var prices = Parse(await http.GetStringAsync(DocsUrl, ct));
        if (prices.Count == 0) throw new InvalidOperationException("tabela de precos nao reconhecida");

        _current = new CacheFile(prices, DateTime.Now);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_current));
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* cache e opcional */ }
    }

    // Le a primeira tabela da pagina: modelo, entrada, escrita 5 min, escrita 1h, leitura de cache, saida.
    internal static List<ClaudePrice> Parse(string markdown)
    {
        var prices = new List<ClaudePrice>();
        var inTable = false;

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('|'))
            {
                if (inTable) break;
                continue;
            }

            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (!inTable)
            {
                inTable = cells.Length >= 6 && cells[0] == "Model" && cells[1].StartsWith("Base input", StringComparison.Ordinal);
                continue;
            }
            if (cells.Length < 6 || Price(cells[1]) is not { } input || Price(cells[2]) is not { } w5
                || Price(cells[3]) is not { } w1 || Price(cells[4]) is not { } read || Price(cells[5]) is not { } output) continue;

            var parts = cells[0].Split(" (", 2);
            var name = parts[0].Trim();
            var note = parts.Length > 1 ? parts[1] : "";

            // Linha "for prompts over N tokens": vira um multiplicador do mesmo modelo.
            var over = OverRegex().Match(note);
            if (over.Success)
            {
                var index = prices.FindIndex(p => p.Name == name);
                if (index >= 0 && prices[index].Input > 0
                    && long.TryParse(over.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold))
                    prices[index] = prices[index] with { LongThreshold = threshold, LongFactor = input / prices[index].Input };
                continue;
            }

            prices.Add(new ClaudePrice(name.ToLowerInvariant().Replace(' ', '-').Replace('.', '-'), name, input, w5, w1, read, output));
        }
        return prices;
    }

    static double? Price(string cell)
    {
        var m = PriceRegex().Match(cell);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    [GeneratedRegex(@"\$\s*([\d.]+)")]
    private static partial Regex PriceRegex();

    [GeneratedRegex(@"over ([\d,]+) tokens")]
    private static partial Regex OverRegex();

    sealed record CacheFile(List<ClaudePrice> Prices, DateTime UpdatedAt);
}
