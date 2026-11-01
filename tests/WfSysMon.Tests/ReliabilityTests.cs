using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WfSysMon;
using Xunit;

// Dados fora do esperado e programas que nao respondem nao podem parar a leitura nem travar o app.
public class ReliabilityTests
{
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void JsonReadTreatsUnexpectedTypesAsMissing()
    {
        var odd = Json("""{"s":1,"b":"x","n":"x","l":"x","big":1e300,"frac":1.5,"d":5,"o":[],"u":"x","far":99999999999999,"neg":-1}""");

        Assert.Null(JsonRead.Str(odd, "s"));
        Assert.Null(JsonRead.Bool(odd, "b"));
        Assert.Null(JsonRead.Num(odd, "n"));
        Assert.Equal(0, JsonRead.Long(odd, "l"));
        Assert.Equal(0, JsonRead.Long(odd, "big"));
        Assert.Equal(0, JsonRead.Long(odd, "frac"));
        Assert.Null(JsonRead.Date(odd, "d"));
        Assert.Null(JsonRead.Obj(odd, "o"));
        Assert.Null(JsonRead.UnixSeconds(odd, "u"));
        Assert.Null(JsonRead.UnixSeconds(odd, "far"));
        Assert.Null(JsonRead.UnixSeconds(odd, "neg"));
        Assert.Null(JsonRead.Str(odd, "missing"));

        // O proprio elemento pode nao ser um objeto.
        foreach (var notObject in new[] { "[]", "1", "\"x\"", "null" })
        {
            Assert.Null(JsonRead.Str(Json(notObject), "a"));
            Assert.Equal(0, JsonRead.Long(Json(notObject), "a"));
        }
    }

