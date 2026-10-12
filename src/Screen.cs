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
        Write($"{Esc}?1049h{Esc}?25l{Esc}2J{Esc}H");
    }

    public static void Leave() => Write($"{Esc}0m{Esc}?25h{Esc}?1049l");

    public static (int W, int H) Size()
    {
        try { return (Math.Max(Console.WindowWidth, 20), Math.Max(Console.WindowHeight, 5)); }
        catch (IOException) { return (120, 40); }
    }

    // Desenha o conteudo com largura = width-1 (evita quebra automatica na ultima coluna)
    // e recorta na altura da janela, indicando quando ha conteudo oculto.
    public static void Draw(IRenderable content, int width, int height, bool clearFirst)
    {
        var lines = RenderLines(content, width - 1);

        if (lines.Count > height)
        {
            lines = lines.Take(height - 1).ToList();
            lines.Add(RenderLines(new Markup("[grey]... aumente a janela para ver mais[/]"), width - 1).FirstOrDefault() ?? "");
        }

        var sb = new StringBuilder();
        if (clearFirst) sb.Append($"{Esc}2J");
        sb.Append($"{Esc}H");
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append($"{Esc}2K").Append(lines[i]);
        }
        sb.Append($"{Esc}0m{Esc}J");
        Write(sb.ToString());
    }

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
