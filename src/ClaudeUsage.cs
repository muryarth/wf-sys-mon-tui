using System.Text.Json;

using static WfSysMon.L;

namespace WfSysMon;

// Used: tokens e custo registrados nos logs deste PC dentro da janela.
record ClaudeWindow(double UsedPercent, DateTime ResetsAt, ClaudeTokens? Used = null);

// Usd e o custo estimado a preco de lista da API.
record ClaudeTokens(long Input, long Output, long CacheRead, long CacheWrite, double Usd = 0)
{
    public static readonly ClaudeTokens Zero = new(0, 0, 0, 0);

    public long Total => Input + Output + CacheRead + CacheWrite;

    public ClaudeTokens Add(ClaudeTokens o) =>
        new(Input + o.Input, Output + o.Output, CacheRead + o.CacheRead, CacheWrite + o.CacheWrite, Usd + o.Usd);

    public ClaudeTokens Max(ClaudeTokens o) =>
        new(Math.Max(Input, o.Input), Math.Max(Output, o.Output), Math.Max(CacheRead, o.CacheRead), Math.Max(CacheWrite, o.CacheWrite), Math.Max(Usd, o.Usd));
}

record ClaudeModelUsage(string Model, ClaudeTokens Tokens);

record ClaudeState(
    ClaudeWindow? FiveHour,
    ClaudeWindow? SevenDay,
    DateTime? QuotaCapturedAt,
    bool HasQuotaFiles,
    ClaudeTokens? Month,      // null ate a primeira leitura dos logs terminar
    IReadOnlyList<ClaudeModelUsage> MonthByModel,
    DateOnly? MonthFirstDay,
    string? LogsError,
    string? Plan,             // ex.: "Claude Max"; null se nao for possivel obter
    bool? LoggedIn,           // null: CLI ausente ou ainda nao consultado
    int Version,
    DateTime? CheckedAt = null,
    bool AccountChecked = false);  // a conta ja foi consultada: LoggedIn null significa CLI ausente  // ultima leitura local; a cota em si so muda quando o Claude Code responde

// Dados do Claude, todos locais (sem rede e sem credenciais):
//  - cota de 5h e 7 dias: gravada pela status line do Claude Code (claude-statusline.ps1);
//  - tokens e custo estimado do mes: somados dos logs de sessao e guardados por dia e modelo em
//    %LOCALAPPDATA%, para o inicio do mes nao se perder quando o Claude Code apagar os logs antigos.
// As unicas consultas de rede sao as paginas publicas de precos, sem autenticacao. O plano vem do
// comando 'claude auth status'.
sealed class ClaudeUsage : IDisposable
{
    static readonly TimeSpan QuotaInterval = TimeSpan.FromSeconds(5);
    static readonly TimeSpan LogsInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromDays(8);  // maior que a janela de 7 dias
    static readonly TimeSpan PricesRetryInterval = TimeSpan.FromMinutes(30);
    static readonly TimeSpan PlanReadInterval = TimeSpan.FromHours(1);
    const int KeepDaysForWindows = 8;  // maior que a janela de 7 dias

    static readonly string DataDir = AppPaths.DataDir;
    static readonly string QuotaDir = Path.Combine(DataDir, "claude-status");
    static readonly string DaysPath = Path.Combine(DataDir, "claude-usage-days.json");

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _wake = new(0, 1);
    DateTime _nextPricesFetch = DateTime.MinValue;
    readonly JsonlTail _tail = new();
    readonly Dictionary<string, Response> _responses = [];        // respostas do mes, sem repeticao
    readonly Dictionary<DateOnly, Dictionary<string, ClaudeTokens>> _days;  // totais por dia e modelo (persistidos)
    DateOnly _keepSince;
    DateTime _nextLogsScan = DateTime.MinValue;
    DateTime _nextPlanPricesFetch = DateTime.MinValue;
    DateTime _nextPlanRead = DateTime.MinValue;
    volatile string? _plan;
    bool? _loggedIn;
    bool _accountChecked;
    volatile ClaudeState _state = new(null, null, null, false, null, [], null, null, null, null, 0);

    public ClaudeState State => _state;

    public ClaudeUsage()
    {
        _days = LoadDays();
        ClaudePricing.LoadCache();
        PlanPrices.LoadCache();
    }

    public void Start() => _ = Task.Run(LoopAsync);

    // Depois de um login: consulta a conta de novo em vez de esperar a proxima hora.
    public void CheckAccountNow()
    {
        _nextPlanRead = DateTime.MinValue;
        RefreshNow();
    }

