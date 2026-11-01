using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace WfSysMon;

// Publica as leituras por um pipe local. So envia: o canal e de mao unica e nao ha comando que a TUI
// possa mandar. So a conta autorizada na instalacao consegue ler. Le os sensores apenas enquanto
// houver alguma janela conectada, e fecha o acesso a eles quando fica ociosa.
sealed class SensorServer(
    Func<List<HardwareDto>> sample,
    Action open,
    Action close,
    SecurityIdentifier allowedUser,
    string pipeName = SensorProtocol.PipeName,
    TimeSpan? interval = null,
    TimeSpan? idleLimit = null) : IDisposable
{
    const int MaxClients = 8;

    readonly TimeSpan _interval = interval ?? TimeSpan.FromSeconds(1);
    readonly TimeSpan _idleLimit = idleLimit ?? TimeSpan.FromSeconds(30);
    readonly CancellationTokenSource _cts = new();
    readonly object _gate = new();
    readonly List<NamedPipeServerStream> _clients = [];
    Task? _accept;
    Task? _publish;

    public event Action<string>? Failed;

    public void Start()
    {
        _accept = Task.Run(AcceptLoopAsync);
        _publish = Task.Run(PublishLoopAsync);
    }

    NamedPipeServerStream CreateInstance(bool first)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (allowedUser != WindowsIdentity.GetCurrent().User)
            security.AddAccessRule(new PipeAccessRule(allowedUser, PipeAccessRights.Read | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize, AccessControlType.Allow));

        // FirstPipeInstance: se outro programa ja criou esse nome, falha em vez de dividir o canal.
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.Out, MaxClients, PipeTransmissionMode.Byte, options, 0, 1 << 16, security);
    }

    async Task AcceptLoopAsync()
    {
        var first = true;
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateInstance(first);
                first = false;
                await pipe.WaitForConnectionAsync(_cts.Token);
                lock (_gate) _clients.Add(pipe);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                pipe?.Dispose();

                // Limite de janelas atingido, ou nome ocupado por outro programa: tenta de novo em seguida.
                Failed?.Invoke(ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    async Task PublishLoopAsync()
    {
        var opened = false;
        var lastClient = DateTime.UtcNow;

        while (!_cts.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            List<NamedPipeServerStream> clients;
            lock (_gate) clients = _clients.ToList();

            if (clients.Count > 0)
            {
                lastClient = started;
                try
                {
                    if (!opened)
                    {
                        open();
                        opened = true;
                    }

                    var line = JsonSerializer.Serialize(new SensorFrame(SensorProtocol.Version, sample())) + "\n";
                    var bytes = Encoding.UTF8.GetBytes(line);
                    await Task.WhenAll(clients.Select(c => SendAsync(c, bytes)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Failed?.Invoke(ex.Message);  // falha pontual de um sensor: tenta na proxima leitura
                }
            }
            else if (opened && started - lastClient >= _idleLimit)
            {
                TryClose();
                opened = false;
            }

            var wait = _interval - (DateTime.UtcNow - started);
            try { if (wait > TimeSpan.Zero) await Task.Delay(wait, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }

        if (opened) TryClose();
    }

    // Uma janela travada nao pode segurar as outras: quem nao recebe em 2 s e desconectado.
    async Task SendAsync(NamedPipeServerStream client, byte[] bytes)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.WriteAsync(bytes, timeout.Token);
            await client.FlushAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            lock (_gate) _clients.Remove(client);
            client.Dispose();
        }
    }

    void TryClose()
    {
        try { close(); }
        catch (Exception ex) { Failed?.Invoke(ex.Message); }
    }

    public int ClientCount
    {
        get { lock (_gate) return _clients.Count; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll([_accept ?? Task.CompletedTask, _publish ?? Task.CompletedTask], TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { /* encerrando */ }

        lock (_gate)
        {
            foreach (var client in _clients) client.Dispose();
            _clients.Clear();
        }
        _cts.Dispose();
    }
}
