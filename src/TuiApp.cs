using LibreHardwareMonitor.Hardware;
using Spectre.Console;
using Spectre.Console.Rendering;
using static WfSysMon.L;

namespace WfSysMon;

// De onde vem as leituras: do servico; do proprio app, como administrador; ou so o que nao exige privilegio.
enum SensorMode { Service, ServiceOutdated, Elevated, Limited }

// DriverInstalled: PawnIO, o driver que a biblioteca de sensores usa para o acesso de baixo nivel.
sealed record UiData(IReadOnlyList<IHardware> Hardware, CopilotState Copilot, ClaudeState Claude, AuthState Auth, SensorMode Mode, bool DriverInstalled = true, UpdateState? Update = null, bool CanInstallUpdate = false);

sealed record UiActions(
    Action Refresh,
    Action LoginGitHub,
    Func<bool> HasGitHubCli,
    Action LoginClaude,
    Action<Action<string?>> LogoutGitHub,   // recebe o resultado: null = sucesso, ou o motivo da falha
    Action<Action<string?>> LogoutClaude,
    Action CheckClaude,
    Action<Action<string?>> InstallDriver,
    Action<Action<string?>> InstallService,
    Action<Action<string?>> RemoveService,
    Action CheckUpdate,
    Action<bool> InstallUpdate,   // true: atualiza tambem o servico de sensores
    Action Quit);

sealed record HitArea(int X, int Y, int W, int H, Action Action);

sealed record ColumnView(string Key, int X, int Y, int W, int H, int ContentHeight);

sealed class Frame(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public string[] Rows { get; } = Enumerable.Repeat("", height).ToArray();
    public List<HitArea> Regions { get; } = [];
    public List<ColumnView> Columns { get; } = [];
}

// Estado e layout da tela. Nao toca no console: Build devolve as linhas e as areas clicaveis,
// e Handle reage a teclado e mouse. Assim todo o comportamento pode ser testado sem terminal.
sealed class TuiApp(UiActions actions)
{
    enum Page { Main, Accounts, Settings, Confirm }

    static readonly Page[] Tabs = [Page.Main, Page.Accounts, Page.Settings];

    const int MinColumnWidth = 62;    // abaixo disso duas colunas nao cabem lado a lado
    const int MaxColumnWidth = 110;   // no ultrawide as colunas param aqui e ficam centralizadas
    const int ColumnGap = 3;
    const int MaxPageWidth = 100;
    const double CellAspect = 23.0 / 12.0;  // altura/largura aproximada de uma celula do terminal

    static readonly (string Code, string Name)[] Languages = [("en", "English"), ("pt", "Português (BR)")];

    readonly Dictionary<string, int> _scroll = [];
    Page _page = Page.Main;
    Page _confirmReturn = Page.Main;
    int _confirmSelected;
    int _focus;
    int _selected;
    List<Action> _items = [];
    Frame? _frame;
    string? _message;
    DateTime _messageUntil;
    string[] _confirmLines = [];
    Action? _confirmYes;
    bool _claudeLoginPending;
    DateTime _claudeLoginGivesUpAt;
    internal TimeSpan ClaudeLoginWait { get; set; } = TimeSpan.FromMinutes(5);
    DateTime _nextClaudeCheck;
    readonly DateTime _startedAt = DateTime.Now;
    StatusLineState _statusLine;
    DateTime _nextStatusLineRead;
    string? _signingOut;
    bool _installingDriver;
    bool _serviceBusy;
    bool _driverInstalledNow;
    bool _languageOpen;
    int _languageIndex;

    public bool Dirty { get; private set; } = true;

    // A proxima tela deve limpar tudo: ao trocar de aba ou fechar a lista, sobrariam restos da anterior.
    public bool ClearRequested { get; private set; }

    public bool TakeClearRequest()
    {
        var requested = ClearRequested;
        ClearRequested = false;
        return requested;
    }

    static Settings S => Settings.Current;

    public static bool IsVertical(int width, int height) => width < 100 || width < height * CellAspect;

    // -------------------------------------------------------------- Montagem

    public Frame Build(UiData data, int width, int height)
    {
        Dirty = false;
        if (DateTime.Now >= _nextStatusLineRead)
        {
            _statusLine = ClaudeSetup.State();
            _nextStatusLineRead = DateTime.Now.AddSeconds(5);
        }
        OpenPendingOnFirstRun(data);

        // Janela de login fechada sem concluir: para de consultar. A aba Contas volta a oferecer o login.
        if (_claudeLoginPending && DateTime.Now >= _claudeLoginGivesUpAt) _claudeLoginPending = false;

        if (_claudeLoginPending && DateTime.Now >= _nextClaudeCheck)
        {
            _nextClaudeCheck = DateTime.Now.AddSeconds(5);
            actions.CheckClaude();
            if (data.Claude.LoggedIn == true) _claudeLoginPending = false;
        }

        var frame = new Frame(width, height);
        switch (_page)
        {
            case Page.Settings: BuildSettings(frame, data); break;
            case Page.Accounts: BuildAccounts(frame, data); break;
            case Page.Confirm: BuildConfirm(frame); break;
            default: BuildMain(frame, data); break;
        }
        _frame = frame;
        return frame;
    }

    void OpenPendingOnFirstRun(UiData data)
    {
        if (S.SetupSeen || DateTime.Now - _startedAt < TimeSpan.FromSeconds(3)) return;
        S.SetupSeen = true;
        S.Save();
        if (_page != Page.Main) return;
        if (PendingAccounts(data) > 0) Open(Page.Accounts);
        else if (PendingSettings(data) > 0) Open(Page.Settings);
    }

    // Um agente oculto nao cobra pendencia.
    static int PendingAccounts(UiData data) =>
        (S.ShowCopilot && data.Copilot.NeedsLogin ? 1 : 0) + (S.ShowClaude && data.Claude.LoggedIn == false ? 1 : 0);

    int PendingSettings(UiData data)
    {
        var pending = 0;
        if (data.Mode != SensorMode.Service) pending++;
        if (!data.DriverInstalled && !_driverInstalledNow) pending++;
        if (S.ShowClaude && _statusLine is StatusLineState.NotConfigured or StatusLineState.InvalidSettings) pending++;
        return pending;
    }