    [Fact]
    public void JsonReadReadsExpectedValues()
    {
        var good = Json("""{"s":"x","b":true,"n":1.5,"l":7,"d":"2026-11-01T00:00:00Z","o":{},"u":1000}""");

        Assert.Equal("x", JsonRead.Str(good, "s"));
        Assert.True(JsonRead.Bool(good, "b"));
        Assert.Equal(1.5, JsonRead.Num(good, "n"));
        Assert.Equal(7, JsonRead.Long(good, "l"));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), JsonRead.Date(good, "d"));
        Assert.NotNull(JsonRead.Obj(good, "o"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1000), JsonRead.UnixSeconds(good, "u"));
    }

    [Theory]
    [InlineData("""{"type":"session.usage_record","id":"x","timestamp":5,"data":{"usage":{}}}""")]
    [InlineData("""{"type":"session.usage_record","id":"x","timestamp":"2026-11-01T00:00:00Z","data":[]}""")]
    [InlineData("""{"type":"session.usage_record","id":"x","timestamp":"2026-11-01T00:00:00Z","data":{"usage":5}}""")]
    [InlineData("""["session.usage_record"]""")]
    public void CopilotLogsSkipLinesWithUnexpectedShapes(string odd)
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "events.jsonl");
        var good = $$$$"""{"type":"session.usage_record","id":"good","timestamp":"{{{{DateTimeOffset.UtcNow:O}}}}","data":{"usage":{"inputTokens":10,"outputTokens":"many"}}}""";
        File.WriteAllText(file, odd + "\n" + good + "\n");

        var usage = new CopilotLocalTokens(temp.Path).Read(DateTime.UtcNow.AddDays(-1));

        Assert.Equal(new CopilotLocalUsage(10, 0, 0, 0, 0), usage);
    }

    [Fact]
    public void ClaudeReadingSurvivesUnexpectedValues()
    {
        using var temp = new TempDir();
        var previousConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", temp.Path);
        File.Delete(Path.Combine(AppPaths.DataDir, "claude-usage-days.json"));

        var quotaDir = Path.Combine(AppPaths.DataDir, "claude-status");
        Directory.CreateDirectory(quotaDir);
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        try
        {
            File.WriteAllText(Path.Combine(quotaDir, "bad.json"), """{"five_hour":{"used_percentage":10,"resets_at":"soon"},"seven_day":{"used_percentage":10,"resets_at":99999999999999},"captured_at":"now"}""");
            File.WriteAllText(Path.Combine(quotaDir, "bad-root.json"), "[1,2]");

            var log = Path.Combine(temp.Path, "projects", "p", "a.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            var now = DateTimeOffset.Now.ToString("O");
            string Line(string id, string usage) => $$$$"""{"type":"assistant","requestId":"{{{{id}}}}","timestamp":"{{{{now}}}}","message":{"id":"{{{{id}}}}","usage":{{{{usage}}}}}}""";
            File.WriteAllText(log, string.Join("\n",
                Line("odd", """{"input_tokens":"many","output_tokens":3,"cache_creation":{"ephemeral_1h_input_tokens":"x"}}"""),
                Line("not-object", "5"),
                Line("good", """{"input_tokens":1}""")) + "\n");

            using var usage = new ClaudeUsage();
            usage.Start();

            var expected = new ClaudeTokens(1, 3, 0, 0);
            Assert.True(SpinWait.SpinUntil(() => usage.State.Month == expected, 8000), usage.State.Month?.ToString() ?? "sem leitura");
            Assert.Null(usage.State.FiveHour);

            // A leitura continua viva: um arquivo valido e uma linha nova ainda aparecem.
            File.WriteAllText(Path.Combine(quotaDir, "ok.json"), $$$$"""{"five_hour":{"used_percentage":25,"resets_at":{{{{future}}}}}}""");
            File.AppendAllText(log, Line("later", """{"input_tokens":2}""") + "\n");
            usage.RefreshNow();

            var later = new ClaudeTokens(3, 3, 0, 0);
            Assert.True(SpinWait.SpinUntil(() => usage.State.Month == later && usage.State.FiveHour is not null, 8000));
            Assert.Equal(25, usage.State.FiveHour!.UsedPercent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousConfig);
            Directory.Delete(quotaDir, recursive: true);
        }
    }

    [Fact]
    public void JsonlTailDoesNotRereadALineThatFailed()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "log.jsonl");
        File.WriteAllText(file, "bad\ngood\n");

        var seen = new List<string>();
        void OnLine(ReadOnlyMemory<byte> line)
        {
            var text = Encoding.UTF8.GetString(line.Span);
            seen.Add(text);
            if (text == "bad") throw new InvalidOperationException();
        }

        var tail = new JsonlTail();
        Assert.Throws<InvalidOperationException>(() => tail.Read(file, OnLine));
        tail.Read(file, OnLine);

        Assert.Equal(["bad", "good"], seen);
    }

    static ProcessStartInfo Cmd(string command) =>
        ExternalTool.PrepareHidden("cmd", $"/c {command}", Environment.SystemDirectory)!;

    [Fact]
    public async Task RunReturnsOutputAndExitCode()
    {
        var result = await ExternalTool.RunAsync(Cmd("echo hello& echo oops 1>&2& exit 3"));

        Assert.NotNull(result);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("hello", result.Output);
        Assert.Contains("oops", result.Error);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunReturnsNullWhenTheProgramCannotStart()
    {
        var missing = new ProcessStartInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")) { UseShellExecute = false };

        Assert.Null(await ExternalTool.RunAsync(missing));
    }

    [Fact]
    public async Task RunStopsAProgramThatDoesNotFinish()
    {
        var watch = Stopwatch.StartNew();

        var result = await ExternalTool.RunAsync(Cmd("ping -n 60 127.0.0.1"), TimeSpan.FromMilliseconds(300));

        Assert.NotNull(result);
        Assert.True(result.TimedOut);
        Assert.NotEqual(0, result.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task RunStopsWhenCanceled()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExternalTool.RunAsync(Cmd("ping -n 60 127.0.0.1"), cancel: cancel.Token));
    }

    // Conexao que abre e para de enviar dados.
    sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
    }

    sealed class StallingHttp(string releaseJson, byte[] hash) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.ToString();
            HttpContent content = url.Contains("api.github.com") ? new StringContent(releaseJson)
                : url.EndsWith(".sha256") ? new ByteArrayContent(hash)
                : new StreamContent(new StalledStream());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StalledDownloadFailsInsteadOfWaitingForever(bool stallLimitFires)
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "WfSysMon.dll"), "old");
        File.WriteAllText(Path.Combine(temp.Path, "wf-sys-mon.cmd"), "old");

        const string zip = "wf-sys-mon-1.2.0-win-x64.zip";
        const string url = "https://github.com/muryarth/wf-sys-mon-tui/releases/download/v1.2.0/";
        var release = JsonSerializer.Serialize(new
        {
            tag_name = "v1.2.0",
            draft = false,
            prerelease = false,
            assets = new[]
            {
                new { name = zip, browser_download_url = url + zip },
                new { name = zip + ".sha256", browser_download_url = url + zip + ".sha256" },
            },
        });

        var limit = TimeSpan.FromMilliseconds(200);
        var unused = TimeSpan.FromMinutes(5);
        using var updater = new Updater(
            new StallingHttp(release, Encoding.ASCII.GetBytes($"{Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant()}  {zip}\n")),
            new Version(1, 1, 0),
            temp.Path,
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>(null),
            downloadLimit: stallLimitFires ? unused : limit,
            stallLimit: stallLimitFires ? limit : unused);

        await updater.CheckAsync();
        await updater.InstallAsync(updateService: false).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(UpdateStep.Failed, updater.State.Step);
        Assert.Equal("old", File.ReadAllText(Path.Combine(temp.Path, "WfSysMon.dll")));
    }

    [Fact]
    public void UndoRestoresWhatItCanWhenAFileIsStuck()
    {
        using var temp = new TempDir();
        string P(string name) => Path.Combine(temp.Path, name);

        File.WriteAllText(P("stuck.dll"), "new");
        File.WriteAllText(P("free.dll"), "new");
        File.WriteAllText(P("free.dll.1.old"), "old");
        File.WriteAllText(P("other.dll.2.old"), "old");
        File.WriteAllText(P("stuck.dll.3.old"), "old");

        bool complete;
        using (File.Open(P("stuck.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            complete = Updater.Undo(
                [P("stuck.dll"), P("free.dll")],
                [(P("stuck.dll"), P("stuck.dll.3.old")), (P("free.dll"), P("free.dll.1.old")), (P("other.dll"), P("other.dll.2.old"))]);
        }

        Assert.False(complete);
        Assert.Equal("old", File.ReadAllText(P("free.dll")));
        Assert.Equal("old", File.ReadAllText(P("other.dll")));
    }

    [Fact]
    public void UndoReportsACompleteRollback()
    {
        using var temp = new TempDir();
        string P(string name) => Path.Combine(temp.Path, name);

        File.WriteAllText(P("a.dll"), "new");
        File.WriteAllText(P("a.dll.1.old"), "old");
        File.WriteAllText(P("added.dll"), "new");

        Assert.True(Updater.Undo([P("a.dll"), P("added.dll"), P("never-copied.dll")], [(P("a.dll"), P("a.dll.1.old"))]));
        Assert.Equal("old", File.ReadAllText(P("a.dll")));
        Assert.False(File.Exists(P("added.dll")));
    }
}
