using LibreHardwareMonitor.Hardware;

namespace WfSysMon;

// Dados que o servico de sensores envia a TUI, uma linha JSON por leitura.
record SensorDto(string Id, string Name, SensorType Type, float? Value, float? Min, float? Max);

record HardwareDto(string Id, string Name, HardwareType Type, List<SensorDto> Sensors, List<HardwareDto> Sub);

record SensorFrame(int Protocol, List<HardwareDto> Hardware);

static class SensorProtocol
{
    public const string PipeName = "wf-sys-mon-sensors";

    // Aumente ao mudar o formato: a TUI avisa que o servico instalado precisa ser atualizado.
    public const int Version = 1;

    public static List<HardwareDto> Snapshot(IEnumerable<IHardware> hardware) => hardware.Select(Convert).ToList();

    static HardwareDto Convert(IHardware h) => new(
        h.Identifier.ToString(),
        h.Name,
        h.HardwareType,
        h.Sensors.Select(s => new SensorDto(s.Identifier.ToString(), s.Name, s.SensorType, s.Value, s.Min, s.Max)).ToList(),
        h.SubHardware.Select(Convert).ToList());
}

// Hardware e sensores recebidos do servico, com a mesma interface dos locais: os paineis nao
// precisam saber de onde vem os dados. Sao so leitura.
sealed class RemoteHardware : IHardware
{
    public RemoteHardware(HardwareDto dto, IHardware? parent = null)
    {
        Identifier = RemoteSensor.ParseIdentifier(dto.Id);
        Name = dto.Name;
        HardwareType = dto.Type;
        Parent = parent!;
        Sensors = dto.Sensors.Select(s => (ISensor)new RemoteSensor(s, this)).ToArray();
        SubHardware = dto.Sub.Select(s => (IHardware)new RemoteHardware(s, this)).ToArray();
    }

    public HardwareType HardwareType { get; }
    public Identifier Identifier { get; }
    public string Name { get; set; }
    public IHardware Parent { get; }
    public ISensor[] Sensors { get; }
    public IHardware[] SubHardware { get; }
    public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();

    public event SensorEventHandler SensorAdded { add { } remove { } }
    public event SensorEventHandler SensorRemoved { add { } remove { } }

    public string GetReport() => "";
    public void Update() { }
    public void Accept(IVisitor visitor) { }
    public void Traverse(IVisitor visitor) { }
}

sealed class RemoteSensor(SensorDto dto, IHardware hardware) : ISensor
{
    public static Identifier ParseIdentifier(string id) =>
        new(id.Split('/', StringSplitOptions.RemoveEmptyEntries));

    public IControl Control => null!;
    public IHardware Hardware { get; } = hardware;
    public Identifier Identifier { get; } = ParseIdentifier(dto.Id);
    public int Index => 0;
    public bool IsDefaultHidden => false;
    public float? Max { get; } = dto.Max;
    public float? Min { get; } = dto.Min;
    public string Name { get; set; } = dto.Name;
    public IReadOnlyList<IParameter> Parameters => [];
    public SensorType SensorType { get; } = dto.Type;
    public float? Value { get; } = dto.Value;
    public IEnumerable<SensorValue> Values => [];
    public TimeSpan ValuesTimeWindow { get; set; }

    public void ResetMin() { }
    public void ResetMax() { }
    public void ClearValues() { }
    public void Accept(IVisitor visitor) { }
    public void Traverse(IVisitor visitor) { }
}