    void BuildMain(Frame frame, UiData data)
    {
        var layout = PlanColumns(frame.Width, frame.Height);
        _focus = Math.Clamp(_focus, 0, layout.Count - 1);

        var left = layout[0].X;
        var total = layout[^1].X + layout[^1].Width - left;

        var y = BuildTop(frame, data, left, total, out var languageX, out var languageY);
        var bodyHeight = Math.Max(frame.Height - y, 1);

        for (var i = 0; i < layout.Count; i++)
            BuildColumn(frame, data, layout[i], y, bodyHeight, focused: layout.Count > 1 && i == _focus);

        if (_languageOpen) BuildLanguageList(frame, languageX, languageY + 1);
    }

    // Abas e menu na mesma linha (o menu desce se nao couber), linha de status e divisoria.
    // Devolve a primeira linha livre.
    int BuildTop(Frame frame, UiData data, int left, int total, out int languageX, out int languageY)
    {
        var pendingAccounts = PendingAccounts(data);
        var pendingSettings = PendingSettings(data);
        var tabs = new (string Label, Page Page)[]
        {
            (T("Monitoramento", "Monitoring"), Page.Main),
            (T("Contas", "Accounts") + (pendingAccounts > 0 ? $" ({pendingAccounts})" : ""), Page.Accounts),
            (T("Ajustes", "Settings") + (pendingSettings > 0 ? $" ({pendingSettings})" : ""), Page.Settings),
        };

        var tabRow = "";
        var tabWidth = 0;

        void TabArrow(string arrow, int step)
        {
            var key = Dashboard.Key(arrow);
            var w = Screen.CellWidth(Markup.Remove(key));
            frame.Regions.Add(new HitArea(left + tabWidth, 0, w, 1, () => MoveTab(step)));
            tabRow += key + " ";
            tabWidth += w + 1;
        }

        TabArrow("←", -1);
        foreach (var (label, page) in tabs)
        {
            var text = $" {label} ";
            var style = _page == page ? $"bold white on {Theme.TabActive}" : $"{Theme.Label} on {Theme.TabIdle}";
            var w = Screen.CellWidth(text);
            var target = page;
            frame.Regions.Add(new HitArea(left + tabWidth, 0, w, 1, () => Open(target)));
            tabRow += $"[{style}]{Markup.Escape(text)}[/] ";
            tabWidth += w + 1;
        }
        TabArrow("→", 1);

        var language = Languages.First(l => l.Code == S.Language).Name;
        (string Markup, Action Action, bool IsLanguage)[] buttons =
        [
            .. (_page is Page.Accounts or Page.Settings
                ? new (string, Action, bool)[] { (Button("↑↓", T("Navegar", "Navigate")), () => MoveSelection(1), false) }
                : Array.Empty<(string, Action, bool)>()),
            (Button("R", T("Atualizar", "Refresh")), Refresh, false),
            ($"{Dashboard.Key("L")} [{Theme.Value} on {Theme.Selected}] {Markup.Escape(language)}  ▾ [/]", OpenLanguageList, true),
            (Button("Q", T("Sair", "Quit")), actions.Quit, false),
        ];

        const int gap = 3;
        var widths = buttons.Select(b => Screen.CellWidth(Markup.Remove(b.Markup))).ToList();
        var menuWidth = widths.Sum() + gap * (widths.Count - 1);

        // Na mesma linha, o menu fica a direita; se nao couber, desce para a linha de baixo.
        var sameRow = tabWidth + 2 + menuWidth < total;
        var y = sameRow ? 0 : 1;
        var x = sameRow ? total - menuWidth - 1 : 0;
        var row = "";
        languageX = left;
        languageY = y;

        for (var i = 0; i < buttons.Length; i++)
        {
            if (i > 0) row += new string(' ', gap);
            frame.Regions.Add(new HitArea(left + x, y, widths[i], 1, buttons[i].Action));
            if (buttons[i].IsLanguage)
            {
                languageX = left + x + 4;  // alinha a lista com o campo, depois da tecla
                languageY = y;
            }
            row += buttons[i].Markup;
            x += widths[i] + gap;
        }

        Put(frame, 0, left, total, Line(tabRow, total));
        var menuX = sameRow ? total - menuWidth - 1 : 0;
        Put(frame, y, left + menuX, menuWidth + 1, Line(row, menuWidth + 1), erase: sameRow == false);
        y++;

        // Respiro: os fundos preenchidos das abas e das temperaturas nao se encostam.
        if (frame.Height >= 30) y++;

        var status = $"[{Theme.Clock}]{DateTime.Now:HH:mm:ss}[/]     {Dashboard.HeaderTemps(data.Hardware)}";
        if (_message is not null && DateTime.Now < _messageUntil) status += $"     [{Theme.Warn}]{Markup.Escape(_message)}[/]";
        else _message = null;
        Put(frame, y++, left, total, Line(status, total));

        if (y < frame.Height)
            Put(frame, y++, left, total, Line($"[{Theme.PanelBorder}]{new string('─', Math.Max(total - 1, 1))}[/]", total));
        if (frame.Height >= 20) y++;
        return y;
    }

    static string Button(string key, string label) => $"{Dashboard.Key(key)} [{Theme.Label}]{Markup.Escape(label)}[/]";

    void BuildLanguageList(Frame frame, int x, int y)
    {
        const int inner = 20;
        var width = inner + 2;
        frame.Regions.Clear();
        frame.Regions.Add(new HitArea(0, 0, frame.Width, frame.Height, CloseLanguageList));

        var border = Theme.Unit;
        Put(frame, y, x, width, Line($"[{border}]╭{new string('─', inner)}╮[/]", width));
        for (var i = 0; i < Languages.Length; i++)
        {
            var index = i;
            var current = Languages[i].Code == S.Language;

            // A linha sob o cursor fica destacada; o ponto marca o idioma em uso.
            var style = i == _languageIndex ? $"bold {Theme.Value} on {Theme.Highlight}" : current ? Theme.Value : Theme.Label;
            var text = Markup.Escape(Pad($" {(current ? "●" : " ")} {Languages[i].Name}", inner));
            Put(frame, y + 1 + i, x, width, Line($"[{border}]│[/][{style}]{text}[/][{border}]│[/]", width));
            frame.Regions.Add(new HitArea(x, y + 1 + i, width, 1, () => ChooseLanguage(index)));
        }
        Put(frame, y + 1 + Languages.Length, x, width, Line($"[{border}]╰{new string('─', inner)}╯[/]", width));
    }

