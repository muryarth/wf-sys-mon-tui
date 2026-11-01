using LibreHardwareMonitor.Hardware;
using Spectre.Console;
using Spectre.Console.Rendering;
using static WfSysMon.L;

namespace WfSysMon;

// Conteudo dos paineis. A posicao deles na tela e decidida em TuiApp.
static class Dashboard
{
    const int LabelWidth = 13;

    public static void UpdateSensors(Computer computer)
    {
        foreach (var hw in computer.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
        }
        SensorDisplay.Update(computer.Hardware);
    }

    // Temperaturas gerais em destaque no topo da tela, sempre visiveis.
    public static string HeaderTemps(IEnumerable<IHardware> hardware)
    {
        var parts = new List<string>();
        var cpu = hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        var gpu = hardware.FirstOrDefault(IsGpu);
        if (cpu is not null) parts.Add($"[bold {Theme.Cpu}]CPU[/] {TempBadge(SensorDisplay.Get(CpuTemp(cpu)))}");
        if (gpu is not null) parts.Add($"[bold {Theme.Gpu}]GPU[/] {TempBadge(SensorDisplay.Get(GpuTemp(gpu)))}");
        return string.Join("  ", parts);
    }

    static bool IsGpu(IHardware h) => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    static ISensor? CpuTemp(IHardware cpu) =>
        Sensors(cpu, SensorType.Temperature).FirstOrDefault(s => s.Name.Contains("Package") || s.Name.Contains("Tctl") || s.Name.Contains("Tdie"));

    static ISensor? GpuTemp(IHardware gpu) =>
        Sensors(gpu, SensorType.Temperature).FirstOrDefault(s => s.Name.Contains("Core"));

    // Ainda chegando dados: indicador animado, em vez de parecer que algo falta.
    public static string Loading(string text) =>
        $"[{Theme.Clock}]{Markup.Escape(StartupUi.SpinnerFrame(DateTime.Now).ToString())}[/] [{Theme.Unit}]{Markup.Escape(text)}[/]";

    // Estrutura do painel enquanto os dados nao chegam: rotulos reais e valores apagados, para os
    // numeros so preencherem o que ja esta na tela.
    static string Faded => $"[{Theme.BarEmpty}]···[/]";

    static string FadedBar(int colWidth) => $"[{Theme.BarEmpty}]{new string(Unicode ? '\u2500' : '-', ValueBar(colWidth))}[/]";

    static string FadedUsage() =>
        $"{Faded}   [{Theme.Unit}]{T("Disponível", "Free")}[/] {Faded}   [{Theme.Unit}]Total[/] {Faded}";

    static string Loading() => Loading(T("carregando", "loading"));

    static Panel CpuSkeleton(int colWidth)
    {
        var summary = Kv();
        summary.AddRow(Label(T("Temperatura", "Temperature")), Loading());
        summary.AddRow(Label(T("Uso total", "Total load")), FadedBar(colWidth));

        var cores = new Table().Expand().Border(TableBorder.None)
            .AddColumn(new TableColumn(Head(T("Núcleo", "Core"), Theme.Unit)).Width(6).NoWrap())
            .AddColumn(new TableColumn(Head("Temp", Theme.Unit)).Width(7).NoWrap().RightAligned())
            .AddColumn(new TableColumn(Head(T("Uso", "Load"), Theme.Unit)).NoWrap())
            .AddColumn(new TableColumn(Head("Clock", Theme.Unit)).Width(8).NoWrap().RightAligned());
        var bar = $"[{Theme.BarEmpty}]{new string(Unicode ? '\u2500' : '-', BarWidth(colWidth, 39, int.MaxValue))}[/]";

        // Duas threads por nucleo e a estimativa comum; a tabela real ajusta o numero exato.
        for (var i = 1; i <= Math.Max(Environment.ProcessorCount / 2, 1); i++)
            cores.AddRow($"[{Theme.Unit}]{i}[/]", Faded, bar, Faded);

        return Styled(new Rows(summary, new Text(""), cores), "CPU", Theme.Cpu, Icons.Cpu);
    }

    static Panel MemorySkeleton(int colWidth)
    {
        var t = Kv();
        t.AddRow(Label(T("Uso", "Load")), Loading());
        t.AddRow(Label(T("Em uso", "Used")), FadedUsage());
        return Styled(t, T("Memória RAM", "RAM"), Theme.Memory, Icons.Ram);
    }

    static Panel GpuSkeleton(int colWidth)
    {
        var t = Kv();
        t.AddRow(Label(T("Temperatura", "Temperature")), Loading());
        t.AddRow(Label(T("Uso", "Load")), FadedBar(colWidth));
        t.AddRow(Label(T("Ventoinha", "Fan")), $"{Faded}   [{Theme.Unit}]{T("Potência", "Power")}[/] {Faded}");
        t.AddEmptyRow();
        t.AddRow(Label("VRAM"), FadedBar(colWidth));
        t.AddRow(Label(T("Em uso", "Used")), FadedUsage());
        return Styled(t, "GPU", Theme.Gpu, Icons.Gpu);
    }

