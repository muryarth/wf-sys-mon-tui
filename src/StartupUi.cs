using Spectre.Console;

namespace WfSysMon;

// Telas de texto antes da TUI abrir: menu com setas e indicador de progresso.
static class StartupUi
{
    const string Esc = "\u001b[";
    static readonly char[] Frames = ['|', '/', '-', '\\'];

    // Quadro atual do indicador animado, tambem usado dentro da TUI.
    public static char SpinnerFrame(DateTime now) => Frames[(int)(now.Ticks / TimeSpan.FromMilliseconds(120).Ticks % Frames.Length)];

    // Devolve a nova selecao e se a escolha foi confirmada. Esc escolhe a ultima opcao, a de sair.
    internal static (int Index, bool Done) Step(int current, int count, ConsoleKey key) => key switch
    {
        ConsoleKey.UpArrow => ((current - 1 + count) % count, false),
        ConsoleKey.DownArrow or ConsoleKey.Tab => ((current + 1) % count, false),
        ConsoleKey.Enter or ConsoleKey.Spacebar => (current, true),
        ConsoleKey.Escape => (count - 1, true),
        _ => (current, false),
    };

    // A opcao recomendada leva uma etiqueta, selecionada ou nao.
    internal static string[] MenuLines(string[] options, int selected, int recommended = -1, string recommendedLabel = "") => options
        .Select((option, i) =>
        {
            var tag = i == recommended ? $"  [green3]{Markup.Escape(recommendedLabel)}[/]" : "";
            return i == selected
                ? $"  [bold aqua]>[/] [bold aqua]{Markup.Escape(option)}[/]{tag}"
                : $"    [grey70]{Markup.Escape(option)}[/]{tag}";
        })
        .ToArray();

    // Sem console interativo, nao ha como escolher: devolve a opcao indicada.
    public static int Menu(string[] options, string hint, int fallback, int recommended = -1, string recommendedLabel = "")
    {
        if (Console.IsInputRedirected) return fallback;

        _ = AnsiConsole.Profile.Capabilities.Ansi;  // garante o modo VT no Windows
        Console.Write($"{Esc}?25l");
        var selected = 0;
        var lines = options.Length + 2;
        var first = true;
        try
        {
            while (true)
            {
                if (!first) Console.Write($"{Esc}{lines}F{Esc}J");
                first = false;

                foreach (var line in MenuLines(options, selected, recommended, recommendedLabel)) AnsiConsole.MarkupLine(line);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"  [grey50]{Markup.Escape(hint)}[/]");

                var (index, done) = Step(selected, options.Length, Console.ReadKey(true).Key);
                selected = index;
                if (done) break;
            }
        }
        finally
        {
            Console.Write($"{Esc}{lines}F{Esc}J{Esc}?25h");
        }
        return selected;
    }

    // Mostra uma linha animada enquanto a tarefa roda, e apaga a linha no fim.
    public static T Progress<T>(string text, Task<T> task)
    {
        Run(text, task);
        return task.GetAwaiter().GetResult();
    }

    public static void Progress(string text, Func<bool> done, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        Run(text, Task.Run(() =>
        {
            while (!done() && DateTime.UtcNow < deadline) Thread.Sleep(30);
        }));
    }

    static void Run(string text, Task task)
    {
        if (Console.IsOutputRedirected)
        {
            task.Wait();
            return;
        }

        _ = AnsiConsole.Profile.Capabilities.Ansi;
        Console.Write($"{Esc}?25l");
        while (!task.IsCompleted)
        {
            Console.Write($"\r{Esc}K");
            AnsiConsole.Markup($"  [aqua]{Markup.Escape(SpinnerFrame(DateTime.Now).ToString())}[/] [grey70]{Markup.Escape(text)}[/]");
            Thread.Sleep(60);
        }
        Console.Write($"\r{Esc}K{Esc}?25h");
    }
}
