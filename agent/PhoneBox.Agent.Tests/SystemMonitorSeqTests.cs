using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Each sample carries a number that grows, so the panel adds a chart point only for a new sample, not for
/// the same one sent again with a media or volume change.</summary>
public sealed class SystemMonitorSeqTests
{
    [Fact]
    public void EachMonitorRunNumbersItsSamplesIndependently()
    {
        using var monitor = new SystemMonitor(NullLogger<SystemMonitor>.Instance);
        using var restarted = new SystemMonitor(NullLogger<SystemMonitor>.Instance);
        var sample = new PcSnapshot(1, null, "cpu", 1, 2, null, 0, 0, [], 0);
        monitor.PublishSample(sample);
        var first = Assert.IsType<PcSnapshot>(monitor.Snapshot());
        monitor.PublishSample(sample);
        var second = Assert.IsType<PcSnapshot>(monitor.Snapshot());
        restarted.PublishSample(sample);
        var afterRestart = Assert.IsType<PcSnapshot>(restarted.Snapshot());
        Assert.Equal(1, first.Seq);
        Assert.Equal(2, second.Seq);
        Assert.Equal(first.RunId, second.RunId);
        Assert.Equal(first.Seq, afterRestart.Seq);
        Assert.False(string.IsNullOrEmpty(first.RunId));
        Assert.NotEqual(first.RunId, afterRestart.RunId);
    }

    [Fact]
    public void TheSeqReachesThePanelAsCamelCase()
    {
        var snap = new PcSnapshot(1, null, "cpu", 1, 2, null, 0, 0, [], 0, Seq: 7);
        var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("\"seq\":7", json);
    }

}
