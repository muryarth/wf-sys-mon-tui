using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using static WfSysMon.L;

namespace WfSysMon;

enum StatusLineState { NotConfigured, Ours, Other, InvalidSettings }

// Registra a status line do wf-sys-mon no Claude Code (pelo 'wf-sys-mon -Install' ou pela tela de contas).
// Nao sobrescreve uma status line existente e nao altera um settings.json invalido.
static class ClaudeSetup
{
    public const string Argument = "--setup-claude-statusline";

    const string ScriptName = "claude-statusline.ps1";

    public static string ConfigDir =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    static string SettingsPath => Path.Combine(ConfigDir, "settings.json");

    // O script e copiado para a pasta do app na compilacao.
    public static string BundledScript => Path.Combine(AppContext.BaseDirectory, ScriptName);

    public static StatusLineState State()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return StatusLineState.NotConfigured;
            if (JsonNode.Parse(File.ReadAllText(SettingsPath)) is not JsonObject settings) return StatusLineState.InvalidSettings;
            if (settings["statusLine"] is not { } existing) return StatusLineState.NotConfigured;

            var command = existing is JsonObject o && o["command"] is JsonValue v && v.TryGetValue<string>(out var c) ? c : "";
            return command.Contains(ScriptName, StringComparison.OrdinalIgnoreCase) ? StatusLineState.Ours : StatusLineState.Other;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return StatusLineState.InvalidSettings;
        }
    }

    public static int Run(string scriptSource)
    {
        var (ok, message) = Apply(scriptSource);
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    public static (bool Ok, string Message) Apply(string scriptSource)
    {
        try
        {
            if (!File.Exists(scriptSource))
                return (false, T($"Script não encontrado: {scriptSource}", $"Script not found: {scriptSource}"));

            var state = State();
            if (state == StatusLineState.InvalidSettings)
                return (false, T($"{SettingsPath} é inválido; nada foi alterado.", $"{SettingsPath} is invalid; nothing was changed."));

            var dataDir = AppPaths.DataDir;
            Directory.CreateDirectory(dataDir);
            var script = Path.Combine(dataDir, ScriptName);
            File.Copy(scriptSource, script, overwrite: true);

            if (state == StatusLineState.Ours)
                return (true, T("Status line do wf-sys-mon já configurada.", "wf-sys-mon status line already configured."));
            if (state == StatusLineState.Other)
                return (true, T("Já existe outra status line no Claude Code; ela foi mantida. A cota do Claude não aparecerá sem a status line do wf-sys-mon.",
                    "Claude Code already has another status line; it was kept. The Claude quota will not appear without the wf-sys-mon status line."));

            var settings = File.Exists(SettingsPath) ? (JsonObject)JsonNode.Parse(File.ReadAllText(SettingsPath))! : [];

            // Barras normais: no Windows o Claude Code pode rodar o comando pelo Git Bash.
            settings["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script.Replace('\\', '/')}\"",
            };

            Directory.CreateDirectory(ConfigDir);
            if (File.Exists(SettingsPath)) File.Copy(SettingsPath, SettingsPath + ".wf-sys-mon.bak", overwrite: true);

            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            var tmp = SettingsPath + ".wf-sys-mon.tmp";
            File.WriteAllText(tmp, settings.ToJsonString(options));
            File.Move(tmp, SettingsPath, overwrite: true);

            return (true, T("Status line configurada. Reinicie o Claude Code para ativar.", "Status line configured. Restart Claude Code to activate it."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (false, T($"Falha ao configurar a status line: {ex.Message}", $"Could not configure the status line: {ex.Message}"));
        }
    }
}
