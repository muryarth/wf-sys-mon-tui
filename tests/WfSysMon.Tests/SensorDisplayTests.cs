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

    [Fact]
    public void BarEasesOutWithoutOvershootingAndStops()
    {
        SensorDisplay.Add("t", 60); SensorDisplay.Publish();
        SensorDisplay.TrackBar("t");

        Assert.True(SensorDisplay.Animate(0.08));
        var start = SensorDisplay.GetBar("t")!.Value;
        Assert.Equal(SensorDisplay.Get("t"), start);
        Assert.False(SensorDisplay.Animate(0.08));

        SensorDisplay.Add("t", 10); SensorDisplay.Add("t", 10); SensorDisplay.Add("t", 10);
        SensorDisplay.Publish(); SensorDisplay.Publish();
        var target = SensorDisplay.Get("t")!.Value;
        Assert.Equal(10, target);

        var steps = new List<float>();
        while (SensorDisplay.Animate(0.08) && steps.Count < 100)
            steps.Add(SensorDisplay.GetBar("t")!.Value);

        Assert.True(steps.Zip(steps.Skip(1)).All(p => p.Second <= p.First) && steps.All(v => v >= target));
        Assert.True(start - steps[0] > steps[0] - steps[1]);
        Assert.Equal(target, steps[^1]);
        Assert.True(steps.Count < 40);
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
