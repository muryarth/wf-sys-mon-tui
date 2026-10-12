using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WfSysMon;

// Preco por 1M de tokens (USD).
record ModelPrice(string Name, double InputPerM, double OutputPerM);

record ModelList(IReadOnlyList<ModelPrice> Models, string Source, DateTime UpdatedAt);

// Lista os modelos mais recentes de cada linha do Claude e do GPT, com os precos por token,
// a partir da pagina oficial de precos do Copilot (docs.github.com). Mantem cache local
// por 24h e usa uma lista embutida se nao houver rede nem cache.
static partial class ModelCatalog
{
    const string DocsUrl =
        "https://docs.github.com/api/article/body?pathname=/en/copilot/reference/copilot-billing/models-and-pricing";

    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wf-sys-mon", "models-cache.json");

    static readonly ModelPrice[] Fallback =
    [
        new("Claude Opus 5.5", 4.00, 20.00),
        new("Claude Sonnet 5.5", 2.00, 10.00),
        new("Claude Haiku 5.5", 0.10, 0.50),
        new("GPT-6.1 Sol", 2.00, 10.00),
        new("GPT-6 Astra", 10.00, 50.00),
        new("GPT-6 Luna", 0.10, 0.50),
    ];

    public static ModelList LoadCachedOrFallback()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath));
                if (cache?.Models is { Count: > 0 })
                    return new ModelList(cache.Models, "cache", cache.UpdatedAt);
            }
        }
        catch (Exception) { /* cache corrompido - ignora */ }

        return new ModelList(Fallback, "embutido", DateTime.MinValue);
    }

    public static async Task<ModelList> FetchAsync(HttpClient http, CancellationToken ct)
    {
        var markdown = await http.GetStringAsync(DocsUrl, ct);
        var models = Latest(Parse(markdown));
        if (models.Count == 0) throw new InvalidOperationException("tabela de precos nao reconhecida");

        var list = new ModelList(models, "docs.github.com", DateTime.Now);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new CacheFile(list.Models.ToList(), list.UpdatedAt)));
        }
        catch (Exception) { /* cache e opcional */ }
        return list;
    }

    // Le todas as tabelas markdown com colunas Model / Input / Output (tier "Default" apenas).
    internal static List<ModelPrice> Parse(string markdown)
    {
        var result = new List<ModelPrice>();
        int model = -1, tier = -1, input = -1, output = -1;

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('|')) { model = -1; continue; }

            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length > 0 && cells[0] == "Model")
            {
                model = 0;
                tier = Array.IndexOf(cells, "Tier");
                input = Array.IndexOf(cells, "Input");
                output = Array.IndexOf(cells, "Output");
                continue;
            }
            if (model < 0 || input < 0 || output < 0 || cells.Length <= Math.Max(input, output)) continue;
            if (cells[model].Length == 0 || cells[model].StartsWith('-')) continue;
            if (tier >= 0 && !cells[tier].Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;

            if (Price(cells[input]) is { } i && Price(cells[output]) is { } o)
                result.Add(new ModelPrice(cells[model], i, o));
        }
        return result;
    }

    // Mantem apenas a versao mais nova de cada linha (ex.: "Claude Opus", "GPT Sol", "GPT mini", "GPT-Codex").
    internal static List<ModelPrice> Latest(IEnumerable<ModelPrice> all)
    {
        var best = new Dictionary<string, (Version Version, ModelPrice Model)>();
        foreach (var m in all)
        {
            if (m.Name.Contains('(')) continue; // variantes como "(fast mode)" / "(preview)"
            if (Family(m.Name) is not { } f) continue;
            if (!best.TryGetValue(f.Key, out var cur) || f.Version > cur.Version)
                best[f.Key] = (f.Version, m);
        }

        // Claude primeiro, depois GPT; dentro de cada um, do mais caro (mais potente) ao mais barato.
        return best.Values
            .Select(v => v.Model)
            .OrderBy(m => m.Name.StartsWith("Claude") ? 0 : 1)
            .ThenByDescending(m => m.OutputPerM)
            .ThenBy(m => m.Name)
            .ToList();
    }

    static (string Key, Version Version)? Family(string name)
    {
        var claude = ClaudeRegex().Match(name);
        if (claude.Success)
            return ($"Claude {claude.Groups[1].Value}", ParseVersion(claude.Groups[2].Value));

        var gpt = GptRegex().Match(name);
        if (gpt.Success)
            return ($"GPT{gpt.Groups[2].Value.Trim().ToLowerInvariant()}", ParseVersion(gpt.Groups[1].Value));

        return null;
    }

    static Version ParseVersion(string v) => Version.Parse(v.Contains('.') ? v : v + ".0");

    static double? Price(string cell) =>
        double.TryParse(cell.Replace("$", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    [GeneratedRegex(@"^Claude ([A-Za-z]+) (\d+(?:\.\d+)?)$")]
    private static partial Regex ClaudeRegex();

    [GeneratedRegex(@"^GPT-(\d+(?:\.\d+)?)(.*)$")]
    private static partial Regex GptRegex();

    sealed record CacheFile(List<ModelPrice> Models, DateTime UpdatedAt);
}
