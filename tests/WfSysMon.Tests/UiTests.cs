using System.Text;
using LibreHardwareMonitor.Hardware;
using Spectre.Console;
using WfSysMon;
using Xunit;
using static SensorFakes;

// Desenha o TUI inteiro em cada tamanho e clica/rola de verdade pela logica de entrada.
public class UiTests
{
    readonly CheckList _checks = new();
    void Check(string name, bool ok, string detail = "")
    {
        _checks.Check(name, ok, detail);
    }

    public static List<IHardware> Machine()
    {
        var c = new List<ISensor> { S("CPU Package", SensorType.Temperature, 72, 38, 91), S("CPU Total", SensorType.Load, 33.4f), S("CPU Package", SensorType.Power, 53.8f) };
        for (var i = 1; i <= 10; i++)
        {
            c.Add(S($"CPU Core #{i}", SensorType.Temperature, 45 + i * 3, 30 + i, 60 + i * 2));
            c.Add(S($"CPU Core #{i}", SensorType.Load, i * 9.3f));
            c.Add(S($"CPU Core #{i}", SensorType.Clock, 4895));
        }
        return
        [
            H("Test CPU", HardwareType.Cpu, c.ToArray()),
            H("Virtual Memory", HardwareType.Memory, S("Memory Used", SensorType.Data, 21.3f), S("Memory Available", SensorType.Data, 12.6f), S("Memory", SensorType.Load, 62.9f)),
            H("Total Memory", HardwareType.Memory, S("Memory Used", SensorType.Data, 15.2f), S("Memory Available", SensorType.Data, 16.7f), S("Memory", SensorType.Load, 47.7f)),
            H("Test GPU", HardwareType.GpuNvidia,
                S("GPU Core", SensorType.Temperature, 40, 35, 66), S("GPU Core", SensorType.Load, 4), S("GPU", SensorType.Fan, 1200), S("GPU Power", SensorType.Power, 42),
                S("D3D Dedicated Memory Used", SensorType.SmallData, 2504), S("GPU Memory Total", SensorType.SmallData, 12227), S("GPU Memory Free", SensorType.SmallData, 1457), S("GPU Memory Used", SensorType.SmallData, 10770)),
            H("TEST DISK A", HardwareType.Storage, S("Temperature", SensorType.Temperature, 36, 34, 40), S("Used Space", SensorType.Load, 75.2f)),
            H("TEST DISK B", HardwareType.Storage, S("Temperature", SensorType.Temperature, 34, 32, 38)),
            H("Test Board", HardwareType.Motherboard, S("Temperature #1", SensorType.Temperature, 44, 41, 47), S("Temperature #2", SensorType.Temperature, 32, 30, 33), S("Fan #2", SensorType.Fan, 1051)),
        ];
    }

    static UiData Data(bool admin = true, bool needsLogin = false, bool? claudeLogged = true)
    {
        var snap = new CopilotSnapshot("individual_pro", true, false, 5000, 9800, -4800, 0, 5000, DateTime.UtcNow.AddDays(30), DateTime.Now);
        var copilot = new CopilotState(needsLogin ? null : snap, needsLogin ? "conta do GitHub nao conectada" : null, false, needsLogin, DateTime.Now.AddSeconds(20), new CopilotLocalUsage(50000, 3000000, 400000000, 20000000, 25000), 1);
        var claude = new ClaudeState(new(6, DateTime.Now.AddHours(3), new(200, 150000, 10000000, 400000, 7.5)), new(1, DateTime.Now.AddDays(1), new(200, 150000, 10000000, 400000, 7.5)), DateTime.Now, true,
            new(250, 200000, 15000000, 500000, 10), [new("claude-test", new(250, 200000, 15000000, 500000, 10))], new DateOnly(2026, 11, 1), null, "Claude Pro", claudeLogged, 1, null, true);
        return new UiData(Machine(), copilot, claude, new AuthState(AuthStep.Idle), admin ? SensorMode.Service : SensorMode.Limited);
    }

    // Emula o terminal: aplica posicionamento de coluna e devolve texto, com largura dupla para emoji.
    static string Dump(Frame f)
    {
        var sb = new StringBuilder();
        foreach (var row in f.Rows)
        {
            var cells = new string[f.Width + 2];
            var x = 0;
            for (var i = 0; i < row.Length;)
            {
                if (row[i] == '\u001b' && i + 1 < row.Length && row[i + 1] == '[')
                {
                    var j = i + 2;
                    while (j < row.Length && !char.IsLetter(row[j])) j++;
                    var p = row[(i + 2)..j];
                    if (row[j] == 'G' && int.TryParse(p, out var col)) x = col - 1;
                    else if (row[j] == 'X' && int.TryParse(p, out var n)) for (var k = x; k < x + n && k < cells.Length; k++) cells[k] = " ";
                    i = j + 1;
                    continue;
                }
                var rune = Rune.GetRuneAt(row, i);
                var text = rune.ToString();
                var w = Screen.CellWidth(text);
                if (x < cells.Length) cells[x] = text;
                if (w == 2 && x + 1 < cells.Length) cells[x + 1] = "";
                x += Math.Max(w, 1);
                i += rune.Utf16SequenceLength;
            }
            sb.AppendLine(string.Concat(cells.Select(c => c ?? " ")).TrimEnd());
        }
        return sb.ToString();
    }

    static bool HasGh = true;

    static (TuiApp App, List<string> Log) NewApp()
    {
        var log = new List<string>();
        var app = new TuiApp(new UiActions(() => log.Add("refresh"), () => log.Add("github"), () => HasGh, () => log.Add("claude"), done => { log.Add("logout-github"); done(null); }, done => { log.Add("logout-claude"); done("erro de teste"); }, () => log.Add("check"), done => { log.Add("driver"); done(null); }, done => { log.Add("install-service"); done(null); }, done => { log.Add("remove-service"); done(null); }, () => log.Add("quit")));
        return (app, log);
    }