    sealed record ColumnPlan(string Key, int X, int Width, List<string> Sections);

    // Pouco espaco horizontal: uma coluna so, usando toda a largura. Com espaco: uma coluna por secao,
    // de tamanhos iguais, limitadas a MaxColumnWidth e centralizadas lado a lado. Com todos os agentes
    // ocultos, a secao de IA nao existe e o hardware fica sozinho, centralizado.
    // A ultima celula de cada coluna e da barra de rolagem e fica vazia sem rolagem: a margem conta com ela.
    static List<ColumnPlan> PlanColumns(int width, int height)
    {
        var columnWidth = (width - ColumnGap) / 2;
        var narrow = IsVertical(width, height) || columnWidth < MinColumnWidth;

        if (!S.AnyAgentVisible)
        {
            var alone = narrow ? width : Math.Min(width, MaxColumnWidth);
            return [new ColumnPlan("all", (width - alone + 1) / 2, alone, ["hardware"])];
        }

        if (narrow)
            return [new ColumnPlan("all", 0, width, ["hardware", "ai"])];

        columnWidth = Math.Min(columnWidth, MaxColumnWidth);
        var left = (width - (columnWidth * 2 + ColumnGap) + 1) / 2;
        return
        [
            new ColumnPlan("hardware", left, columnWidth, ["hardware"]),
            new ColumnPlan("ai", left + columnWidth + ColumnGap, columnWidth, ["ai"]),
        ];
    }

    void BuildColumn(Frame frame, UiData data, ColumnPlan column, int y, int height, bool focused)
    {
        var x = column.X;
        var contentWidth = Math.Max(column.Width - 1, 10);  // a ultima celula e da barra de rolagem
        var lines = new List<string>();
        var headers = new List<(int Line, Action Action)>();

        foreach (var section in column.Sections)
        {
            var collapsed = section == "hardware" ? S.HardwareCollapsed : S.AiCollapsed;
            var toggle = section == "hardware" ? (Action)ToggleHardware : ToggleAi;

            if (lines.Count > 0) lines.Add("");
            headers.Add((lines.Count, toggle));
            lines.Add(Line(SectionHeader(section, collapsed, contentWidth, focused), contentWidth));
            if (collapsed) continue;

            lines.Add("");
            var panels = section == "hardware"
                ? Dashboard.HardwarePanels(data.Hardware, contentWidth)
                : Dashboard.AiPanels(data.Copilot, data.Claude, data.Auth, contentWidth);
            for (var i = 0; i < panels.Count; i++)
            {
                if (i > 0) lines.Add("");
                lines.AddRange(Screen.RenderLines(panels[i], contentWidth));
            }
        }

        var max = Math.Max(lines.Count - height, 0);
        var offset = Math.Clamp(_scroll.GetValueOrDefault(column.Key), 0, max);
        _scroll[column.Key] = offset;
        frame.Columns.Add(new ColumnView(column.Key, x, y, column.Width, height, lines.Count));

        for (var i = 0; i < height && y + i < frame.Height; i++)
        {
            var line = offset + i;
            Put(frame, y + i, x, contentWidth, line < lines.Count ? lines[line] : "");
        }

        foreach (var (line, action) in headers)
        {
            var row = line - offset;
            if (row >= 0 && row < height) frame.Regions.Add(new HitArea(x, y + row, contentWidth, 1, action));
        }

        // Barra de rolagem clicavel: acima ou abaixo do marcador sobe/desce uma pagina.
        if (max > 0)
        {
            var thumbSize = Math.Max(height * height / lines.Count, 1);
            var thumbTop = (height - thumbSize) * offset / max;
            var thumb = Line("[white]┃[/]", 3);
            var track = Line($"[{Theme.PanelBorder}]│[/]", 3);
            for (var i = 0; i < height && y + i < frame.Height; i++)
            {
                var inThumb = i >= thumbTop && i < thumbTop + thumbSize;
                Put(frame, y + i, x + contentWidth, 1, inThumb ? thumb : track, erase: false);
            }
            var key = column.Key;
            frame.Regions.Add(new HitArea(x + contentWidth, y, 1, Math.Max(thumbTop, 0), () => Scroll(key, -height + 1)));
            frame.Regions.Add(new HitArea(x + contentWidth, y + thumbTop + thumbSize, 1, Math.Max(height - thumbTop - thumbSize, 0), () => Scroll(key, height - 1)));
        }
    }

    // [-] HARDWARE ───────────── 1
    static string SectionHeader(string section, bool collapsed, int width, bool focused)
    {
        var color = section == "hardware" ? Theme.HardwareSection : Theme.AiSection;
        var name = section == "hardware" ? "HARDWARE" : T("INTELIGÊNCIA ARTIFICIAL", "ARTIFICIAL INTELLIGENCE");
        var key = section == "hardware" ? "1" : "2";
        var mark = collapsed ? "+" : "-";
        var fill = Math.Max(width - Screen.CellWidth($"[{mark}] {name}  {key}") - 3, 1);
        var rule = focused ? color : Theme.PanelBorder;
        return $"[{Theme.Unit}][[[/][bold {color}]{mark}[/][{Theme.Unit}]]][/] [bold {color}]{Markup.Escape(name)}[/] [{rule}]{new string('─', fill)}[/] [{Theme.Unit}]{key}[/]";
    }

    // ------------------------------------------------------------ Telas extras

