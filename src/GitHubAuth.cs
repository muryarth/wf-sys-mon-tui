using System.Diagnostics;
using System.Text.RegularExpressions;

using static WfSysMon.L;

namespace WfSysMon;

enum AuthStep { Idle, InstallingGh, Starting, WaitingUser, Done, Failed }

record AuthState(AuthStep Step, string? Code = null, string? Message = null);

// Login no GitHub dentro da TUI, usando o GitHub CLI (gh) como backend:
// instala o gh via winget se necessario, inicia o device flow, exibe o codigo
// e abre o navegador. O token fica salvo pelo proprio gh (Windows Credential Manager).
sealed partial class GitHubAuth : IDisposable
{
    public const string DeviceUrl = "https://github.com/login/device";

    volatile AuthState _state = new(AuthStep.Idle);
    Process? _proc;

    public AuthState State => _state;
    public bool InProgress => _state.Step is AuthStep.InstallingGh or AuthStep.Starting or AuthStep.WaitingUser;

    public event Action? Completed;

    // Depois de sair: esquece o resultado do ultimo login.
    public void Reset()
    {
        if (!InProgress) _state = new(AuthStep.Idle);
    }

    public void Start()
    {
        if (InProgress) return;
        _ = Task.Run(RunAsync);
    }

    // Remove o login do GitHub CLI neste PC. Devolve null em caso de sucesso, ou o motivo da falha.
    public static async Task<string?> LogoutAsync()
    {
        foreach (var env in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 })
                return T($"o token vem da variável {env}; remova-a para sair", $"the token comes from the {env} variable; remove it to sign out");

        if (ExternalTool.PrepareHidden("gh", "auth logout --hostname github.com", GhDirs) is not { } psi)
            return T("GitHub CLI não encontrado", "GitHub CLI not found");

        psi.RedirectStandardInput = true;
        using var proc = Process.Start(psi);
        if (proc is null) return T("falha ao iniciar o gh", "could not start gh");

        proc.StandardInput.Close();
        _ = proc.StandardOutput.ReadToEndAsync();
        var error = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0 ? null : error.Trim();
    }

    async Task RunAsync()
    {
        try
        {
            if (FindGh() is null)
            {
                _state = new(AuthStep.InstallingGh, Message: T("instalando o GitHub CLI via winget...", "installing GitHub CLI via winget..."));
                await InstallGhAsync();
                _ = FindGh() ?? throw new InvalidOperationException(T("GitHub CLI não encontrado após a instalação", "GitHub CLI not found after installation"));
            }

            _state = new(AuthStep.Starting, Message: T("iniciando login...", "starting sign-in..."));
            var psi = ExternalTool.PrepareHidden("gh", "auth login --hostname github.com --git-protocol https --web --skip-ssh-key", GhDirs)
                ?? throw new InvalidOperationException(T("GitHub CLI não encontrado", "GitHub CLI not found"));
            psi.RedirectStandardInput = true;
            _proc = Process.Start(psi) ?? throw new InvalidOperationException(T("falha ao iniciar o gh", "could not start gh"));
            _proc.StandardInput.Close();

            var lastLine = "";
            void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                lastLine = line.Trim();
                var m = CodeRegex().Match(line);
                if (m.Success && _state.Code is null)
                {
                    _state = new(AuthStep.WaitingUser, m.Value);
                    OpenBrowser(DeviceUrl);
                }
            }
            _proc.OutputDataReceived += (_, e) => OnLine(e.Data);
            _proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            await _proc.WaitForExitAsync();
            _state = _proc.ExitCode == 0
                ? new(AuthStep.Done, Message: T("conectado ao GitHub", "connected to GitHub"))
                : new(AuthStep.Failed, Message: T($"login não concluído: {lastLine}", $"sign-in not completed: {lastLine}"));
        }
        catch (Exception ex)
        {
            _state = new(AuthStep.Failed, Message: ex.Message);
        }
        finally
        {
            _proc?.Dispose();
            _proc = null;
        }

        if (_state.Step == AuthStep.Done) Completed?.Invoke();
    }

    static async Task InstallGhAsync()
    {
        var psi = ExternalTool.PrepareHidden("winget",
            "install --id GitHub.cli -e --silent --accept-source-agreements --accept-package-agreements")
            ?? throw new InvalidOperationException(T("winget indisponível; instale o GitHub CLI: https://cli.github.com", "winget unavailable; install GitHub CLI: https://cli.github.com"));
        Process? proc;
        try { proc = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(T("winget indisponível; instale o GitHub CLI: https://cli.github.com", "winget unavailable; install GitHub CLI: https://cli.github.com"));
        }
        using (proc)
        {
            if (proc is null) throw new InvalidOperationException(T("falha ao iniciar o winget", "could not start winget"));
            _ = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
        }
    }

    // explorer.exe abre a URL no navegador padrao do usuario sem herdar a elevacao de administrador.
    static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", url) { UseShellExecute = false }); }
        catch (Exception) { /* o usuario ainda ve a URL na tela */ }
    }

    static readonly string[] GhDirs =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI"),
    ];

    public static string? FindGh() => ExternalTool.Find("gh", GhDirs);

    [GeneratedRegex(@"\b[A-Z0-9]{4}-[A-Z0-9]{4}\b")]
    private static partial Regex CodeRegex();

    public void Dispose()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); }
        catch (Exception) { /* processo ja encerrado */ }
    }
}
