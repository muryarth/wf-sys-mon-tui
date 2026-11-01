using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using LibreHardwareMonitor.Hardware;
using Spectre.Console;
using static WfSysMon.L;

namespace WfSysMon;

static class Program
{
    const string LimitedArgument = "--limited";
    const string RequestInstallArgument = "--request-service-install";
    const string RequestUninstallArgument = "--request-service-uninstall";

    static readonly TimeSpan AnimationInterval = TimeSpan.FromMilliseconds(80);

    // Tempo para ler o resultado antes de reabrir; um aviso sobre o servico precisa de mais.
    static readonly TimeSpan ReopenDelay = TimeSpan.FromSeconds(1.5);
    static readonly TimeSpan ReopenDelayWithNotice = TimeSpan.FromSeconds(6);

    static bool _reopen;

    static int Main(string[] args)
    {
        // O servico roda sem console: precisa vir antes de qualquer acesso a ele.
        if (args is [ServiceInstaller.RunArgument, ServiceInstaller.AllowArgument, var allowed])
        {
            ServiceBase.Run(new SensorService(new SecurityIdentifier(allowed)) { ServiceName = ServiceInstaller.ServiceName });
            return 0;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        switch (args)
        {
            case [ClaudeSetup.Argument, var script]: return ClaudeSetup.Run(script);
            case [ServiceInstaller.InstallArgument, var user]: return ServiceInstaller.Install(user);
            case [ServiceInstaller.UninstallArgument, _]: return ServiceInstaller.Uninstall();
            case [RequestInstallArgument]: return Report(ServiceInstaller.RequestAsync(ServiceInstaller.InstallArgument).GetAwaiter().GetResult());
            case [RequestUninstallArgument]: return Report(ServiceInstaller.RequestAsync(ServiceInstaller.UninstallArgument).GetAwaiter().GetResult());
        }

        var limited = args.Contains(LimitedArgument);
        var code = Run(IsAdministrator(), limited);
        return _reopen ? Updater.Reopen(typeof(Program).Assembly.Location, limited ? LimitedArgument : "") : code;
    }

    static int Report(string? failure)
    {
        Console.WriteLine(failure is null ? T("Concluído.", "Done.") : T($"Não concluído: {failure}", $"Not completed: {failure}"));
        return failure is null ? 0 : 1;
    }

    static int Run(bool isAdmin, bool limited)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += onCancel;

        // Os valores vem do servico, se ele existir. Sem ele, o app le o que conseguir por conta propria.
        var sensorGate = new object();
        var sensorVersion = 0;
        IReadOnlyList<IHardware> remoteHardware = [];

        using var client = new SensorClient(hardware =>
        {
            lock (sensorGate)
            {
                SensorDisplay.Update(hardware);
                Volatile.Write(ref remoteHardware, hardware);
            }
            Interlocked.Increment(ref sensorVersion);
        });
        client.Start();

        if (!client.WaitForLink(TimeSpan.FromMilliseconds(700)) && !isAdmin && !limited)
        {
            switch (AskForSensors())
            {
                case SensorChoice.Quit or SensorChoice.Elevated:
                    return 0;
                case SensorChoice.Service:
                    StartupUi.Progress(T("Iniciando os sensores...", "Starting the sensors..."),
                        () => client.Link is SensorLink.Connected or SensorLink.Connecting, TimeSpan.FromSeconds(8));
                    break;
            }
        }

        DiskVolumes.Start(cts.Token);

        using var copilot = new CopilotUsage();
        using var auth = new GitHubAuth();
        auth.Completed += copilot.ResetToken;
        copilot.Start();

        using var claude = new ClaudeUsage();
        claude.Start();

        using var updater = new Updater();
        updater.CleanupOldFiles();

        var app = new TuiApp(new UiActions(
            Refresh: () => { copilot.RefreshNow(); claude.CheckAccountNow(); },
            LoginGitHub: auth.Start,
            HasGitHubCli: () => GitHubAuth.FindGh() is not null,
            LoginClaude: OpenClaudeLogin,
            LogoutGitHub: done => _ = Task.Run(async () =>
            {
                var failure = await GitHubAuth.LogoutAsync();
                auth.Reset();
                copilot.ResetToken();
                done(failure);
            }),
            LogoutClaude: done => _ = Task.Run(async () =>
            {
                var failure = await LogoutClaudeAsync();
                claude.CheckAccountNow();
                done(failure);
            }),
            CheckClaude: claude.CheckAccountNow,
            InstallDriver: done => _ = Task.Run(async () => done(await InstallDriverAsync())),
            InstallService: done => _ = Task.Run(async () => done(await ServiceInstaller.RequestAsync(ServiceInstaller.InstallArgument))),
            RemoveService: done => _ = Task.Run(async () => done(await ServiceInstaller.RequestAsync(ServiceInstaller.UninstallArgument))),
            CheckUpdate: updater.Check,
            InstallUpdate: updater.Install,
            Quit: cts.Cancel));

        using var input = new InputReader();
        LocalSensors? local = null;

        Screen.Enter();
        try
        {
            var lastSize = (W: -1, H: -1);
            var lastSensorVersion = -1;
            var lastCopilotVersion = -1;
            var lastClaudeVersion = -1;
            var lastAuth = auth.State;
            var lastUpdate = updater.State;
            DateTime? installedAt = null;
            var dirty = false;
            var clearFirst = false;
            var lastAnimation = DateTime.Now;
            var lastService = DateTime.MinValue;
            var lastLocalVersion = -1;

            while (!cts.IsCancellationRequested)
            {
                foreach (var e in input.Poll())
                    app.Handle(e);
                dirty |= app.Dirty;

                // Com o servico no ar, o app nao toca nos sensores. Se ele cair, volta a ler o que conseguir.
                var link = client.Link;
                var serviceUp = link is SensorLink.Connected or SensorLink.Connecting;
                if (serviceUp)
                {
                    lastService = DateTime.Now;
                    if (local is not null)
                    {
                        local.Dispose();
                        local = null;
                    }
                }
                else if (local is null && DateTime.Now - lastService > TimeSpan.FromSeconds(3))
                {
                    local = new LocalSensors(sensorGate);
                }

                // So redesenha pela animacao enquanto alguma barra estiver em movimento.
                var sinceAnimation = DateTime.Now - lastAnimation;
                if (sinceAnimation >= AnimationInterval && Monitor.TryEnter(sensorGate))
                {
                    try { dirty |= SensorDisplay.Animate(sinceAnimation.TotalSeconds); }
                    finally { Monitor.Exit(sensorGate); }
                    lastAnimation = DateTime.Now;
                }

                var currentSensors = Volatile.Read(ref sensorVersion);
                if (currentSensors != lastSensorVersion)
                {
                    lastSensorVersion = currentSensors;
                    dirty = true;
                }
                if (local is not null && local.Version != lastLocalVersion)
                {
                    lastLocalVersion = local.Version;
                    dirty = true;
                }

                var size = Screen.Size();
                if (size != lastSize)
                {
                    lastSize = size;
                    clearFirst = true;
                    dirty = true;
                }

                var cstate = copilot.State;
                if (cstate.Version != lastCopilotVersion)
                {
                    lastCopilotVersion = cstate.Version;
                    dirty = true;
                }

                var clstate = claude.State;
                if (clstate.Version != lastClaudeVersion)
                {
                    lastClaudeVersion = clstate.Version;
                    dirty = true;
                }

                var astate = auth.State;
                if (!ReferenceEquals(astate, lastAuth))
                {
                    lastAuth = astate;
                    dirty = true;
                }

                var ustate = updater.State;
                if (!ReferenceEquals(ustate, lastUpdate))
                {
                    lastUpdate = ustate;
                    dirty = true;
                }
                dirty |= ustate.Step is UpdateStep.Checking or UpdateStep.Installing;

                if (ustate.Step == UpdateStep.Installed)
                {
                    installedAt ??= DateTime.Now;
                    if (DateTime.Now - installedAt >= (ustate.Detail is null ? ReopenDelay : ReopenDelayWithNotice))
                    {
                        _reopen = true;
                        cts.Cancel();
                    }
                }

                // O indicador de "carregando" precisa girar mesmo sem novas leituras.
                dirty |= Dashboard.IsLoading(local?.Hardware ?? Volatile.Read(ref remoteHardware), cstate, clstate);

                if (dirty && Monitor.TryEnter(sensorGate))
                {
                    try
                    {
                        var hardware = local?.Hardware ?? Volatile.Read(ref remoteHardware);
                        var mode = serviceUp ? SensorMode.Service
                            : link == SensorLink.Outdated ? SensorMode.ServiceOutdated
                            : isAdmin ? SensorMode.Elevated
                            : SensorMode.Limited;

                        var data = new UiData(hardware, cstate, clstate, astate, mode, LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled, ustate, updater.CanInstall);
                        var frame = app.Build(data, size.W, size.H);
                        Screen.DrawRows(frame.Rows, clearFirst || app.TakeClearRequest());
                        dirty = false;
                        clearFirst = false;
                    }
                    finally { Monitor.Exit(sensorGate); }
                }

                // tick curto para reagir rapido a redimensionamento, teclas e mouse
                Thread.Sleep(30);
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            cts.Cancel();
            Screen.Leave();
            local?.Dispose();
        }

        return 0;
    }

    static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    enum SensorChoice { Service, Elevated, Limited, Quit }

    // Sem o servico: explica o motivo antes de qualquer pedido do Windows e deixa continuar sem ele.
    static SensorChoice AskForSensors()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [bold]WF System Monitor[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [grey70]" + T(
            "Para ler temperatura, potência e discos, o monitor usa um pequeno serviço do Windows.",
            "To read temperature, power and disks, the monitor uses a small Windows service.") + "[/]");
        AnsiConsole.MarkupLine("  [grey70]" + T(
            "Instalar pede sua confirmação (UAC) uma única vez; depois, o app abre sem pedir nada.",
            "Installing asks for your confirmation (UAC) once; after that, the app opens without asking.") + "[/]");
        AnsiConsole.WriteLine();