    [Fact]
    public void WholeInterface()
    {
        using var temp = new TempDir();
        var outDir = temp.Path;
        SensorDisplay.Reset();
        AnsiConsole.Profile.Capabilities.Unicode = true;  // saida redirecionada viria sem Unicode
        var settings = Settings.Current;
        settings.SetupSeen = true;
        settings.DisplaySeconds = 2;
        settings.CopilotSeconds = 30;
        settings.Icons = true;
        settings.Language = "pt";
        settings.HardwareCollapsed = settings.AiCollapsed = false;

        foreach (var hw in new[] { Data() }) SensorDisplay.Update(hw.Hardware);
        SensorDisplay.Animate(0.1);


        foreach (var (name, w, h) in new[] { ("ultrawide", 286, 58), ("vertical", 90, 81), ("meia-ultrawide", 143, 58), ("pequena", 70, 24) })
        {
            var (app, _) = NewApp();
            var frame = app.Build(Data(), w, h);
            var tooWide = Dump(frame).Split('\n').Max(l => Screen.CellWidth(l.TrimEnd()));
            Check($"{name}: nada passa da largura", tooWide <= w, $"maior linha {tooWide}/{w}");
        }

        // Regras de coluna
        var (a1, _) = NewApp();
        var uw = a1.Build(Data(), 286, 58);
        Check("ultrawide: duas colunas iguais", uw.Columns.Count == 2 && uw.Columns[0].W == uw.Columns[1].W, $"{uw.Columns[0].W}/{uw.Columns[1].W}");
        Check("ultrawide: largura maxima", uw.Columns[0].W == 110);
        var margemEsq = uw.Columns[0].X; var margemDir = 286 - (uw.Columns[1].X + uw.Columns[1].W);
        Check("ultrawide: centralizado", Math.Abs(margemEsq - margemDir) <= 1, $"{margemEsq}/{margemDir}");
        var half = a1.Build(Data(), 143, 58);
        Check("meia tela: colunas iguais", half.Columns.Count == 2 && half.Columns[0].W == half.Columns[1].W, $"{half.Columns[0].W}");
        var vert = a1.Build(Data(), 90, 81);
        Check("vertical: coluna unica usa toda a largura", vert.Columns.Count == 1 && vert.Columns[0].X == 0 && vert.Columns[0].W == 90);
        Check("IsVertical", TuiApp.IsVertical(90, 81) && !TuiApp.IsVertical(286, 58) && !TuiApp.IsVertical(143, 58));
        var linhaDaBarra = Dump(vert).Split('\n').First(l => l.Contains("Total load") || l.Contains("Uso total"));
        Check("coluna unica: barra estica", linhaDaBarra.Count(c => c is '\u2501' or '\u2500' or '\u2578') >= 60, linhaDaBarra.Trim());

        // Topo: abas + menu, linha propria para horario/CPU/GPU, menu sem icones
        var topo = Dump(uw).Split('\n');
        Check("abas e menu na primeira linha", topo[0].Contains("Monitoramento") && topo[0].Contains("Contas") && topo[0].Contains("Atualizar") && topo[0].Contains("Sair"), topo[0].Trim());
        Check("linha propria para horario, CPU e GPU", topo[2].Contains("CPU") && topo[2].Contains("GPU") && !topo[2].Contains("Atualizar"));
        var topoV = Dump(vert).Split('\n');
        Check("vertical: menu cabe", string.Join(" ", topoV.Take(3)).Contains("Sair"), topoV[0].Trim());

        // Secoes: recolher mantem o cabecalho
        var (a2, log2) = NewApp();
        var f = a2.Build(Data(), 286, 58);
        HitArea Head(Frame fr, ColumnView c) => fr.Regions.First(r => r.X == c.X && r.W >= c.W - 1 && r.H == 1);
        var hc = Head(f, f.Columns[0]);
        a2.Handle(new MouseInput(hc.X + 3, hc.Y, true, 0));
        Check("clique no titulo recolhe Hardware", settings.HardwareCollapsed);
        f = a2.Build(Data(), 286, 58);
        Check("recolhido: cabecalho continua visivel", Dump(f).Contains("[+] HARDWARE") && !Dump(f).Contains("Temperatura") && f.Columns[0].ContentHeight == 1);
        Check("recolhido: colunas nao mudam de tamanho", f.Columns.Count == 2 && f.Columns[0].W == 110);
        hc = Head(f, f.Columns[0]);
        a2.Handle(new MouseInput(hc.X + 3, hc.Y, true, 0));
        Check("clique de novo expande", !settings.HardwareCollapsed);
        a2.Handle(new KeyInput(ConsoleKey.D1, '1')); a2.Handle(new KeyInput(ConsoleKey.D2, '2'));
        var vc = a2.Build(Data(), 90, 81);
        Check("vertical: as duas recolhidas mantem os cabecalhos", Dump(vc).Contains("[+] HARDWARE") && Dump(vc).Contains("[+] INTELIG"));
        a2.Handle(new KeyInput(ConsoleKey.D1, '1')); a2.Handle(new KeyInput(ConsoleKey.D2, '2'));

        // Idioma: dropdown e tecla L
        Check("idioma padrao e ingles", new Settings().Language == "en");
        f = a2.Build(Data(), 286, 58);
        var lang = f.Regions.First(r => r.H == 1 && Dump(f).Split('\n')[r.Y].Length >= r.X + r.W && Dump(f).Split('\n')[r.Y].Substring(r.X, r.W).Contains("▾"));
        a2.Handle(new MouseInput(lang.X, lang.Y, true, 0));
        var open = a2.Build(Data(), 286, 58);
        Check("dropdown abre com as opcoes", Dump(open).Contains("English") && Dump(open).Contains("●"));
        Check("abrir nao troca o idioma", settings.Language == "pt");
        var opt = open.Regions.First(r => r.H == 1 && r.W < 30);
        Check("lista alinhada com o campo", opt.X == lang.X + 4, $"{opt.X}/{lang.X + 4}");
        a2.Handle(new MouseInput(opt.X + 2, opt.Y, true, 0));
        Check("clique na opcao troca o idioma", settings.Language == "en");
        f = a2.Build(Data(), 286, 58);
        Check("textos em ingles", Dump(f).Contains("Temperature") && Dump(f).Contains("Used") && !Dump(f).Contains("Em uso"));
        Check("dropdown fechou", !Dump(f).Contains("●"));
        lang = f.Regions.First(r => r.H == 1 && Dump(f).Split('\n')[r.Y].Length >= r.X + r.W && Dump(f).Split('\n')[r.Y].Substring(r.X, r.W).Contains("▾"));
        a2.Handle(new MouseInput(lang.X, lang.Y, true, 0));
        a2.Build(Data(), 286, 58);
        a2.Handle(new MouseInput(5, 40, true, 0));
        Check("clique fora fecha sem trocar", settings.Language == "en" && !Dump(a2.Build(Data(), 286, 58)).Contains("●"));
        a2.Handle(new KeyInput(ConsoleKey.L, 'l'));
        Check("L alterna idioma", settings.Language == "pt");
        f = a2.Build(Data(), 286, 58);
        Check("textos em portugues", Dump(f).Contains("Em uso") && Dump(f).Contains("Disponível") && Dump(f).Contains("Total"));
        var usoLado = Dump(f).Split('\n').First(l => l.Contains("Em uso"));
        Check("em uso, disponivel e total na mesma linha", usoLado.Contains("Disponível") && usoLado.Contains("Total"));
        a2.Handle(new KeyInput(ConsoleKey.Q, 'q'));
        Check("Q sai", log2.Contains("quit"));

        // Min/Max por nucleo em coluna estreita e fundo nas temperaturas
        var (a8, _) = NewApp();
        var cab = Dump(a8.Build(Data(), 143, 58)).Split('\n').First(l => l.Contains("Clock"));
        Check("min/max por nucleo em coluna de 70", cab.Contains("Min") && cab.Contains("Max"), cab.Trim());
        Check("sem colchetes na temperatura", !Dump(a8.Build(Data(), 143, 58)).Contains("[ 72"));
        var nucleo = Dump(a8.Build(Data(), 143, 58)).Split('\n').First(l => l.Contains("MHz"));
        Check("nucleo mostra temperatura, min e max", nucleo.Contains("48") && nucleo.Contains("31") && nucleo.Contains("62"), nucleo.Trim());

        // AIC na ponta direita de cada categoria
        foreach (var (w0, h0) in new[] { (286, 58), (143, 58), (90, 81), (70, 90) })
        {
            var (ab, _) = NewApp();
            var linhasAic = Dump(ab.Build(Data(), w0, h0)).Split('\n');
            var ai = ab.Build(Data(), w0, h0).Columns[^1];
            string Celula(string s) => s.Length > ai.X ? s[ai.X..] : "";
            var aic = linhasAic.Select(Celula).Where(l => l.Contains(" AIC ")).ToList();
            var fins = aic.Select(l => l.LastIndexOf("AIC")).Distinct().ToList();
            var barra = linhasAic.Select(Celula).FirstOrDefault(l => l.Contains("98%"));
            Check($"{w0}x{h0}: AIC nas 3 categorias, alinhado", aic.Count == 3 && fins.Count == 1 && aic.All(l => l.Contains("/")), string.Join(",", fins));
            Check($"{w0}x{h0}: AIC na mesma ponta da barra", barra is null || barra.IndexOf("98%") + 3 == aic[0].LastIndexOf("AIC") + 4, $"{barra?.IndexOf("98%") + 3}/{aic[0].LastIndexOf("AIC") + 4}");
        }
        var semAic = Dump(a2.Build(Data(), 286, 58)).Split('\n');
        Check("sem linha AIC separada", !semAic.Any(l => l.Contains("AIC  ") && !l.Contains("/ ")));

        // Ultimo ciclo
        var hist = Path.Combine(outDir, "cycles-teste.json");
        File.Delete(hist);
        File.WriteAllText(hist, "{\"2026-11-01\":{\"Used\":9800,\"Entitlement\":5000,\"ExtraBudget\":5000,\"TokenBasedBilling\":true}}");
        CopilotSnapshot Snap(double used, DateTime? reset) => new("individual_pro", true, false, 5000, used, 5000 - used, 0, 5000, reset, DateTime.Now);
        var dez = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var jan = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var h1 = new CopilotCycleHistory(hist);
        Check("le o fechamento de outubro", h1.Record(Snap(12, dez)) is { Used: 9800, Entitlement: 5000, ExtraBudget: 5000 });
        h1.Record(Snap(500, dez));
        Check("leitura menor nao reduz o ciclo", new CopilotCycleHistory(hist).Record(Snap(400, dez)) is { Used: 9800 });
        Check("virada: o anterior passa a ser novembro", new CopilotCycleHistory(hist).Record(Snap(1, jan)) is { Used: 500 });
        Check("volta da API a ciclo antigo nao apaga historico", new CopilotCycleHistory(hist).Record(Snap(500, dez)) is { Used: 9800 });
        Check("sem data de renovacao nao registra", new CopilotCycleHistory(hist).Record(Snap(1, null)) is null);
        File.WriteAllText(hist, "corrompido");
        Check("arquivo corrompido nao quebra", new CopilotCycleHistory(hist).Record(Snap(1, dez)) is null);
        var (a13, _) = NewApp();
        var comHist = Data();
        comHist = comHist with { Copilot = comHist.Copilot with { LastCycle = new CopilotCycle(9800, 5000, 5000, true) } };
        foreach (var (w1, h2) in new[] { (286, 58), (90, 81), (143, 58) })
        {
            var lc = Dump(a13.Build(comHist, w1, h2)).Split('\n').First(l => l.Contains("Último ciclo"));
            Check($"{w1}: linha do ultimo ciclo", lc.Contains("US$ 98.00") && lc.Contains("100.00") && lc.Contains("9.800 / 10.000 AIC"));
        }
        var (a14, _) = NewApp();
        Check("sem historico, sem linha", !Dump(a14.Build(Data(), 286, 58)).Contains("Último ciclo"));

        // VRAM, RAM e discos
        var d = Dump(a2.Build(Data(), 286, 58));
        Check("VRAM usa GPU Memory Used (10,5 GB)", d.Contains("10.5 GB") && !d.Contains("2.4 GB"));
        Check("VRAM com total e livre", d.Contains("11.9 GB") && d.Contains("1.4 GB"));
        Check("RAM com total calculado", d.Contains("31.9 GB"));
        Check("RAM usa a memoria fisica, nao a virtual", d.Contains("15.2 GB") && !d.Contains("21.3 GB") && !d.Contains("33.9 GB"));
        Check("disco sem volume explica", d.Contains("sem volume"));

        // Scroll por coluna
        var (a3, _) = NewApp();
        f = a3.Build(Data(), 200, 20);
        Check("sem emoji na tela", !Dump(f).Any(char.IsSurrogate));
        var hwCol = f.Columns[0]; var aiCol = f.Columns[1];
        Check("janela baixa gera rolagem", hwCol.ContentHeight > hwCol.H, $"{hwCol.ContentHeight}>{hwCol.H}");
        var before = Dump(f);
        a3.Handle(new MouseInput(hwCol.X + 2, hwCol.Y + 2, false, -1));
        f = a3.Build(Data(), 200, 20);
        var after = Dump(f);
        var left = (string s) => string.Join("\n", s.Split('\n').Skip(hwCol.Y).Take(hwCol.H).Select(l => l.Length > hwCol.W ? l[..hwCol.W] : l));
        var right = (string s) => string.Join("\n", s.Split('\n').Skip(aiCol.Y).Take(aiCol.H).Where(l => !l.Contains("Atualizado") && !l.Contains("renova")).Select(l => l.Length > aiCol.X ? l[aiCol.X..] : ""));
        Check("roda rola so a coluna sob o mouse", left(before) != left(after) && right(before) == right(after));
        a3.Handle(new KeyInput(ConsoleKey.Tab, '\t'));
        a3.Handle(new KeyInput(ConsoleKey.DownArrow, '\0'));
        var scrolled = Dump(a3.Build(Data(), 200, 20));
        Check("Tab + seta rola a outra coluna", right(after) != right(scrolled));
        for (var i = 0; i < 50; i++) a3.Handle(new KeyInput(ConsoleKey.PageDown, '\0'));
        a3.Build(Data(), 200, 20);
        a3.Handle(new KeyInput(ConsoleKey.Home, '\0'));
        Check("Home volta ao topo", right(Dump(a3.Build(Data(), 200, 20))) == right(before));

        // Abas
        var (a9, _) = NewApp();
        a9.Build(Data(), 286, 58);
        a9.Handle(new KeyInput(ConsoleKey.RightArrow, '\0'));
        var aba = a9.Build(Data(), 286, 58);
        Check("seta direita abre Contas", Dump(aba).Contains("Serviço de sensores") && aba.Columns.Count == 0);
        a9.Handle(new KeyInput(ConsoleKey.LeftArrow, '\0'));
        Check("seta esquerda volta", a9.Build(Data(), 286, 58).Columns.Count == 2);
        aba = a9.Build(Data(), 286, 58);
        var abaContas = aba.Regions.First(r => r.Y == 0 && Dump(aba).Split('\n')[0].Substring(r.X, r.W).Contains("Contas"));
        a9.Handle(new MouseInput(abaContas.X + 1, 0, true, 0));
        Check("clique na aba Contas", Dump(a9.Build(Data(), 286, 58)).Contains("Serviço de sensores"));
        a9.Handle(new KeyInput(ConsoleKey.Escape, '\u001b'));
        Check("Esc em Contas volta ao Monitoramento", a9.Build(Data(), 286, 58).Columns.Count == 2);

        // Ajustes como aba: mesma barra superior, setas em ciclo, clique e tecla S
        a9.Handle(new KeyInput(ConsoleKey.RightArrow, '\0'));
        a9.Build(Data(), 286, 58);
        a9.Handle(new KeyInput(ConsoleKey.RightArrow, '\0'));
        aba = a9.Build(Data(), 286, 58);
        var topoAjustes = Dump(aba).Split('\n');
        Check("direita, direita chega em Ajustes", Dump(aba).Contains("Hardware refresh") || Dump(aba).Contains("Atualização do hardware"));
        Check("Ajustes mantem abas, menu e status", topoAjustes[0].Contains("Monitoramento") && topoAjustes[0].Contains("Ajustes") && topoAjustes[0].Contains("Sair") && topoAjustes[2].Contains("CPU"));
        Check("Ajustes saiu do menu", !topoAjustes[0].Contains("S  Ajustes"));
        a9.Handle(new KeyInput(ConsoleKey.RightArrow, '\0'));
        Check("direita na ultima aba volta ao inicio", a9.Build(Data(), 286, 58).Columns.Count == 2);
        a9.Handle(new KeyInput(ConsoleKey.LeftArrow, '\0'));
        aba = a9.Build(Data(), 286, 58);
        Check("esquerda na primeira aba vai para Ajustes", Dump(aba).Contains("Atualização do hardware"));
        a9.Handle(new KeyInput(ConsoleKey.LeftArrow, '\0'));
        Check("esquerda chega em Contas", Dump(a9.Build(Data(), 286, 58)).Contains("Serviço de sensores"));
        a9.Handle(new KeyInput(ConsoleKey.S, 's'));
        aba = a9.Build(Data(), 286, 58);
        Check("S abre Ajustes", Dump(aba).Contains("Atualização do hardware"));
        a9.Handle(new KeyInput(ConsoleKey.Escape, '\u001b'));
        aba = a9.Build(Data(), 286, 58);
        Check("Esc em Ajustes volta ao Monitoramento", aba.Columns.Count == 2);
        var abaAjustes = aba.Regions.First(r => r.Y == 0 && Dump(aba).Split('\n')[0].Substring(r.X, r.W).Contains("Ajustes"));
        a9.Handle(new MouseInput(abaAjustes.X + 1, 0, true, 0));
        Check("clique na aba Ajustes", Dump(a9.Build(Data(), 286, 58)).Contains("Atualização do hardware"));

        // Contas: navegacao, login e logout
        var (a4, log4) = NewApp();
        a4.Build(Data(admin: false, needsLogin: true, claudeLogged: false), 90, 81);
        a4.Handle(new KeyInput(ConsoleKey.A, 'a'));
        Check("abrir contas verifica o Claude", log4.Contains("check"));
        f = a4.Build(Data(admin: false, needsLogin: true, claudeLogged: false), 90, 81);
        Check("contas mostra pendencias", Dump(f).Contains("Modo limitado") && Dump(f).Contains("Não conectado"));
        a4.Handle(new KeyInput(ConsoleKey.Enter, '\r'));
        Check("Enter no primeiro item instala o servico", log4.Contains("install-service"));
        a4.Handle(new KeyInput(ConsoleKey.DownArrow, '\0'));
        a4.Handle(new KeyInput(ConsoleKey.DownArrow, '\0'));
        a4.Handle(new KeyInput(ConsoleKey.Enter, '\r'));
        Check("item GitHub inicia login", log4.Contains("github"));
        a4.Handle(new KeyInput(ConsoleKey.DownArrow, '\0'));
        a4.Handle(new KeyInput(ConsoleKey.Enter, '\r'));
        Check("item Claude abre login", log4.Contains("claude"));
        a4.Handle(new KeyInput(ConsoleKey.Escape, '\u001b'));
        Check("Esc na tela nao fecha o app", !log4.Contains("quit"));

        var (a10, log10) = NewApp();
        a10.Build(Data(), 90, 81);
        a10.Handle(new KeyInput(ConsoleKey.A, 'a'));
        f = a10.Build(Data(), 90, 81);
        a10.Handle(new KeyInput(ConsoleKey.DownArrow, '\0'));
        Check("acao so na linha selecionada", !Dump(f).Contains("Sair     ") || Dump(f).Split('\n').Count(l => l.Contains("Enter")) <= 1);
        a10.Handle(new KeyInput(ConsoleKey.DownArrow, '\0'));
        f = a10.Build(Data(), 90, 81);
        var linhaGh = Dump(f).Split('\n').First(l => l.Contains("GitHub Copilot"));
        Check("seta seleciona a conta e mostra Sair", linhaGh.Contains(">") && linhaGh.Contains("Enter") && linhaGh.Contains("Sair"), linhaGh.Trim());
        a10.Handle(new KeyInput(ConsoleKey.Enter, '\r'));
        f = a10.Build(Data(), 90, 81);
        Check("sair pede confirmacao", Dump(f).Contains("Confirmar") && Dump(f).Contains("git push") && !log10.Contains("logout-github"));
        a10.Handle(new KeyInput(ConsoleKey.N, 'n'));
        a10.Build(Data(), 90, 81);
        Check("N cancela", !log10.Contains("logout-github"));
        Check("voltar mantem a linha selecionada", Dump(a10.Build(Data(), 90, 81)).Split('\n').First(l => l.Contains("GitHub Copilot")).Contains(">"));
        a10.Handle(new KeyInput(ConsoleKey.Enter, '\r'));
        a10.Build(Data(), 90, 81);
        a10.Handle(new KeyInput(ConsoleKey.S, 's'));
        f = a10.Build(Data(), 90, 81);
        Check("confirmar sai e mostra falha", log10.Contains("logout-github") && Dump(f).Contains("sair") || log10.Contains("logout-github"), string.Join(",", log10));
        var (a11, log11) = NewApp();
        a11.Build(Data(), 90, 81);
        a11.Handle(new KeyInput(ConsoleKey.A, 'a'));
        a11.Build(Data(), 90, 81);
        a11.Handle(new KeyInput(ConsoleKey.DownArrow, '\0')); a11.Handle(new KeyInput(ConsoleKey.DownArrow, '\0')); a11.Handle(new KeyInput(ConsoleKey.DownArrow, '\0'));
        a11.Build(Data(), 90, 81);
        a11.Handle(new KeyInput(ConsoleKey.Enter, '\r'));
        a11.Build(Data(), 90, 81);
        a11.Handle(new KeyInput(ConsoleKey.S, 's'));
        f = a11.Build(Data(), 90, 81);
        Check("sair do Claude mostra falha", log11.Contains("logout-claude") && Dump(f).Contains("erro de teste"));

        // Servico de sensores
        var (a22, log22) = NewApp();
        a22.Build(Data(), 90, 81);
        a22.Handle(new KeyInput(ConsoleKey.A, 'a'));
        f = a22.Build(Data(), 90, 81);
        Check("servico ativo aparece, com opcao de remover", Dump(f).Contains("Serviço de sensores") && Dump(f).Contains("Ativo") && Dump(f).Contains("Remover"));
        Check("sem pendencia com o servico", !Dump(f).Split('\n')[0].Contains("Contas ("));
        a22.Handle(new KeyInput(ConsoleKey.Enter, '\r'));
        f = a22.Build(Data(), 90, 81);
        Check("remover pede confirmacao", Dump(f).Contains("Remove o serviço") && !log22.Contains("remove-service"));
        a22.Handle(new KeyInput(ConsoleKey.S, 's'));
        Check("confirmar remove", log22.Contains("remove-service"));
        foreach (var (mode, texto) in new[] { (SensorMode.ServiceOutdated, "Desatualizado"), (SensorMode.Elevated, "Não instalado"), (SensorMode.Limited, "Modo limitado") })
        {
            var (am, _) = NewApp();
            var dm = Data() with { Mode = mode };
            am.Build(dm, 90, 81);
            am.Handle(new KeyInput(ConsoleKey.A, 'a'));
            f = am.Build(dm, 90, 81);
            Check($"{mode}: mostra e conta como pendencia", Dump(f).Contains(texto) && Dump(f).Split('\n')[0].Contains("Contas (1)"));
        }
        var (a23, _) = NewApp();
        var vazio = Data() with { Hardware = [] };
        var telaVazia = Dump(a23.Build(vazio, 286, 58));
        Check("sem leitura ainda, paineis mostram carregando", telaVazia.Split('\n').Count(l => l.Contains("carregando")) >= 5);
        Check("sem leitura ainda, nada aparece como nao detectado", !telaVazia.Contains("não detectado"));
        Check("sem leitura ainda, GPU tambem aparece", telaVazia.Contains("GPU"));
        Check("estrutura real enquanto carrega: rotulos", new[] { "Temperatura", "Uso total", "Núcleo", "Em uso", "Disponível", "VRAM", "Ventoinha", "Disco", "Espaço" }.All(telaVazia.Contains));
        Check("estrutura real enquanto carrega: valores apagados", telaVazia.Contains("···"));
        foreach (var (lv, av) in new[] { (286, 58), (143, 58), (90, 81), (70, 90) })
        {
            var (av1, _) = NewApp();
            var cheia = av1.Build(Data(), lv, av);
            var pre = av1.Build(Data() with { Hardware = [] }, lv, av);
            Check($"{lv}x{av}: carregando ocupa altura parecida com a final", Math.Abs(cheia.Columns[0].ContentHeight - pre.Columns[0].ContentHeight) <= 16, $"{pre.Columns[0].ContentHeight} -> {cheia.Columns[0].ContentHeight}");
        }
        Check("hardware presente sem CPU continua nao detectado", Dump(a23.Build(Data() with { Hardware = Machine().Skip(1).ToList() }, 286, 58)).Contains("não detectado"));


        var renovada = Spectre.Console.Markup.Remove(Dashboard.QuotaEstimate(new ClaudeWindow(0, DateTime.Now.AddHours(5), new(1, 1, 1, 1))));
        Check("janela em 0% mostra traco", renovada == "- tokens", renovada);
        Check("janela com uso continua estimando", Spectre.Console.Markup.Remove(Dashboard.QuotaEstimate(new ClaudeWindow(4, DateTime.Now.AddHours(1), new(200, 150000, 10000000, 400000)))).StartsWith("~"));

        // Carregamento dos dados de IA
        var inicio = Data();
        inicio = inicio with
        {
            Copilot = inicio.Copilot with { Snapshot = null, Error = null, NeedsLogin = false },
            Claude = inicio.Claude with { Plan = null, LoggedIn = null, AccountChecked = false, Month = null },
        };
        var (a25, _) = NewApp();
        var telaInicio = Dump(a25.Build(inicio, 143, 58));
        Check("Copilot mostra a estrutura", new[] { "Gasto", "Cota plano", "Extra budget", "Restante", "Tokens" }.All(telaInicio.Contains));
        Check("Copilot e Claude mostram carregando", telaInicio.Split('\n').Count(l => l.Contains("carregando")) >= 3, "");
        Check("sem ? enquanto carrega", !telaInicio.Contains(" ? ") && !telaInicio.Contains("~?"));
        Check("IsLoading detecta dados pendentes", Dashboard.IsLoading(inicio.Hardware, inicio.Copilot, inicio.Claude));
        Check("IsLoading termina", !Dashboard.IsLoading(Data().Hardware, Data().Copilot, Data().Claude));
        var (a26, _) = NewApp();
        var semPlano = Data();
        semPlano = semPlano with { Claude = semPlano.Claude with { Plan = null } };
        Check("sem plano depois de consultar, mostra ?", Dump(a26.Build(semPlano, 143, 58)).Contains("?"));
        var (a27, _) = NewApp();
        a27.Build(inicio, 90, 81);
        a27.Handle(new KeyInput(ConsoleKey.A, 'a'));
        var contasInicio = Dump(a27.Build(inicio, 90, 81));
        Check("conta do Claude nao aparece como ausente antes de consultar", contasInicio.Contains("Verificando") && !contasInicio.Contains("Não encontrado") && !contasInicio.Contains("claude.com/claude-code"));

        // Menu inicial
        Check("seta para baixo avanca", StartupUi.Step(0, 4, ConsoleKey.DownArrow) == (1, false));
        Check("seta para baixo volta ao inicio", StartupUi.Step(3, 4, ConsoleKey.DownArrow) == (0, false));
        Check("seta para cima volta ao fim", StartupUi.Step(0, 4, ConsoleKey.UpArrow) == (3, false));
        Check("Enter confirma a selecao", StartupUi.Step(2, 4, ConsoleKey.Enter) == (2, true));
        Check("Esc escolhe sair", StartupUi.Step(1, 4, ConsoleKey.Escape) == (3, true));
        Check("outras teclas nao fazem nada", StartupUi.Step(1, 4, ConsoleKey.A) == (1, false) && StartupUi.Step(1, 4, ConsoleKey.Q) == (1, false));
        var menu = StartupUi.MenuLines(["Um", "Dois"], 1);
        Check("so a opcao selecionada tem o marcador", !Markup.Remove(menu[0]).Contains('>') && Markup.Remove(menu[1]).Contains("> Dois"));
        var recomendado = StartupUi.MenuLines(["Um", "Dois"], 1, 0, "Recommended").Select(Markup.Remove).ToArray();
        Check("etiqueta so na opcao recomendada", recomendado[0].Contains("Um  Recommended") && !recomendado[1].Contains("Recommended"));
        var selecionado = StartupUi.MenuLines(["Um", "Dois"], 0, 0, "Recommended").Select(Markup.Remove).ToArray();
        Check("etiqueta continua ao selecionar", selecionado[0].Contains("> Um  Recommended"));
        Check("indicador gira", Enumerable.Range(0, 4).Select(i => StartupUi.SpinnerFrame(new DateTime(2026, 1, 1).AddMilliseconds(i * 120))).Distinct().Count() == 4);
        var (a24, _) = NewApp();
        var carregando = Dump(a24.Build(Data() with { Hardware = [] }, 286, 58));
        Check("carregando sem reticencias", carregando.Contains("carregando") && !carregando.Contains("carregando..."));

        // Driver de sensores
        var (a16, log16) = NewApp();
        var semDriver = Data() with { DriverInstalled = false };
        a16.Build(semDriver, 90, 81);
        a16.Handle(new KeyInput(ConsoleKey.A, 'a'));
        f = a16.Build(semDriver, 90, 81);
        Check("sem driver: aba mostra o estado", Dump(f).Contains("PawnIO não instalado") && Dump(f).Contains("temperatura e potência"));
        Check("sem driver: conta como pendencia", Dump(f).Split('\n')[0].Contains("Contas (1)"), Dump(f).Split('\n')[0].Trim());
        a16.Handle(new KeyInput(ConsoleKey.DownArrow, '\0'));
        a16.Handle(new KeyInput(ConsoleKey.Enter, '\r'));
        f = a16.Build(semDriver, 90, 81);
        Check("instalar pede confirmacao", Dump(f).Contains("namazso.PawnIO") && !log16.Contains("driver"));
        a16.Handle(new KeyInput(ConsoleKey.N, 'n'));
        a16.Build(semDriver, 90, 81);
        Check("N nao instala", !log16.Contains("driver"));
        a16.Handle(new KeyInput(ConsoleKey.Enter, '\r'));
        a16.Build(semDriver, 90, 81);
        a16.Handle(new KeyInput(ConsoleKey.S, 's'));
        f = a16.Build(semDriver, 90, 81);
        Check("confirmar instala e pede para reabrir", log16.Contains("driver") && Dump(f).Contains("reabra o app"));
        var (a17, _) = NewApp();
        a17.Build(Data(), 90, 81);
        a17.Handle(new KeyInput(ConsoleKey.A, 'a'));
        Check("com driver: sem pendencia", !Dump(a17.Build(Data(), 90, 81)).Split('\n')[0].Contains("Contas ("));

        // GitHub CLI ausente: confirma antes de instalar
        HasGh = false;
        var (a18, log18) = NewApp();
        var semLogin = Data(needsLogin: true);
        a18.Build(semLogin, 90, 81);
        a18.Handle(new KeyInput(ConsoleKey.A, 'a'));
        a18.Build(semLogin, 90, 81);
        a18.Handle(new KeyInput(ConsoleKey.DownArrow, '\0')); a18.Handle(new KeyInput(ConsoleKey.DownArrow, '\0'));
        a18.Handle(new KeyInput(ConsoleKey.Enter, '\r'));
        f = a18.Build(semLogin, 90, 81);
        Check("sem gh: pede confirmacao", Dump(f).Contains("GitHub.cli") && !log18.Contains("github"));
        a18.Handle(new KeyInput(ConsoleKey.S, 's'));
        Check("sem gh: confirmar inicia", log18.Contains("github"));
        HasGh = true;

        // Conta sem Copilot: mostra o motivo, em vez de ficar verificando
        var (a19, _) = NewApp();
        var semCopilot = Data();
        semCopilot = semCopilot with { Copilot = semCopilot.Copilot with { Snapshot = null, Error = "conta sem GitHub Copilot ativo (HTTP 404)" } };
        a19.Build(semCopilot, 90, 81);
        a19.Handle(new KeyInput(ConsoleKey.A, 'a'));
        f = a19.Build(semCopilot, 90, 81);
        Check("sem Copilot: mostra o motivo", Dump(f).Contains("Indisponível") && Dump(f).Contains("HTTP 404") && !Dump(f).Contains("Verificando"));

        // Claude Code ausente: orienta
        var (a20, _) = NewApp();
        var semClaude = Data(claudeLogged: null);
        a20.Build(semClaude, 90, 81);
        a20.Handle(new KeyInput(ConsoleKey.A, 'a'));
        f = a20.Build(semClaude, 90, 81);
        Check("sem Claude Code: orienta a instalar", Dump(f).Contains("Não encontrado") && Dump(f).Contains("claude.com/claude-code"));

        // Trocar de aba pede limpeza da tela
        var (a21, _) = NewApp();
        a21.Build(Data(), 286, 58);
        Check("sem troca, sem limpeza", !a21.TakeClearRequest());
        a21.Handle(new KeyInput(ConsoleKey.RightArrow, '\0'));
        Check("trocar de aba limpa a tela", a21.TakeClearRequest());
        Check("limpeza acontece uma vez", !a21.TakeClearRequest());

        // Ferramentas externas: nunca da pasta atual
        var armadilha = Path.Combine(outDir, "armadilha");
        Directory.CreateDirectory(armadilha);
        var marca = Path.Combine(armadilha, "EXECUTOU.txt");
        File.Delete(marca);
        File.WriteAllText(Path.Combine(armadilha, "ferramenta-falsa.cmd"), "@echo x > \"" + marca + "\"\r\n");
        File.WriteAllText(Path.Combine(armadilha, "claude.cmd"), "@echo x > \"" + marca + "\"\r\n");
        File.WriteAllText(Path.Combine(armadilha, "gh.cmd"), "@echo x > \"" + marca + "\"\r\n");
        var pastaAntes = Environment.CurrentDirectory;
        Environment.CurrentDirectory = armadilha;
        try
        {
            Check("nao acha ferramenta so na pasta atual", ExternalTool.Find("ferramenta-falsa") is null);
            var realClaude = ExternalTool.Find("claude");
            Check("claude nao vem da pasta atual", realClaude is null || !realClaude.StartsWith(armadilha, StringComparison.OrdinalIgnoreCase));
            var realGh = GitHubAuth.FindGh();
            Check("gh nao vem da pasta atual", realGh is null || !realGh.StartsWith(armadilha, StringComparison.OrdinalIgnoreCase));
            var psi = ExternalTool.PrepareHidden("claude", "--version");
            Check("roda a partir de pasta do sistema", psi is null || psi.WorkingDirectory == Environment.SystemDirectory);
            Check("nada da armadilha executou", !File.Exists(marca));
            var cmdPath = Path.Combine(Path.GetTempPath(), "wf tool " + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(cmdPath);
            File.WriteAllText(Path.Combine(cmdPath, "ferramenta-teste.cmd"), "@echo ok %1\r\n");
            var script = ExternalTool.PrepareHidden("ferramenta-teste", "abc", cmdPath)!;
            using var q = System.Diagnostics.Process.Start(script)!;
            var resposta = q.StandardOutput.ReadToEnd().Trim();
            q.WaitForExit(10000);
            Check(".cmd em pasta com espaco funciona", resposta == "ok abc", resposta);
            Directory.Delete(cmdPath, true);
        }
        finally { Environment.CurrentDirectory = pastaAntes; }

        // Clique: primeiro seleciona, segundo executa
        var (a12, log12) = NewApp();
        a12.Build(Data(admin: false), 90, 81);
        a12.Handle(new KeyInput(ConsoleKey.A, 'a'));
        f = a12.Build(Data(admin: false), 90, 81);
        var linhas2 = Dump(f).Split('\n');
        var yAdm = Array.FindIndex(linhas2, l => l.Contains("Modo limitado"));
        var yCl = Array.FindIndex(linhas2, l => l.Contains("Claude Code"));
        a12.Handle(new MouseInput(5, yCl, true, 0));
        f = a12.Build(Data(admin: false), 90, 81);
        Check("clique seleciona sem executar", Dump(f).Split('\n')[yCl].Contains(">") && !Dump(f).Contains("Confirmar"));
        a12.Handle(new MouseInput(5, yCl, true, 0));
        Check("segundo clique executa", Dump(a12.Build(Data(admin: false), 90, 81)).Contains("Confirmar"));

        var waiting = new AuthState(AuthStep.WaitingUser, "ABCD-1234");
        var (a5, _) = NewApp();
        a5.Build(Data(), 90, 81);
        a5.Handle(new KeyInput(ConsoleKey.A, 'a'));
        f = a5.Build(Data(needsLogin: true) with { Auth = waiting }, 90, 81);
        Check("codigo do GitHub aparece", Dump(f).Contains("ABCD-1234"));
        Check("min e max com unidade", Dump(a8.Build(Data(), 143, 58)).Contains("min 38°C max 91°C"));

        // Ajustes
        var (a6, _) = NewApp();
        a6.Build(Data(), 90, 81);
        a6.Handle(new KeyInput(ConsoleKey.S, 's'));
        f = a6.Build(Data(), 90, 81);
        var display = settings.DisplaySeconds;
        a6.Handle(new KeyInput(ConsoleKey.DownArrow, '\0')); a6.Handle(new KeyInput(ConsoleKey.Enter, '\r'));
        Check("Enter muda o refresh", settings.DisplaySeconds != display, $"{display}->{settings.DisplaySeconds}");
        settings.DisplaySeconds = display;
        a6.Handle(new KeyInput(ConsoleKey.Escape, '\u001b'));
        Check("Esc volta dos ajustes", a6.Build(Data(), 90, 81).Columns.Count == 1);

        // Ajustes: opcoes a vista, clique direto e Enter em ciclo
        var (a15, _) = NewApp();
        a15.Build(Data(), 90, 81);
        a15.Handle(new KeyInput(ConsoleKey.S, 's'));
        f = a15.Build(Data(), 90, 81);
        var txt = Dump(f);
        Check("idioma mostra as duas opcoes", txt.Split('\n').Any(l => l.Contains("Idioma") && l.Contains("English") && l.Contains("Português (BR)")));
        Check("hardware mostra 1, 2 e 5 s", txt.Split('\n').Any(l => l.Contains("hardware") && l.Contains("1 s") && l.Contains("2 s") && l.Contains("5 s")));
        Check("copilot mostra 30, 60 e 120 s", txt.Split('\n').Any(l => l.Contains("Copilot") && l.Contains("30 s") && l.Contains("60 s") && l.Contains("120 s")));
        Check("icones mostra as duas opcoes", txt.Split('\n').Any(l => l.Contains("Ícones") && l.Contains("Ligados") && l.Contains("Desligados")));
        Check("dica Enter so na linha selecionada", txt.Split('\n').Count(l => l.Contains("Enter")) == 1);
        var lns = txt.Split('\n');
        var yCop = Array.FindIndex(lns, l => l.Contains("Copilot"));
        var x120 = lns[yCop].IndexOf("120 s");
        var copilotAntes = settings.CopilotSeconds;
        a15.Handle(new MouseInput(x120 + 1, yCop, true, 0));
        Check("clique na opcao escolhe direto", settings.CopilotSeconds == 120, $"{settings.CopilotSeconds}");
        f = a15.Build(Data(), 90, 81);
        Check("clique seleciona a linha", Dump(f).Split('\n')[yCop].Contains(">") && Dump(f).Split('\n')[yCop].Contains("Enter"));
        a15.Handle(new KeyInput(ConsoleKey.Enter, '\r'));
        Check("Enter volta ao inicio do ciclo", settings.CopilotSeconds == 30, $"{settings.CopilotSeconds}");
        settings.CopilotSeconds = copilotAntes;
        a15.Build(Data(), 90, 81);
        a15.Handle(new KeyInput(ConsoleKey.UpArrow, '\0'));
        a15.Handle(new KeyInput(ConsoleKey.UpArrow, '\0'));
        a15.Handle(new KeyInput(ConsoleKey.Enter, '\r'));
        Check("Enter em idioma alterna", settings.Language == "en");
        f = a15.Build(Data(), 90, 81);
        Check("textos seguem o idioma", Dump(f).Contains("Hardware refresh") && Dump(f).Contains("switch"));
        a15.Handle(new KeyInput(ConsoleKey.Enter, '\r'));
        Check("Enter volta para portugues", settings.Language == "pt");
        settings.Icons = true;

        // Icones desligados
        settings.Icons = false;
        var (a7, _) = NewApp();
        var noIcons = Dump(a7.Build(Data(), 286, 58));
        Check("sem icones", !noIcons.Contains("[#]"));
        settings.Icons = true;

        // Discos reais
        var disks = DiskVolumes.Read();
        Check("leitura de discos nao falha", disks is not null);

        _checks.AssertAllPassed();
    }
}