    int PageHeader(Frame frame, string title, out int contentX, out int contentWidth)
    {
        contentWidth = Math.Min(frame.Width - 4, MaxPageWidth);
        contentX = Math.Max((frame.Width - contentWidth) / 2, 0);

        var back = Button("Esc", T("Voltar", "Back"));
        Put(frame, 1, contentX, contentWidth, Line(back, contentWidth));
        frame.Regions.Add(new HitArea(contentX, 1, Screen.CellWidth(Markup.Remove(back)), 1, Back));
        Put(frame, 3, contentX, contentWidth, Line($"[bold {Theme.Title}]{Markup.Escape(title)}[/]", contentWidth));
        Put(frame, 4, contentX, contentWidth, Line($"[{Theme.PanelBorder}]{new string('─', Math.Max(contentWidth - 1, 1))}[/]", contentWidth));
        return 6;
    }

    void AddItem(Frame frame, ref int y, int x, int width, string markup, Action? action, params string[] details)
    {
        if (y >= frame.Height) return;

        var index = _items.Count;
        var selected = action is not null && index == _selected;
        var prefix = selected ? $"[bold {Theme.Clock}]>[/] " : "  ";
        Put(frame, y, x, width, Line(prefix + markup, width));

        if (action is not null)
        {
            _items.Add(action);
            var captured = index;
            frame.Regions.Add(new HitArea(x, y, width, 1, () =>
            {
                // Nas contas, o primeiro clique seleciona e o segundo executa, como Enter.
                if (_page == Page.Accounts && _selected != captured)
                {
                    _selected = captured;
                    return;
                }
                _selected = captured;
                action();
            }));
        }
        y++;

        foreach (var detail in details)
        {
            foreach (var wrapped in Screen.RenderLines(new Markup($"[{Theme.Unit}]{Markup.Escape(detail)}[/]"), width - 2))
            {
                if (y >= frame.Height) return;
                Put(frame, y++, x + 2, width - 2, wrapped);
            }
        }
        y++;
    }

    void BuildSettings(Frame frame, UiData data)
    {
        _items = [];
        var layout = PlanColumns(frame.Width, frame.Height);
        var left = layout[0].X;
        var total = layout[^1].X + layout[^1].Width - left;
        var y = BuildTop(frame, data, left, total, out var languageX, out var languageY);

        var x = left + 1;
        var w = Math.Min(total - 2, MaxPageWidth);

        // Uma linha por ajuste, com todas as opcoes a vista e a atual em destaque. Clicar em uma opcao
        // escolhe direto; Enter passa para a proxima. As setas laterais continuam trocando de aba.
        void Options(ref int row, string label, string[] names, int current, Action<int> choose)
        {
            const int labelWidth = 28;
            const int gap = 1;

            var index = _items.Count;
            var isSelected = index == _selected;
            var labelStyle = isSelected ? $"bold {Theme.Clock}" : Theme.Label;
            var text = $"[{labelStyle}]{Markup.Escape(Pad(label, labelWidth))}[/] ";

            var chipX = x + 2 + labelWidth + 1;
            var chips = new List<(int X, int W, int Option)>();
            for (var i = 0; i < names.Length; i++)
            {
                var chip = $" {names[i]} ";
                var style = i != current ? Theme.Unit
                    : isSelected ? $"bold black on {Theme.Clock}"
                    : $"bold {Theme.Value} on {Theme.Highlight}";
                text += $"[{style}]{Markup.Escape(chip)}[/]" + new string(' ', gap);

                var w = Screen.CellWidth(chip);
                chips.Add((chipX, w, i));
                chipX += w + gap;
            }
            if (isSelected)
                text += $"   {Dashboard.Key("Enter")} [{Theme.Label}]{T("alterna", "switch")}[/]";

            var y0 = row;
            AddItem(frame, ref row, x, w, text, () => choose((current + 1) % names.Length));
            foreach (var (cx, cw, option) in chips)
            {
                frame.Regions.Add(new HitArea(cx, y0, cw, 1, () =>
                {
                    _selected = index;
                    choose(option);
                }));
            }
        }

        void Set(Action change)
        {
            change();
            S.Save();
            Dirty = true;
        }

        string[] Seconds(int[] values) => values.Select(v => $"{v} s").ToArray();

        Options(ref y, T("Idioma", "Language"), Languages.Select(l => l.Name).ToArray(),
            Array.FindIndex(Languages, l => l.Code == S.Language), ChooseLanguage);
        Options(ref y, T("Atualização do hardware", "Hardware refresh"), Seconds(Settings.DisplayOptions),
            Array.IndexOf(Settings.DisplayOptions, S.DisplaySeconds), i => Set(() => S.DisplaySeconds = Settings.DisplayOptions[i]));
        Options(ref y, T("Consulta do Copilot", "Copilot refresh"), Seconds(Settings.CopilotOptions),
            Array.IndexOf(Settings.CopilotOptions, S.CopilotSeconds), i => Set(() => S.CopilotSeconds = Settings.CopilotOptions[i]));
        Options(ref y, T("Ícones", "Icons"), [T("Ligados", "On"), T("Desligados", "Off")],
            S.Icons ? 0 : 1, i => Set(() => S.Icons = i == 0));

        void Agent(ref int row, string label, bool visible, Action<bool> set) =>
            Options(ref row, label, [T("Mostrar", "Show"), T("Ocultar", "Hide")], visible ? 0 : 1, i =>
            {
                Set(() => set(i == 0));
                ClearRequested = true;  // o layout da tela principal pode mudar
            });

        Agent(ref y, T("Painel do Copilot", "Copilot panel"), S.ShowCopilot, v => S.ShowCopilot = v);
        Agent(ref y, T("Painel do Claude", "Claude panel"), S.ShowClaude, v => S.ShowClaude = v);

        AddMachineRows(frame, ref y, x, w, data);
        AddUpdateRow(frame, ref y, x, w, data);

        _selected = _items.Count == 0 ? 0 : Math.Clamp(_selected, 0, _items.Count - 1);

        if (_languageOpen) BuildLanguageList(frame, languageX, languageY + 1);
    }

