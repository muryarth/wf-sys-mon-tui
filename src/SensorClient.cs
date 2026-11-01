using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

namespace WfSysMon;

enum SensorLink { Disconnected, Connecting, Connected, Outdated, Denied, Untrusted }

// Recebe as leituras do servico de sensores e reconecta sozinho se ele reiniciar.
sealed class SensorClient(
    Action<IReadOnlyList<IHardware>> onFrame,
    string pipeName = SensorProtocol.PipeName,
    IReadOnlyCollection<SecurityIdentifier>? trustedOwners = null) : IDisposable
{
    // Criar esse pipe como SYSTEM ou administrador exige privilegio: um programa comum nao se passa pelo servico.
    static readonly SecurityIdentifier[] DefaultOwners =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
    ];

    readonly IReadOnlyCollection<SecurityIdentifier> _owners = trustedOwners ?? DefaultOwners;
    readonly CancellationTokenSource _cts = new();
    volatile SensorLink _link = SensorLink.Disconnected;
    Task? _loop;

    public SensorLink Link => _link;

    public void Start() => _loop = Task.Run(RunAsync);

    // Espera um pouco pela conexao, para a TUI ja abrir com o servico.
    public bool WaitForLink(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_link is not (SensorLink.Connected or SensorLink.Connecting) && DateTime.UtcNow < deadline) Thread.Sleep(20);
        return _link is SensorLink.Connected or SensorLink.Connecting;
    }

    async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // Anonymous: quem responder no pipe nao consegue agir em nome do usuario.
                using var pipe = new NamedPipeClientStream(".", pipeName,
                    PipeAccessRights.Read | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous, HandleInheritability.None);

                await pipe.ConnectAsync(500, _cts.Token);

                if (!IsTrusted(pipe))
                {
                    _link = SensorLink.Untrusted;
                }
                else
                {
                    _link = SensorLink.Connecting;  // o servico abre os sensores na primeira conexao
                    await ReadFramesAsync(pipe);
                    if (_link is SensorLink.Connected or SensorLink.Connecting) _link = SensorLink.Disconnected;
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (TimeoutException)
            {
                _link = SensorLink.Disconnected;
            }
            catch (UnauthorizedAccessException)
            {
                _link = SensorLink.Denied;  // instalado para outra conta do Windows
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
            {
                _link = SensorLink.Disconnected;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    bool IsTrusted(NamedPipeClientStream pipe)
    {
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner is not null && _owners.Contains(owner);
    }

    async Task ReadFramesAsync(NamedPipeClientStream pipe)
    {
        using var reader = new StreamReader(pipe);
        while (!_cts.IsCancellationRequested)
        {
            // Sem leitura por 15 s (o servico abre os sensores na primeira conexao): reconecta.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) return;

            var frame = JsonSerializer.Deserialize<SensorFrame>(line);
            if (frame is null) continue;

            if (frame.Protocol != SensorProtocol.Version)
            {
                _link = SensorLink.Outdated;
                continue;
            }

            onFrame(frame.Hardware.Select(h => (IHardware)new RemoteHardware(h)).ToList());
            _link = SensorLink.Connected;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* encerrando */ }
    }
}
