using System.Diagnostics;
using System.Text.RegularExpressions;

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

    public void Start()
    {
        if (InProgress) return;
        _ = Task.Run(RunAsync);
    }

    async Task RunAsync()
    {
        try
        {
            var gh = FindGh();
            if (gh is null)
            {
                _state = new(AuthStep.InstallingGh, Message: "instalando GitHub CLI via winget...");
                await InstallGhAsync();
                gh = FindGh() ?? throw new InvalidOperationException("GitHub CLI nao encontrado apos a instalacao");
            }

            _state = new(AuthStep.Starting, Message: "iniciando login...");
            var psi = new ProcessStartInfo(gh, "auth login --hostname github.com --git-protocol https --web --skip-ssh-key")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _proc = Process.Start(psi) ?? throw new InvalidOperationException("falha ao iniciar o gh");
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
                ? new(AuthStep.Done, Message: "conectado ao GitHub")
                : new(AuthStep.Failed, Message: $"login nao concluido: {lastLine}");
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
        var psi = new ProcessStartInfo("winget",
            "install --id GitHub.cli -e --silent --accept-source-agreements --accept-package-agreements")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process? proc;
        try { proc = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("winget indisponivel - instale o GitHub CLI: https://cli.github.com");
        }
        using (proc)
        {
            if (proc is null) throw new InvalidOperationException("falha ao iniciar o winget");
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

    public static string? FindGh()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), "gh.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    [GeneratedRegex(@"\b[A-Z0-9]{4}-[A-Z0-9]{4}\b")]
    private static partial Regex CodeRegex();

    public void Dispose()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); }
        catch (Exception) { /* processo ja encerrado */ }
    }
}