    void AddUpdateRow(Frame frame, ref int y, int x, int w, UiData data)
    {
        var update = data.Update ?? new UpdateState(UpdateStep.Idle);
        var selected = _items.Count == _selected;
        string Hint(string text) => selected ? $"   {Dashboard.Key("Enter")} [{Theme.Label}]{Markup.Escape(text)}[/]" : "";

        var check = T("procurar atualizações", "check for updates");
        var (status, action) = update.Step switch
        {
            UpdateStep.Checking => (Dashboard.Loading(T("procurando", "checking")), null),
            UpdateStep.UpToDate => ($"[{Theme.Unit}]{T("Atualizado", "Up to date")}[/]" + Hint(check), actions.CheckUpdate),
            UpdateStep.Available when data.CanInstallUpdate => (
                $"[bold {Theme.Warn}]v{Markup.Escape(update.Version ?? "")}[/] [{Theme.Unit}]{T("disponível", "available")}[/]" + Hint(T("instalar", "install")),
                () => ConfirmUpdate(update.Version ?? "", data.Mode is SensorMode.Service or SensorMode.ServiceOutdated)),
            UpdateStep.Available => (
                $"[bold {Theme.Warn}]v{Markup.Escape(update.Version ?? "")}[/] [{Theme.Unit}]{T("disponível: git pull e wf-sys-mon -Rebuild", "available: git pull and wf-sys-mon -Rebuild")}[/]",
                (Action?)null),
            UpdateStep.Installing => (Dashboard.Loading(update.Detail ?? ""), null),
            UpdateStep.Installed => (
                $"[bold {Theme.Warn}]v{Markup.Escape(update.Version ?? "")}[/] [{Theme.Unit}]{T("instalada; reabrindo", "installed; reopening")}[/]"
                    + (update.Detail is { } detail ? $"  [{Theme.Bad}]{Markup.Escape(detail)}[/]" : ""),
                null),
            UpdateStep.Failed => ($"[{Theme.Bad}]{Markup.Escape(update.Detail ?? "")}[/]" + Hint(T("tentar de novo", "try again")), actions.CheckUpdate),
            _ => (Hint(check), actions.CheckUpdate),
        };

        const int labelWidth = 28;
        var version = Updater.CurrentVersion.ToString();
        AddItem(frame, ref y, x, w, $"[{(selected && action is not null ? $"bold {Theme.Clock}" : Theme.Label)}]{Markup.Escape(Pad(T("Versão", "Version"), labelWidth))}[/]  [{Theme.Value}]{Markup.Escape(version)}[/]   {status}", action);
    }

    void ConfirmUpdate(string version, bool updateService) => Confirm(
        [
            T($"Baixa a versão {version} do GitHub, confere o hash e a origem do pacote e substitui os arquivos do app, que reabre sozinho.", $"Downloads version {version} from GitHub, verifies the package hash and origin, and replaces the app files. The app reopens by itself."),
            updateService
                ? T("O serviço de sensores também é atualizado: o Windows confirma (UAC) e as leituras param por alguns segundos.", "The sensor service is updated too: Windows asks for confirmation (UAC) and the readings stop for a few seconds.")
                : T("Preferências e histórico não são alterados.", "Preferences and history are not changed."),
        ],
        () => actions.InstallUpdate(updateService));

    // Uma linha por item, com o estado. A acao so aparece na linha selecionada, para a tela ficar limpa.
    void AddStatusRow(Frame frame, ref int row, int x, int w, int labelWidth, string label, bool ok, string status, string? actionName, Action? action, string[]? details = null)
    {
        var isSelected = _items.Count == _selected;
        var mark = $"[{(ok ? Theme.Good : Theme.Warn)}]{(ok ? "*" : "!")}[/]";
        var labelStyle = isSelected ? $"bold {Theme.Clock}" : Theme.Label;
        var statusStyle = isSelected ? $"bold {Theme.Clock}" : "bold";
        var text = $"[{labelStyle}]{Markup.Escape(Pad(label, labelWidth))}[/] {mark} [{statusStyle}]{Markup.Escape(status)}[/]";
        if (isSelected && actionName is not null)
            text += $"     {Dashboard.Key("Enter")} [{Theme.Clock}]{Markup.Escape(actionName)}[/]";
        AddItem(frame, ref row, x, w, text, action ?? (() => { }), details ?? []);
    }

    void AddMachineRows(Frame frame, ref int y, int x, int w, UiData data)
    {
        void Account(ref int row, string label, bool ok, string status, string? actionName, Action? action, string[]? details = null) =>
            AddStatusRow(frame, ref row, x, w, 28, label, ok, status, actionName, action, details);

        var serviceOk = data.Mode == SensorMode.Service;
        Account(ref y, T("Serviço de sensores", "Sensor service"), serviceOk,
            _serviceBusy ? T("Aguardando...", "Working...") : data.Mode switch
            {
                SensorMode.Service => T("Ativo", "Running"),
                SensorMode.ServiceOutdated => T("Desatualizado", "Outdated"),
                SensorMode.Elevated => T("Não instalado; app como administrador", "Not installed; app running as administrator"),
                _ => T("Modo limitado", "Limited mode"),
            },
            _serviceBusy ? null : data.Mode switch
            {
                SensorMode.Service => T("Remover", "Remove"),
                SensorMode.ServiceOutdated => T("Atualizar", "Update"),
                _ => T("Instalar", "Install"),
            },
            _serviceBusy ? null : serviceOk ? ConfirmRemoveService : InstallService,
            serviceOk ? null : [T("Lê temperatura, potência e discos, e faz o app abrir sem pedir permissão. O Windows confirma uma vez (UAC).", "Reads temperature, power and disks, and lets the app open without asking for permission. Windows confirms once (UAC).")]);

        var driverOk = data.DriverInstalled || _driverInstalledNow;
        Account(ref y, T("Driver de sensores", "Sensor driver"), driverOk,
            _installingDriver ? T("Instalando...", "Installing...")
                : _driverInstalledNow ? T("Instalado; reabra o app", "Installed; reopen the app")
                : data.DriverInstalled ? "PawnIO"
                : T("PawnIO não instalado", "PawnIO not installed"),
            driverOk || _installingDriver ? null : T("Instalar", "Install"),
            driverOk || _installingDriver ? null : ConfirmInstallDriver,
            driverOk ? null : [T("Necessário para temperatura e potência da CPU, entre outros sensores.", "Required for CPU temperature and power, among other sensors.")]);

        var state = _statusLine;
        var statusText = state switch
        {
            StatusLineState.Ours => T("Configurada", "Configured"),
            StatusLineState.Other => T("Outra status line em uso", "Another status line in use"),
            StatusLineState.InvalidSettings => T("settings.json inválido", "Invalid settings.json"),
            _ => T("Não configurada", "Not configured"),
        };
        Account(ref y, T("Status line do Claude Code", "Claude Code status line"), state == StatusLineState.Ours, statusText,
            state == StatusLineState.NotConfigured ? T("Configurar", "Set up") : null,
            state == StatusLineState.NotConfigured ? ConfirmStatusLine : null);
    }

