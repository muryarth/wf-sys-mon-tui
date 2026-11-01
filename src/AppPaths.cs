namespace WfSysMon;

// Pasta dos dados do usuario. Os testes apontam para uma pasta temporaria antes de qualquer leitura.
static class AppPaths
{
    internal static string DataDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wf-sys-mon");
}
