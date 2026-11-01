using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WfSysMon;

// Preco mensal de lista de cada plano, lido das paginas publicas de precos, com cache local de 24h.
// E o preco publicado, nao a fatura da conta. Sem rede nem cache, o painel mostra "?".
static partial class PlanPrices
{
    const string ClaudeUrl = "https://claude.com/pricing";
    const string CopilotUrl = "https://docs.github.com/api/article/body?pathname=/en/copilot/concepts/billing/individual-plans";

    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    static readonly string CachePath = Path.Combine(AppPaths.DataDir, "plan-prices.json");

    static volatile CacheFile _current = new([], DateTime.MinValue);

    public static DateTime UpdatedAt => _current.UpdatedAt;

    public static double? Find(string plan) =>
        _current.Prices.TryGetValue(plan, out var price) ? price : null;

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

    // Se so uma das paginas responder, mantem os precos ja conhecidos da outra.
    public static async Task FetchAsync(HttpClient http, CancellationToken ct)
    {
        var prices = new Dictionary<string, double>(_current.Prices);
        var fresh = 0;

        foreach (var (url, parse) in new (string, Func<string, Dictionary<string, double>>)[] { (ClaudeUrl, ParseClaude), (CopilotUrl, ParseCopilot) })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                using var response = await http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                foreach (var (plan, price) in parse(await response.Content.ReadAsStringAsync(ct)))
                {
                    prices[plan] = price;
                    fresh++;
                }
            }
            catch (HttpRequestException) { /* tenta a outra pagina */ }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* tempo esgotado: tenta a outra pagina */ }
        }

        if (fresh == 0) throw new InvalidOperationException("precos dos planos indisponiveis");

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

    // "Max For all-day work $100 Per month"; no Pro, o preco mensal vem em "$20 if billed monthly".
    internal static Dictionary<string, double> ParseClaude(string html)
    {
        var text = WebUtility.HtmlDecode(TagRegex().Replace(ScriptRegex().Replace(html, " "), " "));
        text = SpaceRegex().Replace(text, " ");

        var prices = new Dictionary<string, double>();
        foreach (Match m in ClaudePlanRegex().Matches(text))
        {
            var plan = $"Claude {m.Groups[1].Value}";
            if (prices.ContainsKey(plan)) continue;

            var monthly = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[2].Value;
            if (double.TryParse(monthly, NumberStyles.Float, CultureInfo.InvariantCulture, out var price))
                prices[plan] = price;
        }
        return prices;
    }

    // Tabela de planos: "| Copilot Max | $100 USD per month | ..." ou "| Copilot Free | Free | ...".
    internal static Dictionary<string, double> ParseCopilot(string markdown)
    {
        var prices = new Dictionary<string, double>();
        foreach (Match m in CopilotPlanRegex().Matches(markdown))
        {
            var plan = m.Groups[1].Value.Trim();
            if (prices.ContainsKey(plan)) continue;

            if (m.Groups[2].Value == "Free") prices[plan] = 0;
            else if (double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var price))
                prices[plan] = price;
        }
        return prices;
    }

    [GeneratedRegex(@"<script.*?</script>|<style.*?</style>", RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex(@"\b(Pro|Max|Ultra) For [^$]{0,60}\$(\d+(?:\.\d+)?) Per month(?:[^$]{0,60}\$\d+[^$]{0,40}\$(\d+(?:\.\d+)?) if billed monthly)?")]
    private static partial Regex ClaudePlanRegex();

    [GeneratedRegex(@"^\|\s*(Copilot [^|]+?)\s*\|\s*(Free|\$(\d+(?:\.\d+)?) USD)", RegexOptions.Multiline)]
    private static partial Regex CopilotPlanRegex();

    sealed record CacheFile(Dictionary<string, double> Prices, DateTime UpdatedAt);
}
