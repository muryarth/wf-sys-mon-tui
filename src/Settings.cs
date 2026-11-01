using System.Text.Json;

namespace WfSysMon;

// Textos da interface: T("portugues", "english"), avaliado na hora de desenhar.
static class L
{
    public static string T(string pt, string en) => Settings.Current.Language == "en" ? en : pt;
}

// Preferencias do usuario, salvas em %LOCALAPPDATA%\wf-sys-mon\settings.json.
sealed class Settings
{
    public static readonly int[] DisplayOptions = [1, 2, 5];
    public static readonly int[] CopilotOptions = [30, 60, 120];

    static readonly string FilePath = Path.Combine(AppPaths.DataDir, "settings.json");

    public static Settings Current { get; private set; } = Load();

    public string Language { get; set; } = "en";
    public bool Icons { get; set; } = true;
    public bool HardwareCollapsed { get; set; }
    public bool AiCollapsed { get; set; }
    public int DisplaySeconds { get; set; } = 2;
    public int CopilotSeconds { get; set; } = 30;
    public bool SetupSeen { get; set; }
    public bool ShowCopilot { get; set; } = true;
    public bool ShowClaude { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool AnyAgentVisible => ShowCopilot || ShowClaude;

    static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) is { } saved)
            {
                saved.Language = saved.Language == "en" ? "en" : "pt";
                if (!DisplayOptions.Contains(saved.DisplaySeconds)) saved.DisplaySeconds = 2;
                if (!CopilotOptions.Contains(saved.CopilotSeconds)) saved.CopilotSeconds = 30;
                return saved;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // arquivo invalido: volta ao padrao
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // sem persistencia: a preferencia vale ate fechar o app
        }
    }

}

// Icones em ASCII, no estilo do terminal e sem risco de desalinhar as bordas.
static class Icons
{
    static string I(string icon) => Settings.Current.Icons ? icon + " " : "";

    public static string Cpu => I("[#]");
    public static string Ram => I("[=]");
    public static string Gpu => I("[%]");
    public static string Board => I("[:]");
    public static string Disk => I("[o]");
    public static string Copilot => I("<^>");
    public static string Claude => I("<*>");
}
