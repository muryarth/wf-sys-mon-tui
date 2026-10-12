using LibreHardwareMonitor.Hardware;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace WfSysMon;

static class Dashboard
{
    public static void UpdateSensors(Computer computer)
    {
        foreach (var hw in computer.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
        }
    }

    const int MinColWidth = 48;   // abaixo disso os paineis ficam apertados
    const int MaxColWidth = 120;  // acima disso vale mais a pena abrir outra coluna

    // Monta o dashboard ocupando width x height:
    //  - usa o menor numero de colunas cujo conteudo caiba na altura (respeitando Min/MaxColWidth);
    //  - distribui os paineis entre as colunas equilibrando as alturas;
    //  - estica o ultimo painel de cada coluna ate o fim da tela.
    public static IRenderable Render(Computer computer, CopilotState copilot, AuthState auth, int width, int height)
    {
        var sep = $"  [{Theme.Unit}]|[/]  ";
        var keys = copilot.NeedsLogin
            ? $"{Key("L")} [{Theme.Label}]entrar no GitHub[/]"
            : $"{Key("R")} [{Theme.Label}]atualiza Copilot[/]";
        var header = new Rule(width >= 60
                ? $"[bold {Theme.Title}]Workflow System Monitor[/]  [{Theme.Clock}]{DateTime.Now:HH:mm:ss}[/]{sep}{keys}{sep}{Key("Q")}[{Theme.Unit}]/[/]{Key("Esc")} [{Theme.Label}]sai[/]"
                : $"[bold {Theme.Title}]HW Monitor[/] [{Theme.Clock}]{DateTime.Now:HH:mm:ss}[/]")
            .LeftJustified()
            .RuleStyle(Theme.Rule);

        var bodyHeight = Math.Max(height - 1, 1);
        var maxCols = Math.Clamp(width / MinColWidth, 1, 4);
        var minCols = Math.Clamp((int)Math.Ceiling(width / (double)MaxColWidth), 1, maxCols);

        ColumnLayout? best = null;
        for (var n = minCols; n <= maxCols; n++)
        {
            var layout = Arrange(computer, copilot, auth, width / n, n);
            if (best is null || layout.Tallest < best.Tallest) best = layout;
            if (layout.Tallest <= bodyHeight) { best = layout; break; }
        }

        // Estica o ultimo painel de cada coluna para alinhar todas ao fim da tela.
        foreach (var col in best!.Columns)
        {
            var used = col.Sum(i => best.Heights[i]);
            var last = col[^1];
            if (used < bodyHeight) best.Panels[last].Height = best.Heights[last] + bodyHeight - used;
        }

        IRenderable body = best.Columns.Count == 1
            ? new Rows(best.Columns[0].Select(i => best.Panels[i]))
            : EqualColumns(best.ColWidth, best.Columns.Select(c => (IRenderable)new Rows(c.Select(i => best.Panels[i]))).ToArray());

        return new Rows(header, body);
    }

    sealed record ColumnLayout(int ColWidth, List<Panel> Panels, List<int> Heights, List<List<int>> Columns)
    {
        public int Tallest => Columns.Max(c => c.Sum(i => Heights[i]));
    }

    static ColumnLayout Arrange(Computer computer, CopilotState copilot, AuthState auth, int colWidth, int columns)
    {
        var panels = BuildPanels(computer, copilot, auth, colWidth);
        var heights = panels.Select(p => Screen.RenderLines(p, colWidth).Count).ToList();
        columns = Math.Min(columns, panels.Count);

        // Maior painel primeiro, sempre na coluna mais baixa (LPT); depois mantem a ordem original
        // dentro de cada coluna e ordena as colunas pelo primeiro painel (CPU fica a esquerda).
        var cols = Enumerable.Range(0, columns).Select(_ => new List<int>()).ToList();
        foreach (var i in Enumerable.Range(0, panels.Count).OrderByDescending(i => heights[i]).ThenBy(i => i))
            cols.MinBy(c => c.Sum(j => heights[j]))!.Add(i);
        foreach (var c in cols) c.Sort();
        cols = cols.Where(c => c.Count > 0).OrderBy(c => c[0]).ToList();

        return new ColumnLayout(colWidth, panels, heights, cols);
    }

