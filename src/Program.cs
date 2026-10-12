using System.Diagnostics;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Spectre.Console;

namespace WfSysMon;

static class Program
{
    static int Main(string[] args)
    {
        if (!IsAdministrator())
            return RelaunchElevated();

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
        };

        try
        {
            computer.Open();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Falha ao abrir sensores:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using var copilot = new CopilotUsage();
        using var auth = new GitHubAuth();
        auth.Completed += copilot.ResetToken;
        copilot.Start();

        Screen.Enter();
        try
        {
            var lastSize = (W: -1, H: -1);
            var lastCopilotVersion = -1;
            var lastAuth = auth.State;
            var nextSensorUpdate = DateTime.MinValue;

            while (!cts.IsCancellationRequested)
            {
                var dirty = false;

                if (DateTime.Now >= nextSensorUpdate)
                {
                    Dashboard.UpdateSensors(computer);
                    nextSensorUpdate = DateTime.Now.AddSeconds(1);
                    dirty = true;
                }

                var size = Screen.Size();
                var resized = size != lastSize;
                if (resized) lastSize = size;

                var cstate = copilot.State;
                if (cstate.Version != lastCopilotVersion)
                {
                    lastCopilotVersion = cstate.Version;
                    dirty = true;
                }

                var astate = auth.State;
                if (!ReferenceEquals(astate, lastAuth))
                {
                    lastAuth = astate;
                    dirty = true;
                }

                if (dirty || resized)
                    Screen.Draw(Dashboard.Render(computer, cstate, astate, size.W - 1, size.H), size.W, size.H, clearFirst: resized);

                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    if (key is ConsoleKey.Q or ConsoleKey.Escape) cts.Cancel();
                    else if (key is ConsoleKey.R) copilot.RefreshNow();
                    else if (key is ConsoleKey.L && copilot.State.NeedsLogin) auth.Start();
                }

                // tick curto para reagir rapido a redimensionamento e teclas
                Thread.Sleep(100);
            }
        }
        finally
        {
            Screen.Leave();
            computer.Close();
        }

        return 0;
    }

    static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Reabre a si mesmo em um console elevado via dotnet.exe (assinado pela Microsoft),
    // disparando o prompt UAC. Necessario para carregar o driver de sensores.
    static int RelaunchElevated()
    {
        var dotnet = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var dll = typeof(Program).Assembly.Location;

        AnsiConsole.MarkupLine("[yellow]Leitura de sensores requer administrador - solicitando permissao (UAC)...[/]");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = $"\"{dll}\"",
                UseShellExecute = true,
                Verb = "runas",
            });
            return 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            AnsiConsole.MarkupLine("[red]Permissao negada.[/] Execute em um terminal como administrador.");
            return 1;
        }
    }
}
