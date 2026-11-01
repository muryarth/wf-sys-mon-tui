using System.Management;

namespace WfSysMon;

record DiskSpace(double UsedGb, double FreeGb, double TotalGb);

// Espaco de cada disco fisico, somando os volumes com letra. Os sensores so informam o percentual
// usado; os tamanhos vem do Windows (WMI), lidos em segundo plano porque a consulta e lenta.
static class DiskVolumes
{
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    const double BytesPerGb = 1024d * 1024 * 1024;

    static volatile Dictionary<string, DiskSpace> _spaces = [];

    public static DiskSpace? Find(string model) => _spaces.GetValueOrDefault(model.Trim());

    public static void Start(CancellationToken ct) => _ = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try { _spaces = Read(); }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                // mantem a ultima leitura; o painel usa o percentual dos sensores
            }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }, ct);

    // Cruza as tabelas de associacao, em vez de ASSOCIATORS OF, que falha em particoes sem volume:
    // disco -> particao -> volume com letra.
    internal static Dictionary<string, DiskSpace> Read()
    {
        var models = Rows("SELECT DeviceID, Model FROM Win32_DiskDrive")
            .ToDictionary(d => (string)d["DeviceID"]!, d => ((d["Model"] as string) ?? "").Trim());
        var volumes = Rows("SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk")
            .ToDictionary(v => (string)v["DeviceID"]!, v => (Size: Convert.ToDouble(v["Size"] ?? 0), Free: Convert.ToDouble(v["FreeSpace"] ?? 0)));

        var partitionToModel = new Dictionary<string, string>();
        foreach (var link in Rows("SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition"))
            if (models.TryGetValue(DeviceId(link["Antecedent"]), out var model))
                partitionToModel[DeviceId(link["Dependent"])] = model;

        var totals = new Dictionary<string, (double Size, double Free)>();
        foreach (var link in Rows("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
        {
            if (!partitionToModel.TryGetValue(DeviceId(link["Antecedent"]), out var model)) continue;
            if (!volumes.TryGetValue(DeviceId(link["Dependent"]), out var volume)) continue;

            var current = totals.GetValueOrDefault(model);
            totals[model] = (current.Size + volume.Size, current.Free + volume.Free);
        }

        return totals
            .Where(t => t.Value.Size > 0)
            .ToDictionary(t => t.Key, t => new DiskSpace((t.Value.Size - t.Value.Free) / BytesPerGb, t.Value.Free / BytesPerGb, t.Value.Size / BytesPerGb));
    }

    static List<Dictionary<string, object?>> Rows(string query)
    {
        using var searcher = new ManagementObjectSearcher(query);
        using var results = searcher.Get();
        var rows = new List<Dictionary<string, object?>>();
        foreach (var item in results)
        {
            using (item)
                rows.Add(item.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value));
        }
        return rows;
    }

    // \\PC\root\cimv2:Win32_DiskPartition.DeviceID="Disk #0, Partition #0" -> Disk #0, Partition #0
    static string DeviceId(object? reference)
    {
        var text = reference as string ?? "";
        var start = text.IndexOf('"');
        return start < 0 ? "" : text[(start + 1)..text.LastIndexOf('"')].Replace("\\\\", "\\");
    }
}
