using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using LibreHardwareMonitor.Hardware;
using static WfSysMon.L;

namespace WfSysMon;

// Servico do Windows que le os sensores com privilegio e so publica os valores. Assim a TUI roda como
// usuario comum, sem pedir permissao ao abrir, e nada mais do app roda elevado.
sealed class SensorService(SecurityIdentifier allowedUser) : ServiceBase
{
    Computer? _computer;
    SensorServer? _server;

    protected override void OnStart(string[] args)
    {
        _server = new SensorServer(Sample, Open, Close, allowedUser);
        _server.Failed += message => EventLog.WriteEntry(message, EventLogEntryType.Warning);
        _server.Start();
    }

    protected override void OnStop()
    {
        _server?.Dispose();
        _server = null;
    }

    void Open()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
        };
        _computer.Open();
    }

    void Close()
    {
        _computer?.Close();
        _computer = null;
    }

    List<HardwareDto> Sample()
    {
        foreach (var hw in _computer!.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
        }
        return SensorProtocol.Snapshot(_computer.Hardware);
    }
}

// Instala, atualiza e remove o servico. Os arquivos ficam em Program Files, onde programas comuns nao
// escrevem: e isso que impede trocar o codigo que roda com privilegio.
static class ServiceInstaller
{
    public const string ServiceName = "WfSysMonSensors";
    public const string RunArgument = "--service";
    public const string AllowArgument = "--allow";
    public const string InstallArgument = "--install-service";
    public const string UninstallArgument = "--uninstall-service";

    static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "wf-sys-mon");
    static string DotnetPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
    static string Sc => Path.Combine(Environment.SystemDirectory, "sc.exe");

    // Parte que roda sem privilegio: pede o UAC uma vez e espera o resultado.
    public static async Task<string?> RequestAsync(string argument)
    {
        var dll = typeof(ServiceInstaller).Assembly.Location;
        var user = WindowsIdentity.GetCurrent().User!.Value;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = DotnetPath,
                Arguments = $"\"{dll}\" {argument} {user}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.SystemDirectory,
            });
            if (proc is null) return T("falha ao iniciar", "could not start");

            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 ? null : T($"código {proc.ExitCode}", $"code {proc.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return T("permissão não concedida", "permission not granted");
        }
    }

    // Parte que roda como administrador.
    public static int Install(string userSid)
    {
        var user = new SecurityIdentifier(userSid);  // valida antes de usar na linha de comando do servico

        RemoveService();
        CopyFiles();

        var dll = Path.Combine(InstallDir, Path.GetFileName(typeof(ServiceInstaller).Assembly.Location));
        var command = $"\"{DotnetPath}\" \"{dll}\" {RunArgument} {AllowArgument} {user.Value}";

        var code = RunSc("create", ServiceName, "binPath=", command, "start=", "delayed-auto", "DisplayName=", "WF System Monitor Sensors");
        if (code != 0) return code;

        RunSc("description", ServiceName, "Reads hardware sensors for WF System Monitor. Only publishes values to the signed-in user.");
        RunSc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000//");
        return RunSc("start", ServiceName);
    }

    public static int Uninstall()
    {
        RemoveService();
        for (var i = 0; i < 10 && Directory.Exists(InstallDir); i++)
        {
            try { Directory.Delete(InstallDir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
        }
        return Directory.Exists(InstallDir) ? 1 : 0;
    }

    static void RemoveService()
    {
        if (RunSc("query", ServiceName) != 0) return;  // nao instalado

        RunSc("stop", ServiceName);
        for (var i = 0; i < 20; i++)
        {
            if (Status() is "STOPPED" or null) break;
            Thread.Sleep(500);
        }
        RunSc("delete", ServiceName);

        // O Windows so conclui a remocao depois que o nome deixa de existir.
        for (var i = 0; i < 20 && RunSc("query", ServiceName) == 0; i++) Thread.Sleep(250);
    }

    static string? Status()
    {
        var output = RunScOutput("query", ServiceName);
        if (output is null) return null;
        return output.Contains("STOPPED") ? "STOPPED" : "OTHER";
    }

    static void CopyFiles()
    {
        var source = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (string.Equals(source, InstallDir, StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(InstallDir);

        // ".old": versoes substituidas por uma atualizacao, ainda nao apagadas.
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".old", StringComparison.OrdinalIgnoreCase)))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(InstallDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    static int RunSc(params string[] args) => RunScCore(args).Code;

    static string? RunScOutput(params string[] args)
    {
        var (code, output) = RunScCore(args);
        return code == 0 ? output : null;
    }

    static (int Code, string Output) RunScCore(string[] args)
    {
        var info = new ProcessStartInfo(Sc)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        using var proc = Process.Start(info)!;
        _ = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, output);
    }
}
