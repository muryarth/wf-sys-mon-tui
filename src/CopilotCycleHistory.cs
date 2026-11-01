using System.Text.Json;

namespace WfSysMon;

record CopilotCycle(double Used, double Entitlement, double ExtraBudget, bool TokenBasedBilling);

// A API do Copilot so informa o ciclo atual e zera na renovacao. Por isso o app guarda a ultima leitura
// de cada ciclo em %LOCALAPPDATA%\wf-sys-mon\copilot-cycles.json, indexada pela data de renovacao.
// Vale a ultima leitura feita com o app aberto: uso posterior a ela no mesmo ciclo nao entra.
sealed class CopilotCycleHistory(string? path = null)
{
    readonly string _path = path ?? Path.Combine(AppPaths.DataDir, "copilot-cycles.json");

    Dictionary<string, CopilotCycle>? _cycles;

    // Registra a leitura e devolve o ciclo anterior ao dela, se houver.
    public CopilotCycle? Record(CopilotSnapshot snapshot)
    {
        if (snapshot.ResetDate is not { } reset) return null;

        _cycles ??= Load();
        var key = reset.ToString("yyyy-MM-dd");

        // O uso de um ciclo so cresce; os limites valem os da leitura mais recente.
        var used = Math.Max(_cycles.GetValueOrDefault(key)?.Used ?? 0, snapshot.Used);
        var current = new CopilotCycle(used, snapshot.Entitlement, snapshot.ExtraBudget, snapshot.TokenBasedBilling);
        if (_cycles.GetValueOrDefault(key) != current)
        {
            _cycles[key] = current;
            Save();
        }

        return _cycles
            .Where(c => string.CompareOrdinal(c.Key, key) < 0)
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .LastOrDefault().Value;
    }

    Dictionary<string, CopilotCycle> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Dictionary<string, CopilotCycle>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // arquivo corrompido: recomeca a partir da proxima leitura
        }
        return [];
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cycles));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // sem persistencia nesta rodada: tenta de novo na proxima leitura que mudar algo
        }
    }
}
