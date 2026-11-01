using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using WfSysMon;
using Xunit;

public class UpdaterTests
{
    const string Base = "https://github.com/muryarth/wf-sys-mon-tui/releases/download/v1.2.0/";
    const string ZipName = "wf-sys-mon-1.2.0-win-x64.zip";
    static readonly Version Current = new(1, 1, 0);

    static string ReleaseJson(string tag = "v1.2.0", bool draft = false, bool prerelease = false, string urlBase = Base, bool withHash = true)
    {
        var assets = new List<object> { new { name = ZipName, browser_download_url = urlBase + ZipName } };
        if (withHash) assets.Add(new { name = ZipName + ".sha256", browser_download_url = urlBase + ZipName + ".sha256" });
        return JsonSerializer.Serialize(new { tag_name = tag, draft, prerelease, assets });
    }

    sealed class FakeHttp(Dictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(responses.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    sealed class Scenario : IDisposable
    {
        readonly TempDir _temp = new();

        public string AppDir { get; }
        public byte[] Zip { get; set; }
        public string? HashOverride { get; set; }
        public string? OriginFailure { get; set; }
        public string? ServiceFailure { get; set; }
        public string? ServiceRefreshedWith { get; private set; }

        public Scenario(bool includeDll = true)
        {
            AppDir = Path.Combine(_temp.Path, "app");
            Directory.CreateDirectory(AppDir);
            File.WriteAllText(Path.Combine(AppDir, "WfSysMon.dll"), "old");
            File.WriteAllText(Path.Combine(AppDir, "wf-sys-mon.cmd"), "old");
            File.WriteAllText(Path.Combine(AppDir, "settings-of-the-user.txt"), "keep");

            var package = Path.Combine(_temp.Path, "package");
            Directory.CreateDirectory(Path.Combine(package, "sub"));
            if (includeDll) File.WriteAllText(Path.Combine(package, "WfSysMon.dll"), "new");
            File.WriteAllText(Path.Combine(package, "wf-sys-mon.cmd"), "new");
            File.WriteAllText(Path.Combine(package, "sub", "extra.txt"), "new");
            var zipPath = Path.Combine(_temp.Path, "p.zip");
            ZipFile.CreateFromDirectory(package, zipPath);
            Zip = File.ReadAllBytes(zipPath);
        }

        public Updater Create() => new(
            new FakeHttp(new()
            {
                ["https://api.github.com/repos/muryarth/wf-sys-mon-tui/releases/latest"] = System.Text.Encoding.UTF8.GetBytes(ReleaseJson()),
                [Base + ZipName] = Zip,
                // CRLF como no arquivo publicado na v0.1.0
                [Base + ZipName + ".sha256"] = System.Text.Encoding.ASCII.GetBytes($"{HashOverride ?? Convert.ToHexString(SHA256.HashData(Zip)).ToLowerInvariant()}  {ZipName}\r\n"),
            }),
            Current,
            AppDir,
            _ => Task.FromResult(OriginFailure),
            dll => { ServiceRefreshedWith = dll; return Task.FromResult(ServiceFailure); });

        public string Dll => File.ReadAllText(Path.Combine(AppDir, "WfSysMon.dll"));

        public void Dispose() => _temp.Dispose();
    }

    [Fact]
    public void OnlyNewerPublishedReleasesAreOffered()
    {
        Assert.Equal(new Version(1, 2, 0), Updater.ParseRelease(ReleaseJson(), Current)!.Version);
        Assert.Null(Updater.ParseRelease(ReleaseJson("v1.1.0"), Current));
        Assert.Null(Updater.ParseRelease(ReleaseJson("v1.0.9"), Current));
        Assert.Null(Updater.ParseRelease(ReleaseJson(draft: true), Current));
        Assert.Null(Updater.ParseRelease(ReleaseJson(prerelease: true), Current));
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("https://github.com/someone/else/releases/download/v1.2.0/")]
    public void DownloadsOutsideThisRepositoryAreRejected(string urlBase) =>
        Assert.Throws<InvalidOperationException>(() => Updater.ParseRelease(ReleaseJson(urlBase: urlBase), Current));

    [Fact]
    public void ReleaseWithoutHashOrValidTagIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Updater.ParseRelease(ReleaseJson(withHash: false), Current));
        Assert.Throws<InvalidOperationException>(() => Updater.ParseRelease(ReleaseJson("latest"), Current));
    }