    public void RefreshNow()
    {
        _nextLogsScan = DateTime.MinValue;
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* ja ha uma atualizacao pendente */ }
    }

    async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var next = _state;
            try
            {
                next = ReadQuota(next) with { CheckedAt = DateTime.Now };
                if (DateTime.Now >= _nextLogsScan)
                {
                    next = ReadLogs(next);
                    _nextLogsScan = DateTime.Now + LogsInterval;
                }
                next = next with
                {
                    FiveHour = WithUsage(next.FiveHour, TimeSpan.FromHours(5)),
                    SevenDay = WithUsage(next.SevenDay, TimeSpan.FromDays(7)),
                };
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Qualquer falha fica visivel no painel; a leitura continua na proxima rodada.
                next = next with { LogsError = ex.Message, Month = next.Month ?? ClaudeTokens.Zero };
                _nextLogsScan = DateTime.Now + LogsInterval;
            }

            Publish(next with { Plan = _plan, LoggedIn = _loggedIn, AccountChecked = _accountChecked });
            try
            {
                await RefreshPricesAsync();
                await RefreshPlanPricesAsync();
                await RefreshPlanAsync();
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
            catch (Exception) { /* precos e plano sao opcionais: mantem o que ja tem */ }

            if (!_accountChecked)
            {
                _accountChecked = true;
                Publish(_state with { AccountChecked = true });
            }

            try { await _wake.WaitAsync(QuotaInterval, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task RefreshPricesAsync()
    {
        if (DateTime.Now < _nextPricesFetch) return;

        if (ClaudePricing.UpdatedAt + ClaudePricing.MaxAge > DateTime.Now)
        {
            _nextPricesFetch = ClaudePricing.UpdatedAt + ClaudePricing.MaxAge;
            return;
        }

        var hadPrices = ClaudePricing.Available;
        try
        {
            await ClaudePricing.FetchAsync(_http, _cts.Token);
            _nextPricesFetch = DateTime.Now + ClaudePricing.MaxAge;

            // Os logs lidos sem preco ficaram sem custo: le tudo de novo com a tabela nova.
            if (!hadPrices)
            {
                _tail.Reset();
                _responses.Clear();
                _nextLogsScan = DateTime.MinValue;
                RefreshNow();
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _nextPricesFetch = DateTime.Now + PricesRetryInterval;  // mantem o cache, ou "?" no painel
        }
    }

    async Task RefreshPlanPricesAsync()
    {
        if (DateTime.Now < _nextPlanPricesFetch) return;

        if (PlanPrices.UpdatedAt + PlanPrices.MaxAge > DateTime.Now)
        {
            _nextPlanPricesFetch = PlanPrices.UpdatedAt + PlanPrices.MaxAge;
            return;
        }

        try
        {
            await PlanPrices.FetchAsync(_http, _cts.Token);
            _nextPlanPricesFetch = DateTime.Now + PlanPrices.MaxAge;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (InvalidOperationException)
        {
            _nextPlanPricesFetch = DateTime.Now + PricesRetryInterval;  // mantem o cache, ou "?" no painel
        }
    }

    async Task RefreshPlanAsync()
    {
        if (DateTime.Now < _nextPlanRead) return;
        _nextPlanRead = DateTime.Now + PlanReadInterval;

        // Usa so o tipo de assinatura da resposta; o restante (como o e-mail) e descartado.
        try
        {
            var psi = ExternalTool.PrepareHidden("claude", "auth status --json");
            var result = psi is null ? null : await ExternalTool.RunAsync(psi, TimeSpan.FromSeconds(20), _cts.Token);
            if (result is null)
            {
                _loggedIn = null;
                _plan = null;
                Publish(_state with { Plan = null, LoggedIn = null });
                return;
            }

            using var doc = JsonDocument.Parse(result.Output);
            _loggedIn = JsonRead.Bool(doc.RootElement, "loggedIn") == true;
            _plan = JsonRead.Str(doc.RootElement, "subscriptionType") is { Length: > 0 } type
                ? $"Claude {char.ToUpperInvariant(type[0])}{type[1..]}"
                : null;
            Publish(_state with { Plan = _plan, LoggedIn = _loggedIn });
        }
        catch (Exception ex) when (ex is JsonException or OperationCanceledException)
        {
            // sem o CLI: o painel mostra "?"
            _loggedIn = null;
            Publish(_state with { LoggedIn = null });
        }
    }

    void Publish(ClaudeState next)
    {
        var current = _state;
        if (next with { Version = current.Version } != current)
            _state = next with { Version = current.Version + 1 };
    }

    // ---------------------------------------------------------------- Cota

    // Cada sessao grava o proprio arquivo. So existe uma janela ativa por vez e o uso dentro dela
    // so cresce, entre as janelas nao vencidas vale o maior percentual (a sessao mais recente).
    static ClaudeState ReadQuota(ClaudeState state)
    {
        if (!Directory.Exists(QuotaDir))
            return state with { FiveHour = null, SevenDay = null, QuotaCapturedAt = null, HasQuotaFiles = false };

        var now = DateTime.UtcNow;
        ClaudeWindow? fiveHour = null, sevenDay = null;
        DateTime? captured = null;
        var hasFiles = false;

        foreach (var file in Directory.EnumerateFiles(QuotaDir, "*.json"))
        {
            hasFiles = true;
            try
            {
                if (now - File.GetLastWriteTimeUtc(file) > SnapshotMaxAge)
                {
                    File.Delete(file);
                    continue;
                }

                using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                var root = doc.RootElement;
                var five = ParseWindow(root, "five_hour", now);
                var seven = ParseWindow(root, "seven_day", now);
                if (five is null && seven is null) continue;

                fiveHour = Highest(fiveHour, five);
                sevenDay = Highest(sevenDay, seven);
                if (JsonRead.UnixSeconds(root, "captured_at") is { } capturedAt)
                {
                    var at = capturedAt.LocalDateTime;
                    if (captured is null || at > captured) captured = at;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // arquivo em uso ou invalido: a proxima leitura tenta de novo
            }
        }

        return state with { FiveHour = fiveHour, SevenDay = sevenDay, QuotaCapturedAt = captured, HasQuotaFiles = hasFiles };
    }

    static ClaudeWindow? ParseWindow(JsonElement root, string name, DateTime nowUtc)
    {
        if (JsonRead.Obj(root, name) is not { } w) return null;
        if (JsonRead.Num(w, "used_percentage") is not { } pct) return null;
        if (JsonRead.UnixSeconds(w, "resets_at") is not { } reset) return null;

        var resetsAt = reset.UtcDateTime;
        return resetsAt <= nowUtc ? null : new ClaudeWindow(pct, resetsAt.ToLocalTime());
    }

    ClaudeWindow? WithUsage(ClaudeWindow? window, TimeSpan length)
    {
        if (window is null) return null;

        var start = window.ResetsAt - length;
        var used = _responses.Values
            .Where(r => r.Time >= start && r.Time < window.ResetsAt)
            .Aggregate(ClaudeTokens.Zero, (sum, r) => sum.Add(r.Tokens));
        return window with { Used = used };
    }

    static ClaudeWindow? Highest(ClaudeWindow? a, ClaudeWindow? b) =>
        a is null ? b : b is null ? a : a.UsedPercent >= b.UsedPercent ? a : b;

    // ---------------------------------------------------------------- Tokens do mes

    ClaudeState ReadLogs(ClaudeState state)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var month = new DateOnly(today.Year, today.Month, 1);

        // Le o mes atual e, na virada do mes, os dias que ainda fazem parte da janela de 7 dias.
        var keepSince = today.AddDays(-KeepDaysForWindows);
        _keepSince = keepSince < month ? keepSince : month;
        foreach (var key in _responses.Where(r => r.Value.Day < _keepSince).Select(r => r.Key).ToList())
            _responses.Remove(key);

        var root = Path.Combine(ClaudeSetup.ConfigDir, "projects");
        var readSince = _keepSince.ToDateTime(TimeOnly.MinValue);
        var sawLog = false;

        if (Directory.Exists(root))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", options))
            {
                _cts.Token.ThrowIfCancellationRequested();
                try
                {
                    if (File.GetLastWriteTime(path) < readSince) continue;
                    if (new FileInfo(path).Length > 0) sawLog = true;
                    _tail.Read(path, ProcessLine);
                }
                catch (IOException) { /* log em uso: tenta de novo na proxima leitura */ }
                catch (UnauthorizedAccessException) { }
            }
        }

        MergeDays();

        var monthDays = _days.Where(d => d.Key >= month && d.Value.Values.Any(t => t.Total > 0)).ToList();
        var byModel = monthDays
            .SelectMany(d => d.Value)
            .Where(m => m.Value.Total > 0)
            .GroupBy(m => m.Key)
            .Select(g => new ClaudeModelUsage(g.Key, g.Aggregate(ClaudeTokens.Zero, (sum, m) => sum.Add(m.Value))))
            .OrderByDescending(m => m.Tokens.Usd)
            .ThenByDescending(m => m.Tokens.Total)
            .ToList();
        var total = byModel.Aggregate(ClaudeTokens.Zero, (sum, m) => sum.Add(m.Tokens));
        DateOnly? first = monthDays.Count > 0 ? monthDays.Min(d => d.Key) : null;

        // Ha log deste mes e nenhuma resposta reconhecida: provavel mudanca no formato dos logs.
        var error = sawLog && _responses.Count == 0 && monthDays.Count == 0
            ? T("nenhum uso reconhecido nos logs do Claude Code", "no usage recognized in the Claude Code logs")
            : null;

        return state with { Month = total, MonthByModel = byModel, MonthFirstDay = first, LogsError = error };
    }

    void ProcessLine(ReadOnlyMemory<byte> line)
    {
        // Filtro barato: a maioria das linhas nao tem uso, e varias sao grandes.
        if (line.Span.IndexOf("\"usage\""u8) < 0) return;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (JsonRead.Str(root, "type") != "assistant") return;
            if (JsonRead.Obj(root, "message") is not { } message) return;
            if (JsonRead.Obj(message, "usage") is not { } usage) return;
            if (JsonRead.Str(message, "id") is not { } id) return;
            if (!DateTimeOffset.TryParse(JsonRead.Str(root, "timestamp"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var timestamp)) return;

            var time = timestamp.LocalDateTime;
            var day = DateOnly.FromDateTime(time);
            if (day < _keepSince) return;

            var input = JsonRead.Long(usage, "input_tokens");
            var output = JsonRead.Long(usage, "output_tokens");
            var cacheRead = JsonRead.Long(usage, "cache_read_input_tokens");
            var cacheWrite = JsonRead.Long(usage, "cache_creation_input_tokens");

            // A escrita de cache de 1h custa mais que a de 5 min; sem o detalhe, conta como 5 min.
            var write1h = JsonRead.Obj(usage, "cache_creation") is { } creation
                ? JsonRead.Long(creation, "ephemeral_1h_input_tokens")
                : 0;
            var write5m = Math.Max(0, cacheWrite - write1h);

            var model = JsonRead.Str(message, "model") ?? "desconhecido";
            var usd = ClaudePricing.Find(model) is { } price
                ? ClaudePricing.Cost(price, input, output, cacheRead, write5m, write1h, JsonRead.Str(usage, "speed"), JsonRead.Str(usage, "inference_geo"))
                : 0;
            var tokens = new ClaudeTokens(input, output, cacheRead, cacheWrite, usd);

            // A mesma resposta aparece em varias linhas e pode se repetir em outros logs da sessao.
            var key = $"{id}|{JsonRead.Str(root, "requestId")}";
            _responses[key] = _responses.TryGetValue(key, out var seen)
                ? seen with { Tokens = seen.Tokens.Max(tokens) }
                : new Response(day, time, model, tokens);
        }
        catch (JsonException) { /* linha invalida: ignora */ }
    }

    // O total salvo de um dia so cresce: logs apagados nao reduzem o que ja foi contado.
    void MergeDays()
    {
        var computed = new Dictionary<(DateOnly Day, string Model), ClaudeTokens>();
        foreach (var r in _responses.Values)
            computed[(r.Day, r.Model)] = computed.GetValueOrDefault((r.Day, r.Model), ClaudeTokens.Zero).Add(r.Tokens);

        var changed = false;
        foreach (var ((day, model), tokens) in computed)
        {
            if (!_days.TryGetValue(day, out var models)) _days[day] = models = [];
            var merged = models.GetValueOrDefault(model, ClaudeTokens.Zero).Max(tokens);
            if (models.GetValueOrDefault(model) == merged) continue;
            models[model] = merged;
            changed = true;
        }
        if (changed) SaveDays();
    }

    static Dictionary<DateOnly, Dictionary<string, ClaudeTokens>> LoadDays()
    {
        try
        {
            if (File.Exists(DaysPath))
                return JsonSerializer.Deserialize<Dictionary<DateOnly, Dictionary<string, ClaudeTokens>>>(File.ReadAllText(DaysPath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // arquivo corrompido: recomeca a partir dos logs ainda disponiveis
        }
        return [];
    }

    void SaveDays()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = DaysPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_days));
            File.Move(tmp, DaysPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // sem persistencia nesta rodada: tenta de novo quando houver novo uso
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }

    sealed record Response(DateOnly Day, DateTime Time, string Model, ClaudeTokens Tokens);
}
