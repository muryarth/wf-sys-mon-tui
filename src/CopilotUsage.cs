using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WfSysMon;

record CopilotSnapshot(
    string Plan,
    bool TokenBasedBilling,
    bool Unlimited,
    double Entitlement,
    double Used,
    double Remaining,
    double PercentRemaining,
    DateTime? ResetDate,
    DateTime FetchedAt);

record CopilotState(
    CopilotSnapshot? Snapshot,
    string? Error,
    bool Loading,
    bool NeedsLogin,
    DateTime? NextRefresh,
    ModelList Models,
    int Version);

// Busca periodicamente o uso do plano GitHub Copilot do usuario.
// Usa o endpoint (nao documentado) copilot_internal/user, o mesmo usado pelo VS Code / Copilot CLI.
// Ele consome a cota "core" da API do GitHub (5.000 req/h); 1 req/min = 60 req/h.
sealed class CopilotUsage : IDisposable
{
    public const double UsdPerCredit = 0.01;          // 1 AI credit = US$ 0,01
    public const double UsdPerPremiumRequest = 0.04;  // cobranca legada por requisicao premium

    static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    static readonly TimeSpan ModelsRetryInterval = TimeSpan.FromMinutes(30);

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _wake = new(0);
    volatile CopilotState _state = new(null, null, true, false, null, ModelCatalog.LoadCachedOrFallback(), 0);
    DateTime _nextModelsFetch = DateTime.MinValue;
    string? _token;

    public CopilotState State => _state;

    public void Start() => _ = Task.Run(LoopAsync);

    public void RefreshNow() => _wake.Release();

    // Chamado apos um novo login: descarta o token em memoria e consulta imediatamente.
    public void ResetToken()
    {
        _token = null;
        RefreshNow();
    }

    async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Update(s => s with { Loading = true });
            await RefreshModelsAsync();