    static Panel StorageSkeleton(int colWidth)
    {
        var t = Kv();
        t.AddRow(Label(T("Disco", "Disk")), Loading());
        t.AddRow(Label(T("Espaço", "Space")), FadedBar(colWidth));
        t.AddRow(Label(T("Em uso", "Used")), FadedUsage());
        return Styled(t, T("Armazenamento", "Storage"), Theme.Storage, Icons.Disk);
    }

    static Panel BoardSkeleton()
    {
        var t = Kv();
        t.AddRow(Label(T("Temperatura", "Temperature")), Loading());
        t.AddRow(Label(T("Ventoinha", "Fan")), Faded);
        return Styled(t, T("Placa-mãe", "Motherboard"), Theme.Mobo, Icons.Board);
    }

    static Panel CopilotSkeleton(int colWidth)
    {
        var t = Kv();
        t.AddRow(Label(T("Plano", "Plan")), Loading());
        t.AddRow(Label(T("Consumo", "Usage")), FadedBar(colWidth));
        t.AddRow(Label(T("Cota plano", "Plan quota")), Faded);
        t.AddRow(Label("Extra budget"), Faded);
        t.AddRow(Label(T("Restante", "Left")), Faded);
        t.AddEmptyRow();
        t.AddRow(Label("Tokens"), Faded);
        t.AddRow(Label(T("Atualizado", "Updated")), Faded);
        return Styled(t, "GitHub Copilot", Theme.Copilot, Icons.Copilot);
    }

    // Algum painel ainda esta esperando dados: o indicador precisa continuar girando.
    public static bool IsLoading(IReadOnlyList<IHardware> hardware, CopilotState copilot, ClaudeState claude) =>
        hardware.Count == 0
        || (Settings.Current.ShowCopilot && copilot.Snapshot is null && !copilot.NeedsLogin && copilot.Error is null)
        || (Settings.Current.ShowClaude && (!claude.AccountChecked || claude.Month is null));

    public static List<Panel> HardwarePanels(IReadOnlyList<IHardware> hardware, int colWidth)
    {
        if (hardware.Count == 0)
        {
            return [CpuSkeleton(colWidth), MemorySkeleton(colWidth), GpuSkeleton(colWidth), StorageSkeleton(colWidth), BoardSkeleton()];
        }

        var cpu = hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);

        // A biblioteca separa a memoria em "Total Memory" e "Virtual Memory"; o painel e da RAM fisica.
        var memories = hardware.Where(h => h.HardwareType == HardwareType.Memory).ToList();
        var mem = memories.FirstOrDefault(h => h.Name.Contains("Total"))
                  ?? memories.FirstOrDefault(h => !h.Name.Contains("Virtual"));
        var mobo = hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
        var disks = hardware.Where(h => h.HardwareType == HardwareType.Storage).ToList();

