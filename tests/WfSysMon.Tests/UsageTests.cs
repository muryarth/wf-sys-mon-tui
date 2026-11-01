using System.Runtime.InteropServices;
using System.Text.Json;
using Spectre.Console;
using WfSysMon;
using Xunit;

public class UsageTests
{
    static ClaudeState WaitFor(ClaudeUsage usage, Func<ClaudeState, bool> condition, int ms = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition(usage.State)) return usage.State;
            Thread.Sleep(50);
        }
        return usage.State;
    }

    static string ClaudeLine(string id, string req, DateTimeOffset ts, int i, int o, int cr, int cw) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            requestId = req,
            timestamp = ts.ToString("O"),
            message = new { id, usage = new { input_tokens = i, output_tokens = o, cache_read_input_tokens = cr, cache_creation_input_tokens = cw } },
        });

    static string CopilotLine(string id, DateTimeOffset ts, int input, int output, int read, int write, long nano) =>
        JsonSerializer.Serialize(new
        {
            type = "session.usage_record",
            id,
            timestamp = ts.ToString("O"),
            data = new { usage = new { inputTokens = input, outputTokens = output, cacheReadTokens = read, cacheWriteTokens = write, copilotUsage = new { totalNanoAiu = nano } } },
        });

    [Fact]
    public void ClaudeLogsAreCountedOnce()
    {
        using var temp = new TempDir();
        var previousConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", temp.Path);
        var dayFile = Path.Combine(AppPaths.DataDir, "claude-usage-days.json");
        File.Delete(dayFile);

        try
        {
            var dir = Path.Combine(temp.Path, "projects", "p", "sess", "subagents");
            Directory.CreateDirectory(dir);
            var now = DateTimeOffset.Now;
            var previousMonth = new DateTimeOffset(now.Year, now.Month, 1, 12, 0, 0, now.Offset).AddMonths(-1);
            var main = Path.Combine(temp.Path, "projects", "p", "a.jsonl");
            var quoted = JsonSerializer.Serialize(new { type = "user", text = "{\"usage\":{\"input_tokens\":999999}}" });

            File.WriteAllText(main, string.Join("\n",
                ClaudeLine("m1", "r1", now, 10, 100, 1000, 10000),
                ClaudeLine("m1", "r1", now, 10, 100, 1000, 10000),   // repetida
                ClaudeLine("old", "r0", previousMonth, 5, 5, 5, 5),  // mes anterior
                quoted,                                              // texto citando "usage"
                "{ json invalido com \"usage\"",                     // linha invalida
                ClaudeLine("m2", "r2", now, 1, 2, 3, 4)) + "\n");

            // mesma resposta em outro log (subagente ou sessao retomada) + uma nova
            File.WriteAllText(Path.Combine(dir, "agent.jsonl"),
                ClaudeLine("m1", "r1", now, 10, 100, 1000, 10000) + "\n" + ClaudeLine("m3", "r3", now, 20, 20, 20, 20) + "\n");

            // linha incompleta ao final: so conta depois do '\n'
            var partial = ClaudeLine("m4", "r4", now, 7, 7, 7, 7);
            File.AppendAllText(main, partial[..(partial.Length / 2)]);

            ClaudeState state;
            using (var usage = new ClaudeUsage())
            {
                usage.Start();
                var expected = new ClaudeTokens(31, 122, 1023, 10024);
                state = WaitFor(usage, x => x.Month is not null);
                Assert.Equal(expected, state.Month);

                File.AppendAllText(main, partial[(partial.Length / 2)..] + "\n");
                usage.RefreshNow();
                var completed = expected.Add(new ClaudeTokens(7, 7, 7, 7));
                state = WaitFor(usage, x => x.Month == completed);
                Assert.Equal(completed, state.Month);

                usage.RefreshNow(); usage.RefreshNow(); usage.RefreshNow();
                Thread.Sleep(300);
                Assert.Equal(state.Month, usage.State.Month);
            }

            // Logs apagados: o total do mes continua, inclusive em nova instancia.
            File.Delete(main);
            File.Delete(Path.Combine(dir, "agent.jsonl"));
            using (var again = new ClaudeUsage())
            {
                again.Start();
                var reopened = WaitFor(again, x => x.Month is not null);
                Assert.Equal(state.Month, reopened.Month);
                Assert.Null(reopened.LogsError);
            }

            // Formato desconhecido gera aviso, e nao um total silencioso.
            File.Delete(dayFile);
            File.WriteAllText(main, "{\"type\":\"assistant\",\"novo_formato\":true}\n");
            using (var unknown = new ClaudeUsage())
            {
                unknown.Start();
                Assert.NotNull(WaitFor(unknown, x => x.Month is not null).LogsError);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousConfig);
        }
    }

    [Fact]
    public void CopilotResponseWithoutQuotaIsAnError()
    {
        using var doc = JsonDocument.Parse("{\"copilot_plan\":\"individual_pro\"}");
        Assert.Throws<InvalidOperationException>(() => CopilotUsage.Parse(doc.RootElement));
    }

    [Fact]
    public void CopilotRefreshDoesNotAccumulate()
    {
        using var copilot = new CopilotUsage();
        for (var i = 0; i < 20; i++) copilot.RefreshNow();
        Assert.Equal(1, copilot.PendingWakeups);
    }

    [Fact]
    public void CopilotLocalTokensAreCountedOnce()
    {
        using var temp = new TempDir();
        var session = Path.Combine(temp.Path, "s1");
        Directory.CreateDirectory(session);
        var file = Path.Combine(session, "events.jsonl");
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = DateTimeOffset.UtcNow;
        var partial = CopilotLine("c", now, 100, 10, 50, 40, 3_000_000_000);

        File.WriteAllText(file, string.Join("\n",
            CopilotLine("a", now, 1000, 100, 800, 150, 1_000_000_000),
            CopilotLine("a", now, 1000, 100, 800, 150, 1_000_000_000),   // repetido
            CopilotLine("old", new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.Zero), 9, 9, 0, 0, 9_000_000_000),  // ciclo anterior
            "{\"type\":\"user\",\"text\":\"session.usage_record\"}",
            "{ invalido session.usage_record",
            CopilotLine("b", now, 20, 2, 0, 0, 2_000_000_000)) + "\n" + partial[..10]);

        var tokens = new CopilotLocalTokens(temp.Path);
        Assert.Equal(new CopilotLocalUsage(70, 102, 800, 150, 3), tokens.Read(start));

        File.AppendAllText(file, partial[10..] + "\n");
        var completed = new CopilotLocalUsage(80, 112, 850, 190, 6);
        Assert.Equal(completed, tokens.Read(start));
        Assert.Equal(completed, tokens.Read(start));

        Assert.Equal(CopilotLocalUsage.Zero, tokens.Read(DateTime.UtcNow.AddHours(1)));
        Assert.Equal(CopilotLocalUsage.Zero, new CopilotLocalTokens(Path.Combine(temp.Path, "nada")).Read(start));
    }

    [Fact]
    public void UnrecognizedPricingPagesYieldNothing()
    {
        Assert.Empty(PlanPrices.ParseClaude("<p>nada</p>"));
        Assert.Empty(PlanPrices.ParseCopilot("nada"));
    }

    [Fact]
    public void InputRecordMatchesTheWindowsLayout() =>
        Assert.Equal(20, Marshal.SizeOf<InputReader.InputRecord>());

    [Fact]
    public void ScreenRewritesOnlyChangedLines()
    {
        var real = Console.Out;
        string Capture(string text, bool clear)
        {
            var writer = new StringWriter();
            Console.SetOut(writer);
            try { Screen.Draw(new Markup(text), 40, 10, clear); }
            finally { Console.SetOut(real); }
            return writer.ToString();
        }

        var first = Capture("a\nb\nc", true);
        var same = Capture("a\nb\nc", false);
        var changed = Capture("a\nX\nc", false);

        Assert.Contains('c', first);
        Assert.Empty(same);
        Assert.Contains('X', changed);
        Assert.DoesNotContain('a', changed);
        Assert.DoesNotContain('c', changed);
    }
}