    void BuildAccounts(Frame frame, UiData data)
    {
        _items = [];
        var layout = PlanColumns(frame.Width, frame.Height);
        var left = layout[0].X;
        var total = layout[^1].X + layout[^1].Width - left;
        var y = BuildTop(frame, data, left, total, out var languageX, out var languageY);

        var x = left + 1;
        var w = Math.Min(total - 2, MaxPageWidth);

        void Account(ref int row, string label, bool ok, string status, string? actionName, Action? action, string[]? details = null) =>
            AddStatusRow(frame, ref row, x, w, 22, label, ok, status, actionName, action, details);

        var ghOk = !data.Copilot.NeedsLogin && data.Copilot.Snapshot is not null;
        var ghBusy = data.Auth.Step is AuthStep.InstallingGh or AuthStep.Starting or AuthStep.WaitingUser;
        var ghText = _signingOut == "github" ? T("Saindo...", "Signing out...")
            : ghOk ? T("Conectado", "Connected")
            : ghBusy ? T("Entrando...", "Signing in...")
            : data.Copilot.NeedsLogin ? T("Não conectado", "Not connected")
            : data.Copilot.Error is not null ? T("Indisponível", "Unavailable")
            : T("Verificando...", "Checking...");
        var ghAction = _signingOut is not null || ghBusy ? null
            : ghOk ? T("Sair", "Sign out")
            : data.Copilot.NeedsLogin ? T("Entrar", "Sign in")
            : null;
        Account(ref y, "GitHub Copilot", ghOk, ghText, ghAction,
            ghAction is null ? null : ghOk ? ConfirmLogoutGitHub : LoginGitHub,
            !ghOk && !data.Copilot.NeedsLogin && data.Copilot.Error is { } copilotError ? [copilotError] : null);

        var steps = Dashboard.LoginSteps(data.Copilot, data.Auth);
        if (steps.Count > 0)
        {
            y--;
            foreach (var step in steps)
            {
                if (y >= frame.Height) break;
                Put(frame, y++, x + 2, w - 2, Line(step, w - 2));
            }
            y++;
        }

        var claudeText = _signingOut == "claude" ? T("Saindo...", "Signing out...")
            : data.Claude.LoggedIn switch
            {
                true => data.Claude.Plan ?? T("Conectado", "Connected"),
                false => _claudeLoginPending ? T("Aguardando o login...", "Waiting for sign-in...") : T("Não conectado", "Not connected"),
                null => data.Claude.AccountChecked ? T("Não encontrado", "Not found") : T("Verificando...", "Checking..."),
            };
        var claudeAction = _signingOut is not null ? null
            : data.Claude.LoggedIn == true ? T("Sair", "Sign out")
            : data.Claude.LoggedIn == false ? T("Entrar", "Sign in")
            : null;
        Account(ref y, "Claude Code", data.Claude.LoggedIn == true, claudeText, claudeAction,
            claudeAction is null ? null : data.Claude.LoggedIn == true ? ConfirmLogoutClaude : LoginClaude,
            data.Claude.LoggedIn is null && data.Claude.AccountChecked ? [T("Instale o Claude Code e reabra o app: https://claude.com/claude-code", "Install Claude Code and reopen the app: https://claude.com/claude-code")] : null);

        _selected = _items.Count == 0 ? 0 : Math.Clamp(_selected, 0, _items.Count - 1);

        if (_languageOpen) BuildLanguageList(frame, languageX, languageY + 1);
    }

    void BuildConfirm(Frame frame)
    {
        _items = [];
        var y = PageHeader(frame, T("Confirmar alteração", "Confirm change"), out var x, out var w);

        foreach (var line in _confirmLines)
        {
            if (y >= frame.Height) break;
            foreach (var wrapped in Screen.RenderLines(new Markup($"[{Theme.Label}]{Markup.Escape(line)}[/]"), w))
                if (y < frame.Height) Put(frame, y++, x, w, wrapped);
        }
        y++;

        AddItem(frame, ref y, x, w, $"{Dashboard.Key(T("S", "Y"))} [bold {Theme.Good}]{T("Confirmar", "Confirm")}[/]", AcceptConfirm);
        AddItem(frame, ref y, x, w, $"{Dashboard.Key("N")} [bold]{T("Cancelar", "Cancel")}[/]", Back);
        _selected = Math.Clamp(_selected, 0, 1);
    }

    // -------------------------------------------------------------- Linhas

    static string Pad(string text, int width) => text + new string(' ', Math.Max(width - Screen.CellWidth(text), 0));

    static string Line(string markup, int width)
    {
        if (string.IsNullOrEmpty(markup)) return "";
        return Screen.RenderLines(new Markup(markup).Overflow(Overflow.Ellipsis), width).FirstOrDefault() ?? "";
    }

    // Coloca um trecho na linha: posiciona na coluna e limpa a area antes, para colunas independentes.
    static void Put(Frame frame, int y, int x, int width, string text, bool erase = true)
    {
        if (y < 0 || y >= frame.Height || width <= 0) return;
        var esc = (char)27;
        var clear = erase ? $"{esc}[{width}X" : "";
        frame.Rows[y] += $"{esc}[{x + 1}G{clear}{text}{esc}[0m";
    }

    // ------------------------------------------------------------- Comandos