            TimeSpan wait;
            try
            {
                var snap = await FetchAsync(_cts.Token);
                wait = RefreshInterval;
                Update(s => s with { Snapshot = snap, Error = null, Loading = false, NeedsLogin = false, NextRefresh = DateTime.Now + wait });
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (NotAuthenticatedException ex)
            {
                wait = Timeout.InfiniteTimeSpan; // aguarda login (RefreshNow/ResetToken)
                Update(s => s with { Error = ex.Message, Loading = false, NeedsLogin = true, NextRefresh = null });
            }
            catch (RateLimitedException ex)
            {
                wait = ex.RetryAfter;
                Update(s => s with { Error = ex.Message, Loading = false, NextRefresh = DateTime.Now + wait });
            }
            catch (Exception ex)
            {
                wait = RetryInterval;
                Update(s => s with { Error = ex.Message, Loading = false, NextRefresh = DateTime.Now + wait });
            }

            try { await _wake.WaitAsync(wait, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task RefreshModelsAsync()
    {
        if (DateTime.Now < _nextModelsFetch) return;

        // lista vinda da rede/cache ainda dentro da validade: agenda a proxima busca e sai
        var models = _state.Models;
        if (models.UpdatedAt + ModelCatalog.MaxAge > DateTime.Now)
        {
            _nextModelsFetch = models.UpdatedAt + ModelCatalog.MaxAge;
            return;
        }

        try
        {
            var fresh = await ModelCatalog.FetchAsync(_http, _cts.Token);
            Update(s => s with { Models = fresh });
            _nextModelsFetch = DateTime.Now + ModelCatalog.MaxAge;
        }
        catch (Exception)
        {
            _nextModelsFetch = DateTime.Now + ModelsRetryInterval; // mantem cache/lista embutida
        }
    }

    void Update(Func<CopilotState, CopilotState> change)
    {
        var s = change(_state);
        _state = s with { Version = _state.Version + 1 };
    }

    async Task<CopilotSnapshot> FetchAsync(CancellationToken ct)
    {
        _token ??= ResolveToken()
            ?? throw new NotAuthenticatedException("conta do GitHub nao conectada");

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/copilot_internal/user");
        req.Headers.Authorization = new AuthenticationHeaderValue("token", _token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.UserAgent.ParseAdd("GitHubCopilotChat/0.35.0");
        req.Headers.Add("Editor-Version", "vscode/1.107.0");
        req.Headers.Add("Editor-Plugin-Version", "copilot-chat/0.35.0");

        using var resp = await _http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _token = null;
            throw new NotAuthenticatedException("sessao do GitHub expirada (HTTP 401)");
        }
        if (RateLimitWait(resp) is { } retry)
            throw new RateLimitedException(retry);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("conta sem GitHub Copilot ativo (HTTP 404)");
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} ao consultar o Copilot");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
        return Parse(doc.RootElement);
    }

    // 429, ou 403 com cota esgotada: espera o Retry-After ou o reset da cota.
    static TimeSpan? RateLimitWait(HttpResponseMessage resp)
    {
        if (resp.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)) return null;

        if (resp.Headers.RetryAfter?.Delta is { } delta) return Clamp(delta);

        var remaining = Header(resp, "X-RateLimit-Remaining");
        if (remaining == "0" && long.TryParse(Header(resp, "X-RateLimit-Reset"), out var reset))
            return Clamp(DateTimeOffset.FromUnixTimeSeconds(reset) - DateTimeOffset.UtcNow);

        return resp.StatusCode == HttpStatusCode.TooManyRequests ? TimeSpan.FromMinutes(1) : null;
    }

    static TimeSpan Clamp(TimeSpan t) =>
        t < TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : t > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : t;

    static string? Header(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    static CopilotSnapshot Parse(JsonElement root)
    {
        var plan = Str(root, "copilot_plan") ?? Str(root, "access_type_sku") ?? "desconhecido";
        var tbb = Bool(root, "token_based_billing") ?? false;

        DateTime? reset = null;
        var resetStr = Str(root, "quota_reset_date_utc") ?? Str(root, "quota_reset_date");
        if (DateTime.TryParse(resetStr, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var r))
            reset = r;

        if (!root.TryGetProperty("quota_snapshots", out var snaps) ||
            !snaps.TryGetProperty("premium_interactions", out var p))
            return new CopilotSnapshot(plan, tbb, false, 0, 0, 0, 100, reset, DateTime.Now);

        var unlimited = Bool(p, "unlimited") ?? false;
        var entitlement = Num(p, "entitlement") ?? 0;
        var remaining = Num(p, "quota_remaining") ?? Num(p, "remaining") ?? 0;
        var pctRemaining = Num(p, "percent_remaining") ?? (entitlement > 0 ? remaining / entitlement * 100 : 100);
        var used = Num(p, "credits_used") is { } cu && (Bool(p, "token_based_billing") ?? tbb)
            ? cu
            : Math.Max(0, entitlement - remaining);

        return new CopilotSnapshot(plan, Bool(p, "token_based_billing") ?? tbb, unlimited,
            entitlement, used, remaining, pctRemaining, reset, DateTime.Now);
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    // Ordem: GH_TOKEN, GITHUB_TOKEN, 'gh auth token'.
    static string? ResolveToken()
    {
        foreach (var env in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } t) return t.Trim();

        if (GitHubAuth.FindGh() is not { } gh) return null;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(gh, "auth token --hostname github.com")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return null;
            _ = proc.StandardError.ReadToEndAsync();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }

    sealed class NotAuthenticatedException(string message) : Exception(message);

    sealed class RateLimitedException(TimeSpan retryAfter)
        : Exception($"limite de requisicoes do GitHub atingido - nova tentativa em {Math.Ceiling(retryAfter.TotalMinutes)} min")
    {
        public TimeSpan RetryAfter { get; } = retryAfter;
    }
}
