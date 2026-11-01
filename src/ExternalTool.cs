using System.Diagnostics;

namespace WfSysMon;

// Localiza e prepara os programas externos (claude, gh, winget). O app pode rodar como administrador,
// entao nunca procura na pasta atual: um "claude.cmd" deixado nela rodaria com permissao elevada.
static class ExternalTool
{
    static readonly string[] Extensions = [".exe", ".cmd", ".bat"];

    // Instalacoes pelo winget: baixam da internet e podem demorar, mas nao para sempre.
    public static readonly TimeSpan InstallLimit = TimeSpan.FromMinutes(10);

    // Caminho completo da ferramenta nas pastas conhecidas e nas entradas absolutas do PATH.
    public static string? Find(string name, params string[] knownDirs)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = knownDirs.Concat(path.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim().Trim('"')));

        foreach (var dir in dirs.Where(Path.IsPathRooted))
        {
            foreach (var extension in Extensions)
            {
                var candidate = Path.Combine(dir, name + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    // Prepara a execucao a partir de uma pasta do sistema. Devolve null se a ferramenta nao existir.
    public static ProcessStartInfo? Prepare(string name, string arguments, params string[] knownDirs)
    {
        if (Find(name, knownDirs) is not { } tool) return null;

        // /d ignora comandos automaticos do cmd; arquivos .cmd/.bat so rodam por meio dele.
        var isExe = tool.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var info = isExe
            ? new ProcessStartInfo(tool, arguments)
            : new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"\"{tool}\" {arguments}\"");

        info.WorkingDirectory = Environment.SystemDirectory;
        return info;
    }

    // Execucao oculta com a saida capturada.
    public static ProcessStartInfo? PrepareHidden(string name, string arguments, params string[] knownDirs)
    {
        var info = Prepare(name, arguments, knownDirs);
        if (info is null) return null;

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        return info;
    }

    // Roda ate o fim e devolve a saida. Devolve null se o programa nao iniciar. Passado o tempo limite,
    // encerra o programa e o que ele abriu; sem limite, espera o tempo que for (como um pedido de UAC).
    public static async Task<ToolResult?> RunAsync(ProcessStartInfo info, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        Process? process;
        try { process = Process.Start(info); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        if (process is null) return null;

        using (process)
        {
            if (info.RedirectStandardInput) process.StandardInput.Close();
            var output = info.RedirectStandardOutput ? process.StandardOutput.ReadToEndAsync(CancellationToken.None) : Task.FromResult("");
            var error = info.RedirectStandardError ? process.StandardError.ReadToEndAsync(CancellationToken.None) : Task.FromResult("");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            if (timeout is { } limit) deadline.CancelAfter(limit);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* ja encerrado */ }

                cancel.ThrowIfCancellationRequested();
                return new ToolResult(-1, "", "", TimedOut: true);
            }

            // Um programa aberto por ele pode manter a saida aberta depois do fim: nao espera por isso.
            try { await Task.WhenAll(output, error).WaitAsync(OutputDrainLimit, cancel); }
            catch (Exception ex) when (ex is TimeoutException or IOException) { }

            return new ToolResult(process.ExitCode, Finished(output), Finished(error));
        }
    }

    static readonly TimeSpan OutputDrainLimit = TimeSpan.FromSeconds(5);

    static string Finished(Task<string> text) => text.IsCompletedSuccessfully ? text.Result : "";
}

sealed record ToolResult(int ExitCode, string Output, string Error, bool TimedOut = false);