    void ToggleHardware() { S.HardwareCollapsed = !S.HardwareCollapsed; S.Save(); Dirty = true; }
    void ToggleAi()
    {
        if (!S.AnyAgentVisible) return;
        S.AiCollapsed = !S.AiCollapsed;
        S.Save();
        Dirty = true;
    }

    void ToggleLanguage()
    {
        S.Language = S.Language == "en" ? "pt" : "en";
        S.Save();
        if (_languageOpen) ClearRequested = true;
        _languageOpen = false;
        Dirty = true;
    }

    void OpenLanguageList()
    {
        _languageIndex = Array.FindIndex(Languages, l => l.Code == S.Language);
        _languageOpen = true;
        Dirty = true;
    }

    void CloseLanguageList()
    {
        ClearRequested = true;
        _languageOpen = false;
        Dirty = true;
    }

    void ChooseLanguage(int index)
    {
        S.Language = Languages[index].Code;
        S.Save();
        CloseLanguageList();
    }

    void Refresh()
    {
        actions.Refresh();
        Say(T("Atualizando...", "Refreshing..."));
    }

    void LoginClaude()
    {
        actions.LoginClaude();
        _claudeLoginPending = true;
        _claudeLoginGivesUpAt = DateTime.Now + ClaudeLoginWait;
        _nextClaudeCheck = DateTime.Now.AddSeconds(5);
        Say(T("Conclua o login na janela que abriu.", "Finish signing in in the window that opened."));
    }

    // Sem o GitHub CLI, avisa antes de instalar; com ele, vai direto ao login.
    void LoginGitHub()
    {
        if (actions.HasGitHubCli())
        {
            actions.LoginGitHub();
            return;
        }

        Confirm(
            [
                T("O login usa o GitHub CLI, que não está instalado. Ele será instalado pelo winget (GitHub.cli).", "Sign-in uses GitHub CLI, which is not installed. It will be installed through winget (GitHub.cli)."),
                T("Depois disso, o navegador abre com o código de login.", "After that, the browser opens with the sign-in code."),
            ],
            actions.LoginGitHub);
    }

    void InstallService() => ServiceAction(actions.InstallService, T("Serviço pronto.", "Service ready."));

    void ConfirmRemoveService() => Confirm(
        [
            T("Remove o serviço de sensores deste PC.", "Removes the sensor service from this PC."),
            T("Temperatura, potência e discos deixam de aparecer, e o Windows confirma (UAC). Dá para instalar de novo aqui.", "Temperature, power and disks will no longer appear, and Windows asks for confirmation (UAC). You can install it again here."),
        ],
        () => ServiceAction(actions.RemoveService, T("Serviço removido.", "Service removed.")));

    void ServiceAction(Action<Action<string?>> run, string success)
    {
        _serviceBusy = true;
        Dirty = true;
        run(failure =>
        {
            _serviceBusy = false;
            Say(failure is null ? success : T($"Não concluído: {failure}", $"Not completed: {failure}"));
        });
    }

    void ConfirmInstallDriver() => Confirm(
        [
            T("Instala o PawnIO pelo winget (namazso.PawnIO). É um driver do Windows, de código aberto e assinado, usado para ler os sensores.", "Installs PawnIO through winget (namazso.PawnIO). It is an open-source, signed Windows driver used to read the sensors."),
            T("O Windows pode pedir sua confirmação (UAC). Reabra o app depois. Para remover: winget uninstall namazso.PawnIO.", "Windows may ask for confirmation (UAC). Reopen the app afterwards. To remove it: winget uninstall namazso.PawnIO."),
        ],
        () =>
        {
            _installingDriver = true;
            actions.InstallDriver(failure =>
            {
                _installingDriver = false;
                _driverInstalledNow = failure is null;
                Say(failure is null
                    ? T("PawnIO instalado. Reabra o app para ler os sensores.", "PawnIO installed. Reopen the app to read the sensors.")
                    : T($"Não foi possível instalar: {failure}", $"Could not install: {failure}"));
            });
        });

    void ConfirmLogoutGitHub() => Confirm(
        [
            T("Remove o login do GitHub CLI neste PC.", "Removes the GitHub CLI sign-in on this PC."),
            T("Outras ferramentas que usam o gh, como git push e o gh, também ficam sem login até você entrar de novo. O token não é revogado no GitHub.", "Other tools that use gh, such as git push and gh itself, are also signed out until you sign in again. The token is not revoked on GitHub."),
        ],
        () => Logout("github", actions.LogoutGitHub, T("Saiu do GitHub.", "Signed out of GitHub.")));

    void ConfirmLogoutClaude() => Confirm(
        [
            T("Sai da sua conta no Claude Code neste PC.", "Signs out of your account in Claude Code on this PC."),
            T("As sessões abertas do Claude Code param de funcionar até você entrar de novo.", "Open Claude Code sessions stop working until you sign in again."),
        ],
        () => Logout("claude", actions.LogoutClaude, T("Saiu do Claude Code.", "Signed out of Claude Code.")));

    void Logout(string account, Action<Action<string?>> run, string success)
    {
        _signingOut = account;
        Dirty = true;
        run(failure =>
        {
            _signingOut = null;
            Say(failure is null ? success : T($"Não foi possível sair: {failure}", $"Could not sign out: {failure}"));
        });
    }

    void Confirm(string[] lines, Action yes)
    {
        _confirmLines = lines;
        _confirmYes = yes;
        _confirmReturn = _page;
        _confirmSelected = _selected;
        Open(Page.Confirm);
    }

    // Volta para a tela de onde veio a confirmacao, na mesma linha.
    void LeaveConfirm()
    {
        Open(_confirmReturn);
        _selected = _confirmSelected;
    }

    void ConfirmStatusLine() => Confirm(
        [
            T("Adiciona a chave statusLine em ~/.claude/settings.json e guarda uma cópia do arquivo atual.", "Adds the statusLine key to ~/.claude/settings.json and keeps a copy of the current file."),
            T("O rodapé do Claude Code passa a mostrar a cota e perde a maioria das dicas de teclado. Reinicie o Claude Code depois.", "The Claude Code footer will show the quota and lose most keyboard hints. Restart Claude Code afterwards."),
        ],
        () =>
        {
            var (_, message) = ClaudeSetup.Apply(ClaudeSetup.BundledScript);
            _nextStatusLineRead = DateTime.MinValue;
            Say(message);
        });

