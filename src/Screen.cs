using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace WfSysMon;

// Renderizacao em tela cheia (buffer alternativo) que se ajusta ao tamanho atual do terminal.
// Substitui o AnsiConsole.Live, que mantem a maior altura/largura ja desenhada e
// corrompe a tela quando a janela e reduzida.
static class Screen
{
    const string Esc = "\u001b[";

    public static void Enter()
    {
        Console.OutputEncoding = Encoding.UTF8;
        _ = AnsiConsole.Profile.Capabilities.Ansi; // garante que o Spectre habilitou o modo VT no Windows
        Write($"{Esc}?1049h{Esc}?25l{Esc}2J{Esc}H{MouseOn}");
    }

    public static void Leave() => Write($"{MouseOff}{Esc}0m{Esc}?25h{Esc}?1049l");

    // Pede ao terminal cliques e roda do mouse (ver InputReader).
    const string MouseOn = "\u001b[?1000h\u001b[?1006h";
    const string MouseOff = "\u001b[?1006l\u001b[?1000l";

    public static (int W, int H) Size()
    {
        try { return (Math.Max(Console.WindowWidth, 20), Math.Max(Console.WindowHeight, 5)); }
        catch (IOException) { return (120, 40); }
    }

    // Desenha o conteudo com largura = width-1 (evita quebra automatica na ultima coluna)
    // e recorta na altura da janela, indicando quando ha conteudo oculto.
    // So reescreve as linhas que mudaram, sem apagar antes, para a tela nao piscar.
    public static void Draw(IRenderable content, int width, int height, bool clearFirst)
    {
        var lines = RenderLines(content, width - 1);

        if (lines.Count > height)
        {
            lines = lines.Take(height - 1).ToList();
            lines.Add(RenderLines(new Markup("[grey]... aumente a janela para ver mais[/]"), width - 1).FirstOrDefault() ?? "");
        }

        DrawRows(lines, clearFirst);
    }

    public static void DrawRows(IReadOnlyList<string> lines, bool clearFirst)
    {
        if (clearFirst) _shown = [];

        var sb = new StringBuilder();
        if (clearFirst) sb.Append($"{Esc}2J");
        for (var i = 0; i < lines.Count; i++)
        {
            if (i < _shown.Count && _shown[i] == lines[i]) continue;
            // Limpa antes de escrever: depois, o cursor ainda estaria sobre a ultima celula e a apagaria.
            sb.Append($"{Esc}{i + 1};1H{Esc}0m{Esc}2K").Append(lines[i]).Append($"{Esc}0m");
        }
        if (lines.Count < _shown.Count) sb.Append($"{Esc}{lines.Count + 1};1H{Esc}0m{Esc}J");
        _shown = lines.ToList();

        // Atualizacao sincronizada: o terminal mostra o quadro inteiro de uma vez (ignorada se nao houver suporte).
        if (sb.Length > 0) Write($"{Esc}?2026h{sb}{Esc}?2026l");
    }

    static List<string> _shown = [];

    // Largura em celulas: emojis ocupam duas.
    public static int CellWidth(string text) => new Segment(text).CellCount();

    public static List<string> RenderLines(IRenderable content, int width)
    {
        var sw = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = (ColorSystemSupport)AnsiConsole.Profile.Capabilities.ColorSystem,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(sw),
        });
        console.Profile.Width = Math.Max(width, 10);
        console.Profile.Capabilities.Unicode = AnsiConsole.Profile.Capabilities.Unicode;
        console.Write(content);

        var text = sw.ToString().Replace("\r\n", "\n").TrimEnd('\n');
        return text.Split('\n').ToList();
    }

    static void Write(string s)
    {
        Console.Out.Write(s);
        Console.Out.Flush();
    }
}
