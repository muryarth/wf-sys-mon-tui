using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using static WfSysMon.L;

namespace WfSysMon;

enum UpdateStep { Idle, Checking, UpToDate, Available, Installing, Installed, Failed }

sealed record UpdateState(UpdateStep Step, string? Version = null, string? Detail = null);

sealed record ReleaseInfo(Version Version, string ZipName, string ZipUrl, string HashUrl);

// Procura e instala novas versoes publicadas no GitHub, sempre a pedido do usuario. O pacote so e
// aplicado com o hash correto e a atestacao de origem confirmada pela pipeline de release: o servico
// de sensores roda com privilegio, entao um pacote sem origem comprovada nunca e instalado.
sealed partial class Updater(
    HttpMessageHandler? handler = null,
    Version? current = null,
    string? appDir = null,
    Func<string, Task<string?>>? verifyOrigin = null,
    Func<string, Task<string?>>? refreshService = null) : IDisposable
{
    const string Repo = "muryarth/wf-sys-mon-tui";
    const string Launcher = "wf-sys-mon.cmd";
    const string AppDll = "WfSysMon.dll";
    const string OldSuffix = ".old";
    const long MaxPackageBytes = 100 * 1024 * 1024;

    static readonly string DownloadPrefix = $"https://github.com/{Repo}/releases/download/";

    readonly HttpClient _http = CreateClient(handler);
    readonly Version _current = current ?? CurrentVersion;
    readonly string _appDir = appDir ?? AppContext.BaseDirectory;
    readonly Func<string, Task<string?>> _verifyOrigin = verifyOrigin ?? VerifyOriginAsync;
    readonly Func<string, Task<string?>> _refreshService = refreshService ?? RefreshServiceAsync;

    ReleaseInfo? _release;
    int _busy;
    volatile UpdateState _state = new(UpdateStep.Idle);

    public UpdateState State => _state;

    public static Version CurrentVersion
    {
        get
        {
            var text = typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            return Version.TryParse(text.Split('+')[0], out var version) ? version : new Version(0, 0, 0);
        }
    }

    // Instalado a partir do pacote da release. Compilado do codigo-fonte, quem atualiza e o git.
    public bool CanInstall => File.Exists(Path.Combine(_appDir, Launcher)) && File.Exists(Path.Combine(_appDir, AppDll));

    public void Check() => Run(CheckAsync);

    public void Install(bool updateService) => Run(() => InstallAsync(updateService));

    void Run(Func<Task> work)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await work(); }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    internal async Task CheckAsync()
    {
        _state = new(UpdateStep.Checking);
        try
        {
            using var response = await _http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _state = new(UpdateStep.UpToDate);
                return;
            }
            response.EnsureSuccessStatusCode();

            _release = ParseRelease(await response.Content.ReadAsStringAsync(), _current);
            _state = _release is { } release
                ? new(UpdateStep.Available, release.Version.ToString())
                : new(UpdateStep.UpToDate);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            _state = new(UpdateStep.Failed, Detail: T("não foi possível consultar as versões", "could not check for versions"));
        }
    }

    internal async Task InstallAsync(bool updateService)
    {
        if (_release is not { } release || !CanInstall) return;

        var version = release.Version.ToString();
        var work = Path.Combine(AppPaths.DataDir, "updates", Guid.NewGuid().ToString("N"));
        void Step(string text) => _state = new(UpdateStep.Installing, version, text);
        void Fail(string text) => _state = new(UpdateStep.Failed, version, text);

        try
        {
            Directory.CreateDirectory(work);
            var zip = Path.Combine(work, release.ZipName);

            Step(T("baixando", "downloading"));
            var hashText = await _http.GetStringAsync(release.HashUrl);
            await DownloadAsync(release.ZipUrl, zip);

            Step(T("conferindo o pacote", "verifying the package"));
            if (ParseHash(hashText, release.ZipName) is not { } expected || !await HashMatchesAsync(zip, expected))
            {
                Fail(T("o hash do pacote não confere", "the package hash does not match"));
                return;
            }
            if (await _verifyOrigin(zip) is { } originFailure)
            {
                Fail(originFailure);
                return;
            }

            var staged = Path.Combine(work, "package");
            ZipFile.ExtractToDirectory(zip, staged);
            if (!File.Exists(Path.Combine(staged, AppDll)) || !File.Exists(Path.Combine(staged, Launcher)))
            {
                Fail(T("o pacote está incompleto", "the package is incomplete"));
                return;
            }

            Step(T("instalando", "installing"));
            ReplaceFiles(staged, _appDir);

            string? serviceFailure = null;
            if (updateService)
            {
                Step(T("atualizando o serviço", "updating the service"));
                serviceFailure = await _refreshService(Path.Combine(_appDir, AppDll));
            }

            _state = new(UpdateStep.Installed, version, serviceFailure is null
                ? null
                : T($"serviço não atualizado: {serviceFailure}", $"service not updated: {serviceFailure}"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Fail(T("a instalação não foi concluída; a versão atual foi mantida", "the installation did not finish; the current version was kept"));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* pasta temporaria */ }
        }
    }

    // Devolve a release se ela for mais nova que a atual. Dados fora do esperado viram erro.
    internal static ReleaseInfo? ParseRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;

        var match = TagRegex().Match(root.GetProperty("tag_name").GetString() ?? "");
        if (!match.Success) throw new InvalidOperationException("unexpected tag");

        var version = Version.Parse(match.Groups[1].Value);
        if (version <= current) return null;

        var zipName = $"wf-sys-mon-{version}-win-x64.zip";
        string Url(string name)
        {
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != name) continue;
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) throw new InvalidOperationException("unexpected URL");
                return url;
            }
            throw new InvalidOperationException("missing asset");
        }

        return new ReleaseInfo(version, zipName, Url(zipName), Url(zipName + ".sha256"));
    }

    internal static string? ParseHash(string text, string fileName)
    {
        foreach (var line in text.Split('\n'))
        {
            var match = HashRegex().Match(line.Trim());
            if (match.Success && match.Groups[2].Value == fileName) return match.Groups[1].Value;
        }
        return null;
    }

    // Arquivos carregados nao podem ser sobrescritos, mas podem ser renomeados: as versoes antigas
    // ficam de lado ate a proxima abertura. Em caso de falha, tudo volta como estava.
    internal static void ReplaceFiles(string source, string target)
    {
        var moved = new List<(string Current, string Old)>();
        var added = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    var old = $"{destination}.{Guid.NewGuid():N}{OldSuffix}";
                    File.Move(destination, old);
                    moved.Add((destination, old));
                }
                added.Add(destination);
                File.Copy(file, destination);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var file in added.Where(File.Exists)) File.Delete(file);
            foreach (var (current, old) in moved) File.Move(old, current);
            throw;
        }
    }

    public void CleanupOldFiles()
    {
        if (!CanInstall) return;
        foreach (var file in Directory.EnumerateFiles(_appDir, "*" + OldSuffix, SearchOption.AllDirectories))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* tenta na proxima abertura */ }
        }
    }

    async Task DownloadAsync(string url, string path)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            total += read;
            if (total > MaxPackageBytes) throw new InvalidDataException("package too large");
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    static async Task<bool> HashMatchesAsync(string path, string expected)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    static async Task<string?> VerifyOriginAsync(string zip)
    {
        var unconfirmed = T("origem não confirmada; confira o login do GitHub na aba Contas", "origin not confirmed; check the GitHub sign-in on the Accounts tab");
        var psi = ExternalTool.PrepareHidden("gh", $"attestation verify \"{zip}\" --repo {Repo} --signer-workflow {Repo}/.github/workflows/release.yml", GitHubAuth.GhDirs);
        if (psi is null) return unconfirmed;

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return unconfirmed;

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error);
            return process.ExitCode == 0 ? null : unconfirmed;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return unconfirmed;
        }
    }

    // Quem instala o servico e a versao nova, ja em disco; o Windows confirma pelo UAC.
    static async Task<string?> RefreshServiceAsync(string dll)
    {
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(dotnet, $"\"{dll}\" --request-service-install")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.SystemDirectory,
            });
            if (process is null) return T("falha ao iniciar", "could not start");

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            return process.ExitCode == 0 ? null : T("confirmação não concedida", "confirmation not granted");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return T("falha ao iniciar", "could not start");
        }
    }

    static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("wf-sys-mon");
        return client;
    }

    [GeneratedRegex(@"^v(\d+\.\d+\.\d+)$")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(\S+)$")]
    private static partial Regex HashRegex();

    public void Dispose() => _http.Dispose();
}