    void AcceptConfirm()
    {
        _confirmYes?.Invoke();
        _confirmYes = null;
        LeaveConfirm();
    }

    void Say(string message)
    {
        _message = message;
        _messageUntil = DateTime.Now.AddSeconds(6);
        Dirty = true;
    }

    void Open(Page page)
    {
        ClearRequested = true;
        _page = page;
        _selected = 0;
        _languageOpen = false;
        Dirty = true;
        if (page == Page.Accounts) actions.CheckClaude();
    }

    void Back()
    {
        if (_page == Page.Confirm) LeaveConfirm();
        else Open(Page.Main);
    }

    // Setas: passa para a aba vizinha, voltando ao inicio depois da ultima.
    void MoveTab(int step)
    {
        var index = Array.IndexOf(Tabs, _page);
        Open(Tabs[(index + step + Tabs.Length) % Tabs.Length]);
    }

    void MoveSelection(int step)
    {
        if (_items.Count == 0) return;
        _selected = (_selected + step + _items.Count) % _items.Count;
        Dirty = true;
    }

    void Scroll(string key, int delta)
    {
        _scroll[key] = Math.Max(_scroll.GetValueOrDefault(key) + delta, 0);
        Dirty = true;
    }

    // --------------------------------------------------------------- Entrada

    public void Handle(InputEvent input)
    {
        switch (input)
        {
            case MouseInput { Click: true } click:
                var region = _frame?.Regions.LastOrDefault(r =>
                    click.X >= r.X && click.X < r.X + r.W && click.Y >= r.Y && click.Y < r.Y + r.H);
                if (region is not null)
                {
                    Dirty = true;
                    region.Action();
                }
                break;

            case MouseInput { Wheel: not 0 } wheel:
                var column = _frame?.Columns.FirstOrDefault(c =>
                    wheel.X >= c.X && wheel.X < c.X + c.W && wheel.Y >= c.Y && wheel.Y < c.Y + c.H);
                if (column is not null) Scroll(column.Key, -wheel.Wheel * 3);
                break;

            case KeyInput key when _languageOpen:
                HandleLanguageKey(key);
                break;

            case KeyInput key:
                if (_page == Page.Main) HandleMainKey(key);
                else HandlePageKey(key);
                break;
        }
    }

    void HandleLanguageKey(KeyInput input)
    {
        switch (input.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.DownArrow:
                _languageIndex = (_languageIndex + 1) % Languages.Length;
                Dirty = true;
                break;
            case ConsoleKey.Enter or ConsoleKey.Spacebar: ChooseLanguage(_languageIndex); break;
            case ConsoleKey.L: ToggleLanguage(); break;
            case ConsoleKey.Escape: CloseLanguageList(); break;
        }
    }

    void HandleMainKey(KeyInput input)
    {
        var columns = _frame?.Columns ?? [];
        var focused = columns.Count > 0 ? columns[Math.Clamp(_focus, 0, columns.Count - 1)] : null;

        switch (input.Key)
        {
            case ConsoleKey.Q or ConsoleKey.Escape: actions.Quit(); break;
            case ConsoleKey.R: Refresh(); break;
            case ConsoleKey.LeftArrow: MoveTab(-1); break;
            case ConsoleKey.RightArrow: MoveTab(1); break;
            case ConsoleKey.A: Open(Page.Accounts); break;
            case ConsoleKey.S: Open(Page.Settings); break;
            case ConsoleKey.L: ToggleLanguage(); break;
            case ConsoleKey.D1 or ConsoleKey.NumPad1: ToggleHardware(); break;
            case ConsoleKey.D2 or ConsoleKey.NumPad2: ToggleAi(); break;
            case ConsoleKey.Tab:
                if (columns.Count > 0) _focus = (_focus + 1) % columns.Count;
                Dirty = true;
                break;
            case ConsoleKey.UpArrow when focused is not null: Scroll(focused.Key, -1); break;
            case ConsoleKey.DownArrow when focused is not null: Scroll(focused.Key, 1); break;
            case ConsoleKey.PageUp when focused is not null: Scroll(focused.Key, -focused.H + 1); break;
            case ConsoleKey.PageDown when focused is not null: Scroll(focused.Key, focused.H - 1); break;
            case ConsoleKey.Home when focused is not null: _scroll[focused.Key] = 0; Dirty = true; break;
            case ConsoleKey.End when focused is not null: Scroll(focused.Key, focused.ContentHeight); break;
        }
    }

    void HandlePageKey(KeyInput input)
    {
        switch (input.Key)
        {
            case ConsoleKey.Escape: Back(); break;
            case ConsoleKey.LeftArrow when _page != Page.Confirm: MoveTab(-1); break;
            case ConsoleKey.RightArrow when _page != Page.Confirm: MoveTab(1); break;
            case ConsoleKey.R when _page != Page.Confirm: Refresh(); break;
            case ConsoleKey.S when _page == Page.Accounts: Open(Page.Settings); break;
            case ConsoleKey.A when _page == Page.Settings: Open(Page.Accounts); break;
            case ConsoleKey.UpArrow: MoveSelection(-1); break;
            case ConsoleKey.DownArrow or ConsoleKey.Tab: MoveSelection(1); break;
            case ConsoleKey.Enter or ConsoleKey.Spacebar when _selected < _items.Count: _items[_selected](); Dirty = true; break;
            case ConsoleKey.Q when _page != Page.Confirm: actions.Quit(); break;
            case ConsoleKey.S or ConsoleKey.Y when _page == Page.Confirm: AcceptConfirm(); break;
            case ConsoleKey.N when _page == Page.Confirm: Back(); break;
            case ConsoleKey.S when _page == Page.Settings: Back(); break;
            case ConsoleKey.A when _page == Page.Accounts: Back(); break;
            case ConsoleKey.L: ToggleLanguage(); break;
        }
    }
}
