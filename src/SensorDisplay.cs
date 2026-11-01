using LibreHardwareMonitor.Hardware;

namespace WfSysMon;

// Valores mostrados na tela. Os sensores oscilam a cada leitura, entao os numeros so mudam a cada
// DisplaySeconds leituras (uma por segundo) e mostram a media das ultimas Window: ficam legiveis e ainda
// reagem em poucos segundos. As barras seguem uma mola criticamente amortecida: aceleram, desaceleram,
// nao passam do alvo e, se o valor mudar no meio do caminho, mudam de rumo sem tranco. Minimo e maximo continuam
// vindo das leituras brutas (ISensor.Min/Max), sem suavizacao.
// Nao e thread-safe: tudo aqui roda sob o mesmo lock dos sensores (veja Program).
static class SensorDisplay
{
    const int Window = 3;
    const float SettleDistance = 0.2f;  // diferenca invisivel na barra: encerra a animacao
    const float SettleSpeed = 2f;       // pontos percentuais por segundo

    static readonly Dictionary<string, Queue<float>> Samples = [];
    static readonly Dictionary<string, float> Shown = [];
    static readonly Dictionary<string, float> Bars = [];
    static readonly Dictionary<string, float> Speeds = [];
    static readonly HashSet<string> BarIds = [];
    static readonly Dictionary<string, float> Minimums = [];
    static int _reads;

    internal static void Reset()
    {
        Samples.Clear();
        Shown.Clear();
        Bars.Clear();
        Speeds.Clear();
        BarIds.Clear();
        Minimums.Clear();
        _reads = 0;
    }

    public static void Update(IEnumerable<IHardware> hardware)
    {
        foreach (var hw in hardware)
            Collect(hw);
        Publish();
    }

    static void Collect(IHardware hw)
    {
        foreach (var sensor in hw.Sensors)
        {
            if (!Smoothed(sensor.SensorType)) continue;
            var id = sensor.Identifier.ToString();
            if (sensor.SensorType == SensorType.Load) BarIds.Add(id);
            if (sensor.SensorType == SensorType.Temperature && sensor.Value is { } reading && reading > 0)
                Minimums[id] = Math.Min(Minimums.GetValueOrDefault(id, float.MaxValue), reading);
            Add(id, sensor.Value);
        }
        foreach (var sub in hw.SubHardware)
            Collect(sub);
    }

    static bool Smoothed(SensorType type) =>
        type is SensorType.Temperature or SensorType.Load or SensorType.Clock or SensorType.Power or SensorType.Fan;

    internal static void Add(string id, float? value)
    {
        if (value is not { } v) return;
        if (!Samples.TryGetValue(id, out var queue)) Samples[id] = queue = new Queue<float>(Window);
        if (queue.Count == Window) queue.Dequeue();
        queue.Enqueue(v);
    }

    // A primeira leitura ja aparece, para a tela nao abrir vazia.
    internal static void Publish()
    {
        if (_reads++ % Settings.Current.DisplaySeconds != 0) return;
        foreach (var (id, queue) in Samples)
            Shown[id] = queue.Average();
    }

    // Quanto a barra leva para responder. Com leituras raras o movimento pode ser mais lento, sem
    // deixar de chegar antes da leitura seguinte; o tempo total e cerca de 4 vezes este valor.
    internal static double SmoothSeconds(int displaySeconds) => displaySeconds switch
    {
        <= 1 => 0.18,
        2 => 0.3,
        _ => 0.6,
    };

    // Avanca as barras pela solucao exata da mola, entao o resultado nao depende da taxa de quadros.
    // Devolve true enquanto alguma ainda estiver em movimento.
    public static bool Animate(double elapsedSeconds)
    {
        var omega = 2 / SmoothSeconds(Settings.Current.DisplaySeconds);
        var decay = Math.Exp(-omega * elapsedSeconds);
        var moved = false;
        foreach (var id in BarIds)
        {
            if (!Shown.TryGetValue(id, out var target)) continue;
            if (!Bars.TryGetValue(id, out var bar))
            {
                Bars[id] = target;  // primeira leitura: a barra ja nasce no valor
                moved = true;
                continue;
            }
            var speed = Speeds.GetValueOrDefault(id);
            if (bar == target && speed == 0) continue;

            double offset = bar - target;
            var c = speed + omega * offset;
            var nextOffset = (offset + c * elapsedSeconds) * decay;
            var nextSpeed = (c - omega * (offset + c * elapsedSeconds)) * decay;

            if (Math.Abs(nextOffset) < SettleDistance && Math.Abs(nextSpeed) < SettleSpeed)
            {
                Bars[id] = target;
                Speeds[id] = 0;
            }
            else
            {
                Bars[id] = Math.Clamp(target + (float)nextOffset, 0, 100);
                Speeds[id] = (float)nextSpeed;
            }
            moved = true;
        }
        return moved;
    }

    internal static void TrackBar(string id) => BarIds.Add(id);

    internal static float? Get(string id) => Shown.TryGetValue(id, out var v) ? v : null;

    internal static float? GetBar(string id) => Bars.TryGetValue(id, out var v) ? v : null;

    public static float? Get(ISensor? sensor) =>
        sensor is null ? null : Smoothed(sensor.SensorType) ? Get(sensor.Identifier.ToString()) ?? sensor.Value : sensor.Value;

    // Menor leitura valida: alguns sensores, como os de disco, registram 0 na primeira leitura.
    public static float? Min(ISensor sensor) =>
        Minimums.TryGetValue(sensor.Identifier.ToString(), out var v) ? v : sensor.Min;

    public static float? Bar(ISensor? sensor) =>
        sensor is null ? null : GetBar(sensor.Identifier.ToString());
}