    [Fact]
    public void HashFileIsParsedWithEitherLineEnding()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, Updater.ParseHash($"{hash}  x.zip\r\n", "x.zip"));
        Assert.Equal(hash, Updater.ParseHash($"{hash}  x.zip\n", "x.zip"));
        Assert.Null(Updater.ParseHash($"{hash}  other.zip\n", "x.zip"));
        Assert.Null(Updater.ParseHash("abc  x.zip\n", "x.zip"));
    }

    [Fact]
    public async Task InstallsVerifiedPackageAndUpdatesService()
    {
        using var s = new Scenario();
        using var updater = s.Create();

        await updater.CheckAsync();
        Assert.Equal(new UpdateState(UpdateStep.Available, "1.2.0"), updater.State);

        await updater.InstallAsync(updateService: true);

        Assert.Equal(new UpdateState(UpdateStep.Installed, "1.2.0"), updater.State);
        Assert.Equal("new", s.Dll);
        Assert.Equal("new", File.ReadAllText(Path.Combine(s.AppDir, "sub", "extra.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(s.AppDir, "settings-of-the-user.txt")));
        Assert.Equal(Path.Combine(s.AppDir, "WfSysMon.dll"), s.ServiceRefreshedWith);

        updater.CleanupOldFiles();
        Assert.Empty(Directory.GetFiles(s.AppDir, "*.old", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ServiceIsLeftAloneWhenNotInstalled()
    {
        using var s = new Scenario();
        using var updater = s.Create();
        await updater.CheckAsync();
        await updater.InstallAsync(updateService: false);

        Assert.Equal(UpdateStep.Installed, updater.State.Step);
        Assert.Null(s.ServiceRefreshedWith);
    }

    [Fact]
    public async Task RefusedServiceUpdateStillReportsTheInstalledApp()
    {
        using var s = new Scenario { ServiceFailure = "refused" };
        using var updater = s.Create();
        await updater.CheckAsync();
        await updater.InstallAsync(updateService: true);

        Assert.Equal(UpdateStep.Installed, updater.State.Step);
        Assert.Contains("refused", updater.State.Detail);
        Assert.Equal("new", s.Dll);
    }

    [Fact]
    public async Task WrongHashChangesNothing()
    {
        using var s = new Scenario { HashOverride = new string('0', 64) };
        using var updater = s.Create();
        await updater.CheckAsync();
        await updater.InstallAsync(updateService: true);

        Assert.Equal(UpdateStep.Failed, updater.State.Step);
        Assert.Equal("old", s.Dll);
        Assert.Null(s.ServiceRefreshedWith);
    }

    [Fact]
    public async Task UnconfirmedOriginChangesNothing()
    {
        using var s = new Scenario { OriginFailure = "no attestation" };
        using var updater = s.Create();
        await updater.CheckAsync();
        await updater.InstallAsync(updateService: true);

        Assert.Equal(new UpdateState(UpdateStep.Failed, "1.2.0", "no attestation"), updater.State);
        Assert.Equal("old", s.Dll);
        Assert.Null(s.ServiceRefreshedWith);
    }

    [Fact]
    public async Task IncompletePackageChangesNothing()
    {
        using var s = new Scenario(includeDll: false);
        using var updater = s.Create();
        await updater.CheckAsync();
        await updater.InstallAsync(updateService: true);

        Assert.Equal(UpdateStep.Failed, updater.State.Step);
        Assert.Equal("old", s.Dll);
    }

    [Fact]
    public async Task SourceInstallIsNeverReplaced()
    {
        using var s = new Scenario();
        File.Delete(Path.Combine(s.AppDir, "wf-sys-mon.cmd"));
        using var updater = s.Create();
        await updater.CheckAsync();
        Assert.False(updater.CanInstall);

        await updater.InstallAsync(updateService: true);

        Assert.Equal(UpdateStep.Available, updater.State.Step);
        Assert.Equal("old", s.Dll);
    }

    [Fact]
    public void FailedReplacementRestoresEveryFile()
    {
        using var temp = new TempDir();
        var source = Path.Combine(temp.Path, "source");
        var target = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "a.txt"), "new");
        File.WriteAllText(Path.Combine(source, "b.txt"), "new");
        File.WriteAllText(Path.Combine(target, "a.txt"), "old");
        File.WriteAllText(Path.Combine(target, "b.txt"), "old");

        // Aberto sem permitir renomear: a troca deste arquivo falha.
        using (File.Open(Path.Combine(target, "b.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<Exception>(() => Updater.ReplaceFiles(source, target));

        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "b.txt")));
        Assert.Equal(2, Directory.GetFiles(target).Length);
    }
}
