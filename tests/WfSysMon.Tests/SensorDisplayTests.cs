using LibreHardwareMonitor.Hardware;
using WfSysMon;
using Xunit;
using static SensorFakes;

public class SensorDisplayTests
{
    public SensorDisplayTests()
    {
        SensorDisplay.Reset();
        Settings.Current.DisplaySeconds = 2;
    }

    [Fact]
    public void AveragesReadingsAndPublishesEveryOtherRead()
    {
        SensorDisplay.Add("t", 40); SensorDisplay.Publish();
        Assert.Equal(40, SensorDisplay.Get("t"));

        SensorDisplay.Add("t", 90); SensorDisplay.Publish();
        Assert.Equal(40, SensorDisplay.Get("t"));

        SensorDisplay.Add("t", 50); SensorDisplay.Publish();
        Assert.Equal(60, SensorDisplay.Get("t"));

        // janela de 3: descarta a mais antiga e ignora nulo
        SensorDisplay.Add("t", null); SensorDisplay.Add("t", 70); SensorDisplay.Publish();
        SensorDisplay.Add("t", 80); SensorDisplay.Publish();
        Assert.Equal((50 + 70 + 80) / 3f, SensorDisplay.Get("t"));
    }

    // Leva a barra a 60 e muda o alvo para 10. Devolve a posicao a cada quadro ate parar.
    static List<float> MoveBar(double frameSeconds)
    {
        SensorDisplay.Reset();
        SensorDisplay.Add("t", 60); SensorDisplay.Add("t", 60); SensorDisplay.Add("t", 60); SensorDisplay.Publish();
        SensorDisplay.TrackBar("t");
        SensorDisplay.Animate(frameSeconds);
        Assert.False(SensorDisplay.Animate(frameSeconds));

        SensorDisplay.Add("t", 10); SensorDisplay.Add("t", 10); SensorDisplay.Add("t", 10);
        for (var i = 0; i < Settings.Current.DisplaySeconds; i++) SensorDisplay.Publish();
        Assert.Equal(10, SensorDisplay.Get("t"));

        List<float> steps = [60];
        while (SensorDisplay.Animate(frameSeconds) && steps.Count < 1000)
            steps.Add(SensorDisplay.GetBar("t")!.Value);
        return steps;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void BarAcceleratesSlowsDownAndArrivesBeforeTheNextReading(int interval)
    {
        Settings.Current.DisplaySeconds = interval;
        const double frame = 0.04;
        var steps = MoveBar(frame);

        Assert.Equal(10, steps[^1]);
        Assert.True(steps.Zip(steps.Skip(1)).All(p => p.Second <= p.First));
        Assert.True(steps.All(v => v >= 10));

        var speeds = steps.Zip(steps.Skip(1)).Select(p => p.First - p.Second).ToList();
        var fastest = speeds.IndexOf(speeds.Max());
        Assert.True(fastest > 0, "precisa acelerar no inicio");
        Assert.True(fastest < speeds.Count / 2, "precisa desacelerar por mais tempo");
        Assert.True(speeds[^1] < speeds[fastest] / 3, "precisa chegar devagar");

        var seconds = speeds.Count * frame;
        Assert.True(seconds < interval, $"levou {seconds:F2} s para um intervalo de {interval} s");
        Assert.True(seconds > interval * 0.35, $"{seconds:F2} s e rapido demais para um intervalo de {interval} s");
    }

    [Fact]
    public void LongerIntervalsMoveMoreSlowly()
    {
        var times = Settings.DisplayOptions.Select(interval =>
        {
            Settings.Current.DisplaySeconds = interval;
            return MoveBar(0.04).Count;
        }).ToList();

        Assert.True(times[0] < times[1] && times[1] < times[2], string.Join(" < ", times));
    }

    [Fact]
    public void MotionDoesNotDependOnFrameRate()
    {
        var fine = MoveBar(0.02);
        var coarse = MoveBar(0.08);

        // O mesmo instante nas duas taxas: 0,4 s
        Assert.Equal(fine[20], coarse[5], 0.01f);
    }

    [Fact]
    public void TargetChangeMidwayHasNoJump()
    {
        SensorDisplay.Add("t", 50); SensorDisplay.Add("t", 50); SensorDisplay.Add("t", 50); SensorDisplay.Publish();
        SensorDisplay.TrackBar("t");
        SensorDisplay.Animate(0.04);

        void Target(float value)
        {
            SensorDisplay.Add("t", value); SensorDisplay.Add("t", value); SensorDisplay.Add("t", value);
            SensorDisplay.Publish(); SensorDisplay.Publish();
        }

        Target(100);
        List<float> steps = [];
        for (var i = 0; i < 6; i++) { SensorDisplay.Animate(0.04); steps.Add(SensorDisplay.GetBar("t")!.Value); }
        var before = steps[^1] - steps[^2];

        Target(0);
        SensorDisplay.Animate(0.04);
        var after = SensorDisplay.GetBar("t")!.Value - steps[^1];

        Assert.True(Math.Abs(after - before) < 6, $"velocidade mudou de {before:F1} para {after:F1} em um quadro");
    }

    [Fact]
    public void MinimumIgnoresZeroedFirstReading()
    {
        var disk = S("Temperature", SensorType.Temperature, 0, 0, 40);
        var hardware = H("disco", HardwareType.Storage, disk);

        SensorDisplay.Update([hardware]);
        ((Fake)(object)disk).Values["Value"] = (float?)38f;
        SensorDisplay.Update([hardware]);

        Assert.Equal(38, SensorDisplay.Min(disk));
    }
}
