using System.Diagnostics;

namespace WfSysMon;

// Localiza e prepara os programas externos (claude, gh, winget). O app pode rodar como administrador,
// entao nunca procura na pasta atual: um "claude.cmd" deixado nela rodaria com permissao elevada.
static class ExternalTool
{
    static readonly string[] Extensions = [".exe", ".cmd", ".bat"];

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
}