        var choice = StartupUi.Menu(
            [
                T("Instalar o serviço", "Install the service"),
                T("Só desta vez: abrir como administrador", "Just this once: open as administrator"),
                T("Continuar em modo limitado", "Continue in limited mode"),
                T("Sair", "Quit"),
            ],
            T("↑ ↓ escolhe  ·  Enter confirma", "↑ ↓ choose  ·  Enter confirm"),
            fallback: 2,
            recommended: 0,
            recommendedLabel: T("Recomendado", "Recommended"));

        switch (choice)
        {
            case 0:
                var failure = StartupUi.Progress(
                    T("Instalando o serviço; confirme no Windows...", "Installing the service; confirm in Windows..."),
                    ServiceInstaller.RequestAsync(ServiceInstaller.InstallArgument));
                if (failure is null) return SensorChoice.Service;
                Notice(T($"Não instalado ({failure}). Continuando em modo limitado.", $"Not installed ({failure}). Continuing in limited mode."));
                return SensorChoice.Limited;

            case 1:
                if (Relaunch()) return SensorChoice.Elevated;
                Notice(T("Permissão não concedida. Continuando em modo limitado.", "Permission not granted. Continuing in limited mode."));
                return SensorChoice.Limited;

            case 2:
                return SensorChoice.Limited;

            default:
                return SensorChoice.Quit;
        }
    }

    static void Notice(string text)
    {
        AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(text)}[/]");
        Thread.Sleep(1500);
    }

    // Reabre em um console elevado via dotnet.exe (assinado pela Microsoft), disparando o UAC.
    static bool Relaunch()
    {
        var dotnet = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var dll = typeof(Program).Assembly.Location;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = $"\"{dll}\"",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.SystemDirectory,
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;  // o usuario recusou no UAC
        }
    }

    // Instala o PawnIO pelo winget. Devolve null em caso de sucesso, ou o motivo da falha.
    static async Task<string?> InstallDriverAsync()
    {
        const int alreadyInstalled = -1978335189;
        try
        {
            var psi = ExternalTool.PrepareHidden("winget", "install --id namazso.PawnIO -e --silent --accept-source-agreements --accept-package-agreements");
            using var proc = psi is null ? null : Process.Start(psi);
            if (proc is null) return T("winget indisponível; instale o PawnIO em https://pawnio.eu", "winget unavailable; install PawnIO from https://pawnio.eu");

            _ = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return proc.ExitCode is 0 or alreadyInstalled ? null : T($"o winget terminou com o código {proc.ExitCode}", $"winget exited with code {proc.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return T("winget indisponível; instale o PawnIO em https://pawnio.eu", "winget unavailable; install PawnIO from https://pawnio.eu");
        }
    }

    // Devolve null em caso de sucesso, ou o motivo da falha.
    static async Task<string?> LogoutClaudeAsync()
    {
        try
        {
            var psi = ExternalTool.PrepareHidden("claude", "auth logout");
            using var proc = psi is null ? null : Process.Start(psi);
            if (proc is null) return T("falha ao iniciar o Claude Code", "could not start Claude Code");

            _ = proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 ? null : error.Trim();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ex.Message;
        }
    }

    // O login e feito pelo proprio Claude Code, em outra janela.
    static void OpenClaudeLogin()
    {
        try
        {
            if (ExternalTool.Prepare("claude", "auth login") is { } psi)
            {
                psi.UseShellExecute = true;  // abre em uma janela propria
                Process.Start(psi);
            }
        }
        catch (System.ComponentModel.Win32Exception) { /* a aba de contas continua indicando que falta o login */ }
    }
}

