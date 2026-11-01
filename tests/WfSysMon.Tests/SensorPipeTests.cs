using System.IO.Pipes;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using WfSysMon;
using Xunit;

// Testa o canal de verdade: servidor e cliente em pipes reais.
public class SensorPipeTests
{
    static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    // Em processo elevado (como na CI) o dono do pipe e o grupo Administradores, nao a conta.
    static readonly SecurityIdentifier[] TrustMe = [Me, new(WellKnownSidType.BuiltinAdministratorsSid, null)];

    static string PipeName() => "wf-test-" + Guid.NewGuid().ToString("N")[..8];

    static bool Until(Func<bool> condition, int ms = 6000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    static List<HardwareDto> Sample(float temp) =>
    [
        new("/cpu/0", "CPU", HardwareType.Cpu,
            [new("/cpu/0/temperature/0", "CPU Package", SensorType.Temperature, temp, 30, 90)],
            [new("/sub/0", "Sub", HardwareType.Motherboard, [new("/sub/0/fan/0", "Fan", SensorType.Fan, 1000, null, null)], [])]),
    ];

    static SensorServer Server(string name, Func<List<HardwareDto>>? read = null) =>
        new(read ?? (() => Sample(50)), () => { }, () => { }, Me, name, TimeSpan.FromMilliseconds(100));

    [Fact]
    public void ServesClientsAndStopsReadingWhenIdle()
    {
        var name = PipeName();
        float temp = 50;
        int opens = 0, closes = 0, samples = 0;
        var server = new SensorServer(() => { samples++; return Sample(temp); }, () => opens++, () => closes++, Me, name, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(600));
        server.Start();
        Thread.Sleep(200);
        Assert.Equal(0, opens);
        Assert.Equal(0, samples);

        IReadOnlyList<IHardware>? got1 = null, got2 = null;
        using (var c1 = new SensorClient(h => got1 = h, name, TrustMe))
        using (var c2 = new SensorClient(h => got2 = h, name, TrustMe))
        {
            c1.Start();
            c2.Start();
            Assert.True(c1.WaitForLink(TimeSpan.FromSeconds(4)));
            Assert.True(Until(() => got1 is not null && got2 is not null && c1.Link == SensorLink.Connected && c2.Link == SensorLink.Connected));

            var sensor = got1![0].Sensors[0];
            Assert.Equal(50, sensor.Value);
            Assert.Equal(30, sensor.Min);
            Assert.Equal(90, sensor.Max);
            Assert.Equal("CPU Package", sensor.Name);
            Assert.Equal(SensorType.Temperature, sensor.SensorType);
            Assert.Equal("/cpu/0/temperature/0", sensor.Identifier.ToString());
            Assert.Equal(1000, got1[0].SubHardware[0].Sensors[0].Value);

            temp = 77;
            Assert.True(Until(() => got1![0].Sensors[0].Value == 77 && got2![0].Sensors[0].Value == 77));
            Assert.Equal(1, opens);

            SensorDisplay.Update(got1);
            Assert.NotNull(SensorDisplay.Get(got1[0].Sensors[0]));

            // Canal de mao unica: escrever e recusado e o servidor continua funcionando.
            var writeBlocked = false;
            try
            {
                using var writer = new NamedPipeClientStream(".", name, PipeDirection.Out);
                writer.Connect(800);
                writer.WriteByte(65);
                writer.Flush();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or TimeoutException) { writeBlocked = true; }
            Assert.True(writeBlocked);
            Assert.True(Until(() => { var before = samples; Thread.Sleep(250); return samples > before; }));
        }

        Assert.True(Until(() => server.ClientCount == 0 && closes == 1, 8000));
        var idle = samples;
        Thread.Sleep(350);
        Assert.Equal(idle, samples);

        // Reabre para uma nova janela e reconecta quando o servico volta.
        IReadOnlyList<IHardware>? got3 = null;
        using var c3 = new SensorClient(h => got3 = h, name, TrustMe);
        c3.Start();
        Assert.True(Until(() => got3 is not null && opens == 2));
        server.Dispose();
        Assert.True(Until(() => c3.Link == SensorLink.Disconnected));
        got3 = null;
        using var again = Server(name);
        again.Start();
        Assert.True(Until(() => got3 is not null && c3.Link == SensorLink.Connected, 8000));
    }

    [Fact]
    public void RejectsServerOwnedByRegularUser()
    {
        // Em processo elevado o dono do pipe ja e Administradores: o cenario so existe sem elevacao.
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return;

        var name = PipeName();
        using var fake = Server(name);
        fake.Start();
        Thread.Sleep(200);

        var received = false;
        using var client = new SensorClient(_ => received = true, name);  // donos padrao: SYSTEM e Administradores
        client.Start();

        Assert.True(Until(() => client.Link == SensorLink.Untrusted), client.Link.ToString());
        Thread.Sleep(300);
        Assert.False(received);
    }

    [Fact]
    public void DoesNotShareTheNameWithAnotherProgram()
    {
        var name = PipeName();
        using var squatter = new NamedPipeServerStream(name, PipeDirection.Out, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var failure = "";
        using var blocked = Server(name);
        blocked.Failed += message => failure = message;
        blocked.Start();
        Assert.True(Until(() => failure.Length > 0, 3000));
    }

    [Fact]
    public void DifferentProtocolIsReportedAsOutdated()
    {
        var name = PipeName();
        var frame = System.Text.Json.JsonSerializer.Serialize(new SensorFrame(SensorProtocol.Version + 1, [])) + "\n";
        AssertOutdated(name, frame);
    }

    // Servico da versao anterior, com os tipos em numero.
    [Fact]
    public void PreviousProtocolWithNumericTypesIsReportedAsOutdated() =>
        AssertOutdated(PipeName(), """{"Protocol":1,"Hardware":[{"Id":"/cpu/0","Name":"CPU","Type":2,"Sensors":[],"Sub":[]}]}""" + "\n");

    // Um formato futuro pode trazer o que esta versao nao entende: o aviso ainda precisa aparecer.
    [Fact]
    public void FutureProtocolWithUnknownTypesIsReportedAsOutdated() =>
        AssertOutdated(PipeName(), $$"""{"Protocol":{{SensorProtocol.Version + 1}},"Hardware":[{"Id":"/cpu/0","Name":"CPU","Type":"Quantum","Sensors":[],"Sub":[]}]}""" + "\n");

    [Fact]
    public void SensorTypesTravelByName()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new SensorFrame(SensorProtocol.Version, Sample(50)), SensorProtocol.Json);
        Assert.Contains("\"Type\":\"Temperature\"", json);
        Assert.Contains("\"Type\":\"Cpu\"", json);

        var back = System.Text.Json.JsonSerializer.Deserialize<SensorFrame>(json, SensorProtocol.Json)!;
        Assert.Equal(SensorType.Temperature, back.Hardware[0].Sensors[0].Type);
        Assert.Equal(HardwareType.Motherboard, back.Hardware[0].Sub[0].Type);
    }

    static void AssertOutdated(string name, string frame)
    {
        using var old = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var called = false;
        using var client = new SensorClient(_ => called = true, name, TrustMe);
        client.Start();
        old.WaitForConnection();
        old.Write(System.Text.Encoding.UTF8.GetBytes(frame));
        old.Flush();

        Assert.True(Until(() => client.Link == SensorLink.Outdated), client.Link.ToString());
        Assert.False(called);
    }
}