    static List<Panel> BuildPanels(Computer computer, CopilotState copilot, AuthState auth, int colWidth)
    {
        var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        var mem = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
        var gpus = computer.Hardware.Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel);
        var mobo = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
        var disks = computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage).ToList();

        var panels = new List<Panel>
        {
            cpu is null ? Unavailable("CPU", Theme.Cpu) : CpuPanel(cpu, colWidth),
            mem is null ? Unavailable("Memoria", Theme.Memory) : MemoryPanel(mem, colWidth),
        };
        panels.AddRange(gpus.Select(g => GpuPanel(g, colWidth)));
        panels.Add(mobo is null ? Unavailable("Placa-mae", Theme.Mobo) : MotherboardPanel(mobo));
        panels.Add(disks.Count == 0 ? Unavailable("Storage", Theme.Storage) : StoragePanel(disks, colWidth));
        panels.Add(CopilotPanel(copilot, auth, colWidth));
        return panels;
    }

    static Grid EqualColumns(int colWidth, params IRenderable[] cells)
    {
        var grid = new Grid();
        foreach (var _ in cells)
            grid.AddColumn(new GridColumn().Width(colWidth).Padding(0, 0, 0, 0));
        grid.AddRow(cells);
        return grid;
    }

    // ---------------------------------------------------------------- CPU

    static Panel CpuPanel(IHardware cpu, int colWidth)
    {
        var temps = Sensors(cpu, SensorType.Temperature);
        var loads = Sensors(cpu, SensorType.Load);
        var clocks = Sensors(cpu, SensorType.Clock);
        var power = Sensors(cpu, SensorType.Power).FirstOrDefault(s => s.Name.Contains("Package"));

        var pkgTemp = temps.FirstOrDefault(s => s.Name.Contains("Package") || s.Name.Contains("Tctl") || s.Name.Contains("Tdie"));
        var totalLoad = loads.FirstOrDefault(s => s.Name.Contains("Total"));

        var summary = new Table().Border(TableBorder.None).HideHeaders().AddColumn("k").AddColumn("v");
        summary.AddRow(Label("Pacote"), TempMarkup(pkgTemp?.Value));
        summary.AddRow(Label("Uso total"), LoadBar(totalLoad?.Value, BarWidth(colWidth, 22, 80)));
        if (power?.Value is { } w) summary.AddRow(Label("Potencia"), Value($"{w:F1}", "W"));

        var cores = new Table()
            .Expand()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse(Theme.TableBorder))
            .AddColumn(new TableColumn(Head("Nucleo", Theme.Cpu)).Width(6).NoWrap())
            .AddColumn(new TableColumn(Head("Temp", Theme.Cpu)).Width(6).NoWrap().RightAligned())
            .AddColumn(new TableColumn(Head("Uso", Theme.Cpu)).NoWrap())
            .AddColumn(new TableColumn(Head("Clock", Theme.Cpu)).Width(8).NoWrap().RightAligned());

        var coreTemps = temps
            .Where(s => s.Name.StartsWith("CPU Core #") && !s.Name.Contains("Distance"))
            .OrderBy(s => CoreIndex(s.Name));

        foreach (var t in coreTemps)
        {
            var idx = CoreIndex(t.Name);
            var load = loads.FirstOrDefault(s => s.Name == $"CPU Core #{idx}")
                    ?? loads.FirstOrDefault(s => s.Name.StartsWith($"CPU Core #{idx} "));
            var clock = clocks.FirstOrDefault(s => s.Name == $"CPU Core #{idx}");

            cores.AddRow(
                $"[{Theme.Cpu}]#{idx}[/]",
                TempMarkup(t.Value),
                LoadBar(load?.Value, BarWidth(colWidth, 42, 70)),
                clock?.Value is { } c ? Value($"{c:F0}", "MHz", Theme.Clock) : Dash);
        }

        return Styled(new Rows(summary, cores), "CPU", Theme.Cpu, cpu.Name);
    }

    // ------------------------------------------------------------- Memoria

    static Panel MemoryPanel(IHardware mem, int colWidth)
    {
        var used = Sensors(mem, SensorType.Data).FirstOrDefault(s => s.Name == "Memory Used")?.Value;
        var avail = Sensors(mem, SensorType.Data).FirstOrDefault(s => s.Name == "Memory Available")?.Value;
        var pct = Sensors(mem, SensorType.Load).FirstOrDefault(s => s.Name == "Memory")?.Value;

        var t = new Table().Border(TableBorder.None).HideHeaders().AddColumn("k").AddColumn("v");
        t.AddRow(Label("Em uso"), used is { } u ? Value($"{u:F1}", "GB") : Dash);
        t.AddRow(Label("Disponivel"), avail is { } a ? Value($"{a:F1}", "GB", Theme.Good) : Dash);
        t.AddRow("", LoadBar(pct, BarWidth(colWidth, 24, 80)));

        return Styled(t, "Memoria RAM", Theme.Memory);
    }

    // ----------------------------------------------------------------- GPU

    static Panel GpuPanel(IHardware gpu, int colWidth)
    {
        var temp = Sensors(gpu, SensorType.Temperature).FirstOrDefault(s => s.Name.Contains("Core"))?.Value;
        var load = Sensors(gpu, SensorType.Load).FirstOrDefault(s => s.Name.Contains("Core"))?.Value;
        var fan = Sensors(gpu, SensorType.Fan).FirstOrDefault()?.Value;
        var power = Sensors(gpu, SensorType.Power).FirstOrDefault()?.Value;
        var memUsed = Sensors(gpu, SensorType.SmallData).FirstOrDefault(s => s.Name.Contains("Memory Used"))?.Value;
        var memTotal = Sensors(gpu, SensorType.SmallData).FirstOrDefault(s => s.Name.Contains("Memory Total"))?.Value;

        var t = new Table().Border(TableBorder.None).HideHeaders().AddColumn("k").AddColumn("v");
        t.AddRow(Label("Temperatura"), TempMarkup(temp));
        t.AddRow(Label("Uso"), LoadBar(load, BarWidth(colWidth, 25, 80)));
        if (memUsed is { } mu && memTotal is { } mt) t.AddRow(Label("VRAM"), $"{Value($"{mu / 1024:F1}", "")}[{Theme.Unit}]/[/] {Value($"{mt / 1024:F1}", "GB")}");
        if (fan is { } f) t.AddRow(Label("Fan"), Value($"{f:F0}", "RPM", Theme.Fan));
        if (power is { } p) t.AddRow(Label("Potencia"), Value($"{p:F0}", "W"));

        return Styled(t, "GPU", Theme.Gpu, gpu.Name);
    }

    // ----------------------------------------------------------- Placa-mae

    static Panel MotherboardPanel(IHardware mobo)
    {
        var temps = Sensors(mobo, SensorType.Temperature).Where(s => s.Value > 0).Take(4).ToList();
        var fans = Sensors(mobo, SensorType.Fan).Where(s => s.Value > 0).ToList();

        var t = new Table().Border(TableBorder.None).HideHeaders().AddColumn("k").AddColumn("v");
        foreach (var s in temps) t.AddRow(Label(s.Name), TempMarkup(s.Value));
        foreach (var s in fans) t.AddRow(Label(s.Name), Value($"{s.Value:F0}", "RPM", Theme.Fan));
        if (t.Rows.Count == 0) t.AddRow($"[{Theme.Unit}]sem sensores expostos[/]", "");

        return Styled(t, "Placa-mae", Theme.Mobo, mobo.Name);
    }

    // ------------------------------------------------------------- Storage

    static Panel StoragePanel(List<IHardware> disks, int colWidth)
    {
        var t = new Table().Border(TableBorder.None).HideHeaders().AddColumn("k").AddColumn("v").AddColumn("u");
        foreach (var d in disks)
        {
            var temp = Sensors(d, SensorType.Temperature).FirstOrDefault()?.Value;
            var used = Sensors(d, SensorType.Load).FirstOrDefault(s => s.Name == "Used Space")?.Value;
            t.AddRow(Label(d.Name), TempMarkup(temp), LoadBar(used, BarWidth(colWidth, 45, 40)));
        }
        return Styled(t, "Storage", Theme.Storage);
    }

    // ------------------------------------------------------------- Copilot

    static Panel CopilotPanel(CopilotState state, AuthState auth, int colWidth)
    {
        const string title = "GitHub Copilot";
        var snap = state.Snapshot;

        var authBusy = auth.Step is AuthStep.InstallingGh or AuthStep.Starting or AuthStep.WaitingUser;
        if (state.NeedsLogin || authBusy)
            return Styled(LoginContent(state, auth), title, Theme.Copilot);

        if (snap is null)
        {
            var msg = state.Error is { } err
                ? $"[bold {Theme.Bad}]Erro:[/] {Markup.Escape(err)}"
                : $"[{Theme.Warn}]consultando plano...[/]";
            return Styled(new Markup(msg), title, Theme.Copilot);
        }

        var unit = snap.TokenBasedBilling ? "creditos" : "req. premium";
        var usdPerUnit = snap.TokenBasedBilling ? CopilotUsage.UsdPerCredit : CopilotUsage.UsdPerPremiumRequest;

        var t = new Table().Border(TableBorder.None).HideHeaders().AddColumn("k").AddColumn("v");
        t.AddRow(Label("Plano"), $"[bold {Theme.Copilot}]{Markup.Escape(PlanName(snap.Plan))}[/] [{Theme.Unit}]({Markup.Escape(snap.Plan)})[/]");

        if (snap.Unlimited)
        {
            t.AddRow(Label("Uso"), $"[bold {Theme.Good}]ilimitado[/]");
        }
        else
        {
            var usedPct = (float)Math.Clamp(100 - snap.PercentRemaining, 0, 100);
            t.AddRow(Label("Consumo"), LoadBar(usedPct, BarWidth(colWidth, 24, 80)));
            t.AddRow(Label("Gasto"), $"[bold {Theme.Spent}]{Int(snap.Used)}[/] [{Theme.Unit}]/[/] [bold]{Int(snap.Entitlement)}[/] [{Theme.Unit}]{unit}[/] [{Theme.Money}](US$ {snap.Used * usdPerUnit:F2})[/]");
            t.AddRow(Label("Restante"), $"[bold {Theme.Good}]{Int(snap.Remaining)}[/] [{Theme.Unit}]{unit}[/] [{Theme.Money}](US$ {snap.Remaining * usdPerUnit:F2})[/]");
        }

        if (snap.ResetDate is { } reset)
        {
            var days = Math.Max(0, (int)Math.Ceiling((reset - DateTime.UtcNow).TotalDays));
            var daysColor = days <= 3 ? Theme.Warn : Theme.Unit;
            t.AddRow(Label("Renova em"), $"[bold]{reset:dd/MM/yyyy}[/] [{daysColor}]({days} dias)[/]");
        }

        var status = state.Loading ? $"[{Theme.Warn}]atualizando...[/]" : $"[{Theme.Clock}]{snap.FetchedAt:HH:mm:ss}[/]";
        if (!state.Loading && state.NextRefresh is { } next)
            status += $" [{Theme.Unit}](proxima em {Math.Max(0, (int)(next - DateTime.Now).TotalSeconds)}s)[/]";
        if (state.Error is { } e) status += $" [{Theme.Bad}]({Markup.Escape(e)})[/]";
        t.AddRow(Label("Atualizado"), status);

        var content = new List<IRenderable> { t };

        if (snap.TokenBasedBilling && !snap.Unlimited)
        {
            var tokens = new Table()
                .Expand()
                .Border(TableBorder.Rounded)
                .BorderStyle(Style.Parse(Theme.TableBorder))
                .Title($"[{Theme.Label}]equivalente em tokens ([/][{Theme.Input}]entrada[/] [{Theme.Unit}]/[/] [{Theme.Output}]saida[/][{Theme.Label}])[/]")
                .AddColumn(new TableColumn(Head("Modelo", Theme.Copilot)).NoWrap());

            // Em colunas estreitas mostra so o restante para nao quebrar linhas.
            var showSpent = colWidth >= 64;
            if (showSpent) tokens.AddColumn(new TableColumn(Head("Gasto", Theme.Spent)).NoWrap().RightAligned());
            tokens.AddColumn(new TableColumn(Head("Restante", Theme.Good)).NoWrap().RightAligned());

            var models = state.Models;
            tokens.Caption($"[{Theme.Unit}]precos: {Markup.Escape(models.Source)}"
                + (models.UpdatedAt > DateTime.MinValue ? $" ({models.UpdatedAt:dd/MM HH:mm})" : "") + "[/]");

            foreach (var m in models.Models)
            {
                var name = $"[{Theme.Copilot}]{Markup.Escape(m.Name)}[/]";
                if (showSpent) tokens.AddRow(name, TokenPair(snap.Used, m), TokenPair(snap.Remaining, m));
                else tokens.AddRow(name, TokenPair(snap.Remaining, m));
            }

            content.Add(tokens);
        }
        else if (!snap.TokenBasedBilling)
        {
            content.Add(new Markup($"[{Theme.Unit}]cobranca legada por requisicao - sem conversao direta para tokens[/]"));
        }

        return Styled(new Rows(content), title, Theme.Copilot);
    }

    // Jornada de primeiro uso: conectar a conta do GitHub sem sair da TUI.
    static IRenderable LoginContent(CopilotState state, AuthState auth)
    {
        var lines = new List<IRenderable>();
        switch (auth.Step)
        {
            case AuthStep.InstallingGh:
            case AuthStep.Starting:
                lines.Add(new Markup($"[{Theme.Warn}]{Markup.Escape(auth.Message ?? "aguarde...")}[/]"));
                break;

            case AuthStep.WaitingUser:
                lines.Add(new Markup($"[bold]1.[/] [{Theme.Label}]Acesse[/] [bold {Theme.Input} underline]{GitHubAuth.DeviceUrl}[/] [{Theme.Unit}](aberto no navegador)[/]"));
                lines.Add(new Markup($"[bold]2.[/] [{Theme.Label}]Digite o codigo[/] [{Theme.Unit}](ja copiado)[/]:"));
                lines.Add(new Panel(new Markup($"[bold {Theme.Spent}]{auth.Code}[/]"))
                    .Border(BoxBorder.Double).BorderStyle(Style.Parse(Theme.Spent)).Padding(3, 0, 3, 0));
                lines.Add(new Markup($"[bold]3.[/] [{Theme.Label}]Autorize o acesso e volte para ca[/]"));
                lines.Add(new Markup($"[{Theme.Warn}]aguardando autorizacao...[/]"));
                break;

            case AuthStep.Done:
                lines.Add(new Markup($"[bold {Theme.Good}]conectado![/] [{Theme.Warn}]carregando dados do Copilot...[/]"));
                break;

            default:
                if (auth.Step == AuthStep.Failed)
                    lines.Add(new Markup($"[bold {Theme.Bad}]Falha no login:[/] {Markup.Escape(auth.Message ?? "")}"));
                else if (state.Error is { } err)
                    lines.Add(new Markup($"[{Theme.Warn}]{Markup.Escape(err)}[/]"));
                lines.Add(new Markup($"[{Theme.Label}]Conecte sua conta do GitHub para ver o plano, os creditos e o uso do Copilot.[/]"));
                lines.Add(new Markup($"Pressione {Key("L")} [bold]para entrar[/] [{Theme.Unit}](instala o GitHub CLI se necessario)[/]"));
                break;
        }
        return new Rows(lines);
    }

    static string PlanName(string plan) => plan.ToLowerInvariant() switch
    {
        "free" or "individual_free" => "Copilot Free",
        "individual" or "individual_pro" or "pro" => "Copilot Pro",
        "individual_pro_plus" or "pro_plus" => "Copilot Pro+",
        "individual_max" or "max" => "Copilot Max",
        "business" => "Copilot Business",
        "enterprise" => "Copilot Enterprise",
        _ => plan,
    };

    // creditos -> US$ -> tokens, usando o preco por 1M tokens do modelo
    static string TokenPair(double credits, ModelPrice m)
    {
        var usd = credits * CopilotUsage.UsdPerCredit;
        return $"[{Theme.Input}]{Tokens(usd / m.InputPerM * 1_000_000)}[/] [{Theme.Unit}]/[/] [{Theme.Output}]{Tokens(usd / m.OutputPerM * 1_000_000)}[/]";
    }

    static string Tokens(double n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000:F1}B",
        >= 1_000_000 => $"{n / 1_000_000:F1}M",
        >= 1_000 => $"{n / 1_000:F0}k",
        _ => $"{n:F0}",
    };

    static string Int(double v) => Math.Floor(v).ToString("N0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', '.');

    // ------------------------------------------------------------- Helpers

    // Largura da barra = largura da coluna menos o espaco ocupado pelo resto da linha, limitada a [4, max].
    static int BarWidth(int colWidth, int reserved, int max) => Math.Clamp(colWidth - reserved, 4, max);

    static Panel Unavailable(string title, string color) =>
        Styled(new Markup($"[{Theme.Unit}]nao detectado[/]"), title, color);

    // Painel com borda e titulo na cor de destaque da secao.
    static Panel Styled(IRenderable content, string title, string color, string? subtitle = null) =>
        new Panel(content)
            .Header(subtitle is null
                ? $"[bold {color}] {title} [/]"
                : $"[bold {color}] {title} [/][{Theme.Unit}]{Markup.Escape(subtitle)} [/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse(color))
            .Expand();

    static string Label(string text) => $"[{Theme.Label}]{Markup.Escape(text)}[/]";

    static string Head(string text, string color) => $"[bold {color}]{text}[/]";

    static string Key(string key) => $"[bold black on {Theme.KeyBg}] {key} [/]";

    static string Value(string number, string unit, string color = Theme.Value) =>
        unit.Length == 0 ? $"[bold {color}]{number}[/] " : $"[bold {color}]{number}[/] [{Theme.Unit}]{unit}[/]";

    static readonly string Dash = $"[{Theme.Unit}]-[/]";

    static IEnumerable<ISensor> Sensors(IHardware hw, SensorType type)
    {
        foreach (var s in hw.Sensors.Where(s => s.SensorType == type)) yield return s;
        foreach (var sub in hw.SubHardware)
            foreach (var s in Sensors(sub, type)) yield return s;
    }

    static int CoreIndex(string name)
    {
        var digits = new string(name.SkipWhile(c => c != '#').Skip(1).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var i) ? i : 0;
    }

    static string TempMarkup(float? v)
    {
        if (v is null) return Dash;
        var color = v >= 85 ? Theme.Bad : v >= 70 ? Theme.Warn : v >= 45 ? Theme.Good : Theme.Cool;
        return $"[bold {color}]{v:F0}[/] [{Theme.Unit}]{(Unicode ? "\u00b0C" : "C")}[/]";
    }

    // Barra com gradiente: cada celula preenchida recebe a cor da faixa em que esta.
    static string LoadBar(float? v, int width)
    {
        if (v is null) return Dash;
        var pct = Math.Clamp(v.Value, 0, 100);
        var filled = (int)Math.Round(pct / 100 * width);
        var (full, empty) = Unicode ? ('\u2588', '\u2591') : ('#', '.');

        var sb = new System.Text.StringBuilder();
        string? current = null;
        for (var i = 0; i < filled; i++)
        {
            var color = LoadColor((i + 0.5f) / width * 100);
            if (color != current)
            {
                if (current is not null) sb.Append("[/]");
                sb.Append('[').Append(color).Append(']');
                current = color;
            }
            sb.Append(full);
        }
        if (current is not null) sb.Append("[/]");
        if (filled < width) sb.Append($"[{Theme.BarEmpty}]{new string(empty, width - filled)}[/]");

        return $"{sb} [bold {LoadColor(pct)}]{pct,3:F0}%[/]";
    }

    static string LoadColor(float pct) =>
        pct >= 85 ? Theme.Bad : pct >= 70 ? Theme.Hot : pct >= 50 ? Theme.Warn : Theme.Good;

    static bool Unicode => AnsiConsole.Profile.Capabilities.Unicode;
}

// Paleta da TUI (nomes de cor do Spectre.Console).
static class Theme
{
    public const string Cpu = "deepskyblue1";
    public const string Memory = "mediumpurple1";
    public const string Gpu = "springgreen2";
    public const string Mobo = "orange1";
    public const string Storage = "gold1";
    public const string Copilot = "orchid";

    public const string Title = "deepskyblue1";
    public const string Rule = "grey35";
    public const string KeyBg = "grey70";
    public const string TableBorder = "grey35";

    public const string Label = "grey70";
    public const string Unit = "grey50";
    public const string Value = "white";
    public const string Clock = "aqua";
    public const string Fan = "turquoise2";
    public const string Money = "khaki1";
    public const string Spent = "yellow";
    public const string Input = "skyblue1";
    public const string Output = "plum1";

    public const string Cool = "deepskyblue1";
    public const string Good = "green3";
    public const string Warn = "yellow";
    public const string Hot = "darkorange";
    public const string Bad = "red1";
    public const string BarEmpty = "grey23";
}