// Leitura direta dos sensores, usada quando o servico nao esta disponivel: como administrador le tudo;
// como usuario comum, so o que nao exige privilegio.
sealed class LocalSensors : IDisposable
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);  // a cadencia da tela fica em SensorDisplay

    readonly object _gate;
    readonly CancellationTokenSource _cts = new();
    readonly Computer _computer;
    readonly Task _loop;
    int _version;

    public LocalSensors(object gate)
    {
        _gate = gate;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
        };
        _loop = Task.Run(Loop);
    }

    public IReadOnlyList<IHardware> Hardware
    {
        get
        {
            try { return _computer.Hardware.ToList(); }
            catch (InvalidOperationException) { return []; }  // ainda abrindo
        }
    }

    public int Version => Volatile.Read(ref _version);

    void Loop()
    {
        try
        {
            lock (_gate) _computer.Open();
        }
        catch (Exception)
        {
            return;  // sem acesso aos sensores: os paineis mostram "nao detectado"
        }

        while (!_cts.IsCancellationRequested)
        {
            var started = DateTime.Now;
            try
            {
                lock (_gate) Dashboard.UpdateSensors(_computer);
                Interlocked.Increment(ref _version);
            }
            catch (Exception) { /* falha pontual de um sensor: tenta na proxima leitura */ }

            var wait = Interval - (DateTime.Now - started);
            if (wait > TimeSpan.Zero) _cts.Token.WaitHandle.WaitOne(wait);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_loop.Wait(TimeSpan.FromSeconds(3))) _computer.Close();
        _cts.Dispose();
    }
}
