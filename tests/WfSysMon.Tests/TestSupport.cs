using System.Reflection;
using System.Runtime.CompilerServices;
using LibreHardwareMonitor.Hardware;
using WfSysMon;
using Xunit;
using Xunit.Sdk;

// Settings e SensorDisplay guardam estado estatico.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

static class TestEnvironment
{
    // Roda antes de qualquer classe do app: os testes nunca tocam nos dados reais do usuario.
    [ModuleInitializer]
    internal static void Init()
    {
        AppPaths.DataDir = Path.Combine(Path.GetTempPath(), "wf-sys-mon-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(AppPaths.DataDir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(AppPaths.DataDir, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }
}

sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wf-sys-mon-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

// Roteiros longos continuam depois de uma falha e relatam todas no final.
sealed class CheckList
{
    readonly List<string> _failures = [];

    public void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _failures.Add(detail.Length > 0 ? $"{name}: {detail}" : name);
    }

    public void AssertAllPassed()
    {
        if (_failures.Count > 0)
            throw new XunitException($"{_failures.Count} falha(s):\n" + string.Join("\n", _failures));
    }
}

// Sensores falsos: os testes nao dependem do hardware nem de permissao de administrador.
class Fake : DispatchProxy
{
    public Dictionary<string, object?> Values = [];

    protected override object? Invoke(MethodInfo? m, object?[]? args)
    {
        if (m is null) return null;
        var name = m.Name.StartsWith("get_") ? m.Name[4..] : m.Name;
        if (Values.TryGetValue(name, out var v)) return v;
        return m.ReturnType.IsValueType && m.ReturnType != typeof(void) ? Activator.CreateInstance(m.ReturnType) : null;
    }
}

static class SensorFakes
{
    static int _n;

    public static ISensor S(string name, SensorType type, float? value, float? min = null, float? max = null)
    {
        var s = DispatchProxy.Create<ISensor, Fake>();
        ((Fake)(object)s).Values = new()
        {
            ["Name"] = name, ["SensorType"] = type, ["Value"] = value, ["Min"] = min ?? value, ["Max"] = max ?? value,
            ["Identifier"] = new Identifier("fake", (_n++).ToString()),
        };
        return s;
    }

    public static IHardware H(string name, HardwareType type, params ISensor[] sensors)
    {
        var h = DispatchProxy.Create<IHardware, Fake>();
        ((Fake)(object)h).Values = new() { ["Name"] = name, ["HardwareType"] = type, ["Sensors"] = sensors, ["SubHardware"] = Array.Empty<IHardware>() };
        return h;
    }
}