        var panels = new List<Panel>
        {
            cpu is null ? Missing("CPU", Theme.Cpu, Icons.Cpu) : CpuPanel(cpu, colWidth),
            mem is null ? Missing(T("Memória RAM", "RAM"), Theme.Memory, Icons.Ram) : MemoryPanel(mem, colWidth),
        };
        panels.AddRange(hardware.Where(IsGpu).Select(g => GpuPanel(g, colWidth)));
        panels.Add(disks.Count == 0 ? Missing(T("Armazenamento", "Storage"), Theme.Storage, Icons.Disk) : StoragePanel(disks, colWidth));
        panels.Add(mobo is null ? Missing(T("Placa-mãe", "Motherboard"), Theme.Mobo, Icons.Board) : MotherboardPanel(mobo));
        return panels;
    }

    public static List<Panel> AiPanels(CopilotState copilot, ClaudeState claude, AuthState auth, int colWidth) =>
        [
            .. Settings.Current.ShowCopilot ? [CopilotPanel(copilot, auth, colWidth)] : Array.Empty<Panel>(),
            .. Settings.Current.ShowClaude ? [ClaudePanel(claude, colWidth)] : Array.Empty<Panel>(),
        ];

    // ---------------------------------------------------------------- CPU

    static Panel CpuPanel(IHardware cpu, int colWidth)
    {
        var temps = Sensors(cpu, SensorType.Temperature);
        var loads = Sensors(cpu, SensorType.Load);
        var clocks = Sensors(cpu, SensorType.Clock);
        var power = Sensors(cpu, SensorType.Power).FirstOrDefault(s => s.Name.Contains("Package"));
        var totalLoad = loads.FirstOrDefault(s => s.Name.Contains("Total"));

        var summary = Kv();
        var powerText = SensorDisplay.Get(power) is { } w ? $"   [{Theme.Label}]{T("Potência", "Power")}[/] {Value($"{w:F1}", "W")}" : "";
        summary.AddRow(Label(T("Temperatura", "Temperature")), MainTemp(CpuTemp(cpu)) + powerText);
        summary.AddRow(Label(T("Uso total", "Total load")), SensorBar(totalLoad, ValueBar(colWidth)));

        // Min/Max por nucleo so saem em colunas muito estreitas, onde a barra nao caberia.
        var showRange = colWidth >= 60;
        var cores = new Table()
            .Expand()
            .Border(TableBorder.None)
            .AddColumn(new TableColumn(Head(T("Núcleo", "Core"), Theme.Unit)).Width(6).NoWrap())
            .AddColumn(new TableColumn(Head("Temp", Theme.Unit)).Width(7).NoWrap().RightAligned());
        if (showRange)
        {
            cores.AddColumn(new TableColumn(Head("Min", Theme.Unit)).Width(5).NoWrap().RightAligned());
            cores.AddColumn(new TableColumn(Head("Max", Theme.Unit)).Width(5).NoWrap().RightAligned());
        }
        cores.AddColumn(new TableColumn(Head(T("Uso", "Load"), Theme.Unit)).NoWrap())
            .AddColumn(new TableColumn(Head("Clock", Theme.Unit)).Width(8).NoWrap().RightAligned());

        var coreTemps = temps
            .Where(s => s.Name.StartsWith("CPU Core #") && !s.Name.Contains("Distance"))
            .OrderBy(s => CoreIndex(s.Name));

        foreach (var t in coreTemps)
        {
            var idx = CoreIndex(t.Name);
            var load = loads.FirstOrDefault(s => s.Name == $"CPU Core #{idx}")
                    ?? loads.FirstOrDefault(s => s.Name.StartsWith($"CPU Core #{idx} "));
            var clock = clocks.FirstOrDefault(s => s.Name == $"CPU Core #{idx}");

            var row = new List<string> { $"[{Theme.Unit}]{idx}[/]", TempMarkup(SensorDisplay.Get(t)) };
            if (showRange)
            {
                row.Add(TempNumber(SensorDisplay.Min(t)));
                row.Add(TempNumber(t.Max));
            }
            row.Add(SensorBar(load, BarWidth(colWidth, showRange ? 53 : 39, int.MaxValue)));
            row.Add(SensorDisplay.Get(clock) is { } c ? Value($"{c:F0}", "MHz", Theme.Clock) : Dash);
            cores.AddRow(row.ToArray());
        }

        return Styled(new Rows(summary, new Text(""), cores), "CPU", Theme.Cpu, Icons.Cpu, cpu.Name);
    }

    // ------------------------------------------------------------- Memoria

    static Panel MemoryPanel(IHardware mem, int colWidth)
    {
        var used = Sensors(mem, SensorType.Data).FirstOrDefault(s => s.Name == "Memory Used")?.Value;
        var avail = Sensors(mem, SensorType.Data).FirstOrDefault(s => s.Name == "Memory Available")?.Value;
        var pct = Sensors(mem, SensorType.Load).FirstOrDefault(s => s.Name == "Memory");

        var t = Kv();
        t.AddRow(Label(T("Uso", "Load")), SensorBar(pct, ValueBar(colWidth)));
        AddUsage(t, used, avail, used + avail, SensorDisplay.Get(pct), colWidth);

        return Styled(t, T("Memória RAM", "RAM"), Theme.Memory, Icons.Ram);
    }

    // Em uso / Disponivel / Total, sempre nesta ordem e nas mesmas cores: o uso segue o alerta da barra.
    static void AddUsage(Table t, double? used, double? available, double? total, float? pct, int colWidth)
    {
        var color = pct is { } p ? LoadColor(p) : Theme.Value;
        var usedText = used is { } u ? $"[bold {color}]{Gb(u)}[/]" : Dash;
        var freeText = available is { } a ? $"[bold {Theme.Good}]{Gb(a)}[/]" : Dash;
        var totalText = total is { } tt ? $"[bold {Theme.Value}]{Gb(tt)}[/]" : Dash;

        if (colWidth >= 62)
        {
            t.AddRow(Label(T("Em uso", "Used")), $"{usedText}   [{Theme.Label}]{T("Disponível", "Free")}[/] {freeText}   [{Theme.Label}]Total[/] {totalText}");
        }
        else
        {
            t.AddRow(Label(T("Em uso", "Used")), usedText);
            t.AddRow(Label(T("Disponível", "Free")), freeText);
            t.AddRow(Label("Total"), totalText);
        }
    }

    static string Gb(double v) => v >= 100 ? $"{v:F0} GB" : $"{v:F1} GB";

    // ----------------------------------------------------------------- GPU

    static Panel GpuPanel(IHardware gpu, int colWidth)
    {
        var load = Sensors(gpu, SensorType.Load).FirstOrDefault(s => s.Name.Contains("Core"));
        var fan = SensorDisplay.Get(Sensors(gpu, SensorType.Fan).FirstOrDefault());
        var power = SensorDisplay.Get(Sensors(gpu, SensorType.Power).FirstOrDefault());

        var t = Kv();
        t.AddRow(Label(T("Temperatura", "Temperature")), MainTemp(GpuTemp(gpu)));
        t.AddRow(Label(T("Uso", "Load")), SensorBar(load, ValueBar(colWidth)));
        if (fan is { } f && power is { } both)
            t.AddRow(Label(T("Ventoinha", "Fan")), $"{Value($"{f:F0}", "RPM", Theme.Fan)}   [{Theme.Label}]{T("Potência", "Power")}[/] {Value($"{both:F0}", "W")}");
        else if (fan is { } onlyFan) t.AddRow(Label(T("Ventoinha", "Fan")), Value($"{onlyFan:F0}", "RPM", Theme.Fan));
        else if (power is { } onlyPower) t.AddRow(Label(T("Potência", "Power")), Value($"{onlyPower:F0}", "W"));

        // A memoria dedicada real: "D3D Dedicated Memory Used" nao e o total em uso da placa.
        var memory = Sensors(gpu, SensorType.SmallData).ToList();
        var used = VramSensor(memory, "Used")?.Value;
        var free = VramSensor(memory, "Free")?.Value;
        var total = VramSensor(memory, "Total")?.Value ?? used + free;
        free ??= total - used;

        if (used is { } u && total is { } tt && tt > 0)
        {
            var pct = u / tt * 100;
            t.AddEmptyRow();
            t.AddRow(Label("VRAM"), LoadBar(pct, ValueBar(colWidth)));
            AddUsage(t, u / 1024, free / 1024, tt / 1024, pct, colWidth);
        }

        return Styled(t, "GPU", Theme.Gpu, Icons.Gpu, gpu.Name);
    }

    static ISensor? VramSensor(List<ISensor> memory, string kind) =>
        memory.FirstOrDefault(s => s.Name == $"GPU Memory {kind}")
        ?? memory.FirstOrDefault(s => s.Name.Contains($"Memory {kind}") && !s.Name.StartsWith("D3D"));

    // ----------------------------------------------------------- Placa-mae

    static Panel MotherboardPanel(IHardware mobo)
    {
        var temps = Sensors(mobo, SensorType.Temperature).Where(s => s.Value > 0).Take(4).ToList();
        var fans = Sensors(mobo, SensorType.Fan).Where(s => s.Value > 0).ToList();

        var t = new Table().Border(TableBorder.None).HideHeaders().AddColumn(new TableColumn("k").Width(18).NoWrap()).AddColumn("v");
        foreach (var s in temps) t.AddRow(Label(s.Name), $"{TempMarkup(SensorDisplay.Get(s))}  {TempRange(s)}");
        foreach (var s in fans) t.AddRow(Label(s.Name), Value($"{SensorDisplay.Get(s):F0}", "RPM", Theme.Fan));
        if (t.Rows.Count == 0) t.AddRow($"[{Theme.Unit}]{T("sem sensores", "no sensors")}[/]", "");

        return Styled(t, T("Placa-mãe", "Motherboard"), Theme.Mobo, Icons.Board, mobo.Name);
    }

    // ------------------------------------------------------------- Storage

    static Panel StoragePanel(List<IHardware> disks, int colWidth)
    {
        var t = Kv();
        var first = true;
        foreach (var d in disks)
        {
            if (!first) t.AddEmptyRow();
            first = false;

            var temp = Sensors(d, SensorType.Temperature).FirstOrDefault();
            var usedSensor = Sensors(d, SensorType.Load).FirstOrDefault(s => s.Name == "Used Space");
            var space = DiskVolumes.Find(d.Name);

            var diskTemp = $"{TempMarkup(SensorDisplay.Get(temp))}  {TempRange(temp)}";
            if (colWidth >= 76)
            {
                t.AddRow(Label(T("Disco", "Disk")), $"[bold {Theme.Value}]{Markup.Escape(d.Name)}[/]   {diskTemp}");
            }
            else
            {
                t.AddRow(Label(T("Disco", "Disk")), $"[bold {Theme.Value}]{Markup.Escape(d.Name)}[/]");
                t.AddRow(Label("Temp"), diskTemp);
            }

            if (space is not null)
            {
                var pct = (float)(space.UsedGb / space.TotalGb * 100);
                t.AddRow(Label(T("Espaço", "Space")), LoadBar(pct, ValueBar(colWidth)));
                AddUsage(t, space.UsedGb, space.FreeGb, space.TotalGb, pct, colWidth);
            }
            else if (usedSensor is not null)
            {
                t.AddRow(Label(T("Espaço", "Space")), SensorBar(usedSensor, ValueBar(colWidth)));
            }
            else
            {
                t.AddRow(Label(T("Espaço", "Space")), $"[{Theme.Unit}]{T("sem volume", "no volume")}[/]");
            }
        }
        return Styled(t, T("Armazenamento", "Storage"), Theme.Storage, Icons.Disk);
    }

    // ------------------------------------------------------------- Copilot

    static Panel CopilotPanel(CopilotState state, AuthState auth, int colWidth)
    {
        const string title = "GitHub Copilot";
        var snap = state.Snapshot;

        var authBusy = auth.Step is AuthStep.InstallingGh or AuthStep.Starting or AuthStep.WaitingUser;
        if (state.NeedsLogin || authBusy)
            return Styled(new Markup(NeedsAccount(T("Não conectado", "Not connected"))), title, Theme.Copilot, Icons.Copilot);

        if (snap is null)
        {
            return state.Error is { } err
                ? Styled(new Markup($"[bold {Theme.Bad}]{T("Erro", "Error")}:[/] {Markup.Escape(err)}"), title, Theme.Copilot, Icons.Copilot)
                : CopilotSkeleton(colWidth);
        }

        var usdPerUnit = snap.TokenBasedBilling ? CopilotUsage.UsdPerCredit : CopilotUsage.UsdPerPremiumRequest;
        var total = snap.Entitlement + snap.ExtraBudget;  // cota do plano + extra budget
        var remaining = Math.Max(0, total - snap.Used);   // a API devolve saldo negativo quando usa o extra
        var usedPct = total > 0 ? (float)Math.Clamp(snap.Used / total * 100, 0, 100) : 0f;
        var band = BudgetColor(usedPct);

        var t = Kv();
        t.AddRow(Label(T("Plano", "Plan")), PlanRow(PlanName(snap.Plan), Theme.Copilot));

        if (snap.Unlimited)
        {
            t.AddRow(Label(T("Uso", "Usage")), $"[bold {Theme.Good}]{T("ilimitado", "unlimited")}[/]");
        }
        else
        {
            t.AddRow(Label(T("Consumo", "Usage")), LoadBar(usedPct, ValueBar(colWidth)));
            // O valor em AIC fica na ponta direita de cada categoria; cobranca legada nao usa creditos.
            string Row(double used, double limit, bool showLimit = true)
            {
                var money = $"[{band}]US$ {used * usdPerUnit:F2}[/]";
                if (showLimit) money += $" [{Theme.Unit}]/ {limit * usdPerUnit:F2}[/]";
                if (!snap.TokenBasedBilling) return money;

                var credits = showLimit ? $"{Credits(used)} / {Credits(limit)} AIC" : $"{Credits(used)} AIC";
                return WithRightBadge(money, $"[bold black on {band}] {credits} [/]", colWidth);
            }

            var extraUsed = Math.Max(0, snap.Used - snap.Entitlement);
            var planUsed = snap.Used - extraUsed;
            t.AddRow(Label(T("Cota plano", "Plan quota")), Row(planUsed, snap.Entitlement));
            if (snap.ExtraBudget > 0)
                t.AddRow(Label("Extra budget"), Row(extraUsed, snap.ExtraBudget));
            else if (extraUsed > 0)
                t.AddRow(Label("Extra budget"), Row(extraUsed, 0, showLimit: false));
            t.AddRow(Label(T("Restante", "Left")), $"[bold {band}]US$ {remaining * usdPerUnit:F2}[/]");
        }

        // Ciclo que ja fechou: em cor neutra, porque nao e um alerta do momento.
        if (state.LastCycle is { } last)
        {
            var lastTotal = last.Entitlement + last.ExtraBudget;
            var lastUnit = last.TokenBasedBilling ? CopilotUsage.UsdPerCredit : CopilotUsage.UsdPerPremiumRequest;
            var lastMoney = $"[{Theme.Value}]US$ {last.Used * lastUnit:F2}[/] [{Theme.Unit}]/ {lastTotal * lastUnit:F2}[/]";
            t.AddRow(Label(T("Último ciclo", "Last cycle")), last.TokenBasedBilling
                ? WithRightBadge(lastMoney, $"[bold black on {Theme.KeyBg}] {Credits(last.Used)} / {Credits(lastTotal)} AIC [/]", colWidth)
                : lastMoney);
        }

        t.AddEmptyRow();
        if (state.Local is { Total: > 0 } local)
        {
            var share = snap.Used > 0 ? Math.Min(100, local.Credits / snap.Used * 100) : 0;
            t.AddRow(Label("Tokens"), $"[bold]{Tokens(local.Total)}[/]   [{Theme.Unit}]CLI, {share:F0}% {T("do gasto", "of spend")}[/]");
            t.AddRow("", TokenBreakdown(local.Input, local.Output, local.CacheRead + local.CacheWrite));
        }
        else
        {
            t.AddRow(Label("Tokens"), Unavailable());
        }

        var status = state.Loading ? $"[{Theme.Warn}]{T("atualizando...", "updating...")}[/]" : $"[{Theme.Clock}]{snap.FetchedAt:HH:mm:ss}[/]";
        if (state.Error is { } e) status += $" [{Theme.Bad}]({Markup.Escape(e)})[/]";
        if (snap.ResetDate is { } reset)
        {
            var days = Math.Max(0, (int)Math.Ceiling((reset - DateTime.UtcNow).TotalDays));
            var daysColor = days <= 3 ? Theme.Warn : Theme.Unit;
            status += $"   [{Theme.Label}]{T("Renova", "Renews")}[/] [bold]{Day(reset)}[/] [{daysColor}]{T("em", "in")} {days}d[/]";
        }
        t.AddRow(Label(T("Atualizado", "Updated")), status);

        return Styled(t, title, Theme.Copilot, Icons.Copilot);
    }

    // Passos do login no GitHub, mostrados na tela de contas.
    public static List<string> LoginSteps(CopilotState state, AuthState auth)
    {
        switch (auth.Step)
        {
            case AuthStep.InstallingGh:
            case AuthStep.Starting:
                return [$"[{Theme.Warn}]{Markup.Escape(auth.Message ?? T("aguarde...", "please wait..."))}[/]"];

            case AuthStep.WaitingUser:
                return
                [
                    $"[bold]1.[/] [{Theme.Label}]{T("Acesse", "Open")}[/] [bold {Theme.Input} underline]{GitHubAuth.DeviceUrl}[/] [{Theme.Unit}]({T("aberto no navegador", "opened in the browser")})[/]",
                    $"[bold]2.[/] [{Theme.Label}]{T("Digite o código", "Enter the code")}:[/] [bold black on {Theme.Spent}] {auth.Code} [/]",
                    $"[bold]3.[/] [{Theme.Label}]{T("Autorize o acesso e volte para cá", "Authorize access and come back here")}[/]",
                    $"[{Theme.Warn}]{T("aguardando autorização...", "waiting for authorization...")}[/]",
                ];

            case AuthStep.Done when state.Snapshot is null && state.Error is null:
                return [$"[bold {Theme.Good}]{T("conectado!", "connected!")}[/] [{Theme.Warn}]{T("carregando dados do Copilot...", "loading Copilot data...")}[/]"];

            case AuthStep.Failed:
                return [$"[bold {Theme.Bad}]{T("Falha no login", "Sign-in failed")}:[/] {Markup.Escape(auth.Message ?? "")}"];

            default:
                // "Nao conectado" ja aparece na linha da conta; aqui so o que for novidade, como sessao expirada.
                return state.NeedsLogin && state.Error is { } err && err != CopilotUsage.NotConnectedMessage
                    ? [$"[{Theme.Warn}]{Markup.Escape(err)}[/]"]
                    : [];
        }
    }

    static string NeedsAccount(string text) =>
        $"[{Theme.Unit}]{Markup.Escape(text)}[/]  {Key("A")}";

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

    // -------------------------------------------------------------- Claude

    // Cada bloco indica a origem: a cota vem da conta (via Claude Code); os tokens, dos logs deste PC.
    internal static Panel ClaudePanel(ClaudeState state, int colWidth)
    {
        if (state.LoggedIn == false)
            return Styled(new Markup(NeedsAccount(T("Não conectado", "Not connected"))), "Claude", Theme.Claude, Icons.Claude);

        var t = Kv();
        t.AddRow(Label(T("Plano", "Plan")), state.Plan is { } plan ? PlanRow(plan, Theme.Claude)
            : state.AccountChecked ? Unavailable()
            : Loading(T("carregando", "loading")));

        if (state.FiveHour is null && state.SevenDay is null && state.Month is null)
        {
            t.AddRow(Label(T("Cota 5h", "5h quota")), FadedBar(colWidth));
            t.AddRow(Label(T("Cota 7 dias", "7-day quota")), FadedBar(colWidth));
        }
        else if (state.FiveHour is null && state.SevenDay is null)
        {
            t.AddRow(Label(T("Cota", "Quota")), state.HasQuotaFiles
                ? $"[{Theme.Warn}]{T("sem dado recente", "no recent data")}[/]"
                : $"[{Theme.Warn}]{T("sem dados", "no data")}[/]  {Key("S")}");
        }
        else
        {
            AddWindow(t, T("Cota 5h", "5h quota"), state.FiveHour, colWidth, state.Month is null);
            AddWindow(t, T("Cota 7 dias", "7-day quota"), state.SevenDay, colWidth, state.Month is null);
            if (state.QuotaCapturedAt is { } at)
            {
                var age = DateTime.Now - at;
                var color = age > TimeSpan.FromMinutes(10) ? Theme.Warn : Theme.Clock;
                t.AddRow(Label(T("Atualizado", "Updated")), $"[{color}]{T($"há {Elapsed(age)}", $"{Elapsed(age)} ago")}[/]"
                    + (state.CheckedAt is { } read ? $"   [{Theme.Unit}]{T("lido", "read")} {read:HH:mm:ss}[/]" : ""));
            }
        }

        t.AddEmptyRow();
        if (state.LogsError is { } error)
        {
            t.AddRow(Label(T("Este mês", "This month")), $"[{Theme.Bad}]{Markup.Escape(error)}[/]");
        }
        else if (state.Month is not { } month)
        {
            t.AddRow(Label(T("Este mês", "This month")), Loading(T("carregando", "loading")));
        }
        else
        {
            // Sem preco para algum modelo o total ficaria menor que o real: o "?" deixa isso visivel.
            var unpriced = state.MonthByModel.Any(m => ClaudePricing.Find(m.Model) is null);
            var usdText = !unpriced ? $"US$ {month.Usd:F2}" : month.Usd > 0 ? $"US$ {month.Usd:F2}+{Unavailable()}" : $"US$ {Unavailable()}";
            t.AddRow(Label(T("Este mês", "This month")), $"[bold]{Tokens(month.Total)}[/]   [bold {Theme.Spent}]~{usdText}[/]   [{Theme.Unit}]{T("neste PC", "this PC")}[/]");
            t.AddRow("", TokenBreakdown(month.Input, month.Output, month.CacheRead + month.CacheWrite));
        }

        return Styled(t, "Claude", Theme.Claude, Icons.Claude);
    }

    // Empurra o selo ate a ponta direita da coluna de valor, alinhado com o percentual das barras.
    static string WithRightBadge(string text, string badge, int colWidth)
    {
        var free = colWidth - 22 - Screen.CellWidth(Markup.Remove(text)) - Screen.CellWidth(Markup.Remove(badge));
        return text + new string(' ', Math.Max(free, 1)) + badge;
    }

    static string Credits(double v) =>
        Math.Floor(v).ToString("N0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', T(".", ",")[0]);

    // Nome do plano e preco mensal de lista.
    static string PlanRow(string plan, string color)
    {
        var perMonth = T("/mês", "/mo");
        var price = PlanPrices.Find(plan) switch
        {
            null => $"[{Theme.Money}]US$[/] {Unavailable()}[{Theme.Unit}]{perMonth}[/]",
            0 => $"[{Theme.Money}]{T("grátis", "free")}[/]",
            var p => $"[{Theme.Money}]US$ {p:0.##}[/][{Theme.Unit}]{perMonth}[/]",
        };
        return $"[bold {color}]{Markup.Escape(plan)}[/]  {price}";
    }

    static string TokenBreakdown(long input, long output, long cache) =>
        $"[{Theme.Input}]{Tokens(input)}[/] [{Theme.Unit}]{T("entrada", "input")} /[/] [{Theme.Output}]{Tokens(output)}[/] [{Theme.Unit}]{T("saída", "output")} /[/] [{Theme.Clock}]{Tokens(cache)}[/] [{Theme.Unit}]cache[/]";

    // Marca um valor que nao foi possivel obter.
    static string Unavailable() => $"[bold {Theme.Warn}]?[/]";

    static void AddWindow(Table t, string label, ClaudeWindow? window, int colWidth, bool logsLoading)
    {
        if (window is null)
        {
            t.AddRow(Label(label), $"[{Theme.Unit}]{T("sem dado recente", "no recent data")}[/]");
            return;
        }

        t.AddRow(Label(label), LoadBar((float)window.UsedPercent, ValueBar(colWidth)));
        t.AddRow("", WithRightBadge(
            $"[{Theme.Unit}]{T("renova em", "resets in")}[/] {Elapsed(window.ResetsAt - DateTime.Now)}",
            logsLoading ? Loading("tokens") : QuotaEstimate(window),
            colWidth));
    }

    // Quanto ainda cabe na janela, pela proporcao entre o percentual e o uso deste PC na mesma janela.
    // A Anthropic nao publica o limite em tokens. O percentual vem arredondado (+/- 0,5 ponto), entao
    // o resultado e uma faixa, que fica mais estreita conforme o uso cresce.
    internal static string QuotaEstimate(ClaudeWindow window)
    {
        var pct = window.UsedPercent;
        if (window.Used is not { Total: > 0 } used || pct < 1)
            return $"{Dash} [{Theme.Unit}]tokens[/]";  // janela recem-renovada: ainda nao ha base para estimar

        var left = Math.Max(0, 100 - pct);
        var low = left / (pct + 0.5);
        var high = left / (pct - 0.5);

        return $"[{Theme.Unit}]~[/][bold]{Tokens(used.Total * low)}-{Tokens(used.Total * high)}[/] [{Theme.Unit}]tokens[/]";
    }

    static string Day(DateTime d) => T($"{d:dd/MM}", $"{d:MM/dd}");

    static string Elapsed(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return "<1 min";
        if (t < TimeSpan.FromHours(1)) return $"{(int)t.TotalMinutes} min";
        if (t < TimeSpan.FromDays(1)) return $"{(int)t.TotalHours}h {t.Minutes:00}min";
        return $"{(int)t.TotalDays}d {t.Hours}h";
    }

    static string Tokens(double n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1_000_000_000:F1}B",
        >= 1_000_000 => $"{n / 1_000_000:F1}M",
        >= 1_000 => $"{n / 1_000:F0}k",
        _ => $"{n:F0}",
    };

    // ------------------------------------------------------------- Helpers

    // Tabela rotulo/valor com a coluna de rotulos alinhada em todos os paineis.
    static Table Kv() => new Table()
        .Border(TableBorder.None)
        .HideHeaders()
        .AddColumn(new TableColumn("k").Width(LabelWidth).NoWrap())
        .AddColumn(new TableColumn("v"));

    // Barra na coluna de valor de Kv: desconta bordas, rotulo, margens e o percentual escrito.
    static int ValueBar(int colWidth) => BarWidth(colWidth, 27, int.MaxValue);

    // Largura da barra = largura da coluna menos o espaco ocupado pelo resto da linha, limitada a [4, max].
    static int BarWidth(int colWidth, int reserved, int max) => Math.Clamp(colWidth - reserved, 4, Math.Max(max, 4));

    static Panel Missing(string title, string color, string icon) =>
        Styled(new Markup($"[{Theme.Unit}]{T("não detectado", "not detected")}[/]"), title, color, icon);

    // Painel discreto: a borda fica neutra e so o titulo leva a cor do componente.
    static Panel Styled(IRenderable content, string title, string color, string icon, string? subtitle = null) =>
        new Panel(content)
            .Header(subtitle is null
                ? $" [{color}]{Markup.Escape(icon)}[/][bold {color}]{Markup.Escape(title)}[/] "
                : $" [{color}]{Markup.Escape(icon)}[/][bold {color}]{Markup.Escape(title)}[/]  [{Theme.Unit}]{Markup.Escape(subtitle)}[/] ")
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse(Theme.PanelBorder))
            .Expand();

    static string Label(string text) => $"[{Theme.Label}]{Markup.Escape(text)}[/]";

    static string Head(string text, string color) => $"[{color}]{Markup.Escape(text)}[/]";

    internal static string Key(string key) => $"[bold black on {Theme.KeyBg}] {key} [/]";

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

    static string TempColor(float v) =>
        v >= 85 ? Theme.Bad : v >= 70 ? Theme.Warn : v >= 45 ? Theme.Good : Theme.Cool;

    static string DegreeUnit => Unicode ? "°C" : "C";

    static string TempMarkup(float? v) => TempBadge(v);

    // Temperatura sempre com fundo na cor da faixa, para ser lida de relance.
    static string TempBadge(float? v) =>
        v is { } t ? $"[bold black on {TempColor(t)}] {t:F0} {DegreeUnit} [/]" : Dash;

    static string MainTemp(ISensor? sensor) => $"{TempBadge(SensorDisplay.Get(sensor))}  {TempRange(sensor)}";

    // Menor e maior leitura desde que o app abriu (brutas, sem a media da tela).
    static string TempRange(ISensor? sensor) =>
        sensor is null ? "" : $"[{Theme.Unit}]min[/] {TempNumber(SensorDisplay.Min(sensor))} [{Theme.Unit}]max[/] {TempNumber(sensor.Max)}";

    static string TempNumber(float? v) =>
        v is { } t && t > 0 ? $"[{TempColor(t)}]{t:F0}{DegreeUnit}[/]" : Dash;

    static string SensorBar(ISensor? sensor, int width) =>
        LoadBar(SensorDisplay.Get(sensor), width, SensorDisplay.Bar(sensor));

    // Barra de uma cor so, a do alerta do valor atual. O percentual escrito e sempre v;
    // barValue permite a barra estar a caminho dele.
    static string LoadBar(float? v, int width, float? barValue = null)
    {
        if (v is null) return Dash;
        var pct = Math.Clamp(v.Value, 0, 100);
        var color = LoadColor(pct);
        var exact = Math.Clamp(barValue ?? v.Value, 0, 100) / 100 * width;
        var filled = Unicode ? (int)Math.Floor(exact) : (int)Math.Round(exact);

        // Meia celula (U+2578), para a barra deslizar com mais suavidade.
        var half = Unicode && filled < width && exact - filled >= 0.5;
        var (full, empty) = Unicode ? ('\u2501', '\u2500') : ('=', '-');

        var bar = new string(full, filled) + (half ? "\u2578" : "");
        var used = filled + (half ? 1 : 0);
        var rest = used < width ? $"[{Theme.BarEmpty}]{new string(empty, width - used)}[/]" : "";
        return $"[{color}]{bar}[/]{rest} [bold {color}]{pct,3:F0}%[/]";
    }

    // Faixas do orcamento, aplicadas aos valores: ate 1/3 verde, ate 2/3 laranja, acima disso vermelho.
    internal static string BudgetColor(float pct) =>
        pct < 100f / 3 ? Theme.Good : pct < 200f / 3 ? Theme.Hot : Theme.Bad;

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
    public const string Claude = "salmon1";

    public const string HardwareSection = "steelblue1";
    public const string AiSection = "mediumorchid1";

    public const string Title = "white";
    public const string PanelBorder = "grey30";
    public const string KeyBg = "grey70";
    public const string Selected = "grey23";
    public const string Highlight = "grey35";
    public const string TabActive = "dodgerblue2";
    public const string TabIdle = "grey19";

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
