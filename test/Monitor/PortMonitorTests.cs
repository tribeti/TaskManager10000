using core.Monitor;

namespace test.Monitor;

public class PortMonitorTests
{
    private static PortMonitor CreateRefreshedMonitor()
    {
        var procMonitor = new ProcessMonitor();
        procMonitor.Refresh();

        var portMonitor = new PortMonitor();
        portMonitor.Refresh(procMonitor.Snapshot());
        return portMonitor;
    }

    [Fact]
    public void Refresh_ShouldPopulateCache()
    {
        var monitor = CreateRefreshedMonitor();

        Assert.NotEmpty(monitor.Connections);
    }

    [Fact]
    public void Connections_ShouldHaveValidValues()
    {
        var monitor = CreateRefreshedMonitor();

        Assert.All(monitor.Connections, c =>
        {
            Assert.True(c.Protocol is "TCP" or "UDP", $"Protocol must be TCP/UDP, got {c.Protocol}");
            Assert.InRange(c.LocalPort, 0, 65535);
            Assert.InRange(c.RemotePort, 0, 65535);
            Assert.True(c.Pid >= 0, $"PID must be non-negative, got {c.Pid}");
            Assert.False(string.IsNullOrEmpty(c.ProcessName), "Process name must not be empty");
        });
    }

    [Fact]
    public void Connections_ShouldOnlyContainLiveProcessPids()
    {
        var procMonitor = new ProcessMonitor();
        procMonitor.Refresh();
        var pids = procMonitor.Snapshot().Select(p => p.Id).ToHashSet();

        var monitor = new PortMonitor();
        monitor.Refresh(procMonitor.Snapshot());

        Assert.All(monitor.Connections, c => Assert.True(pids.Contains(c.Pid),
            $"Connection row with PID {c.Pid} should have been dropped"));
    }

    [Fact]
    public void Connections_ShouldDropSystemPids()
    {
        var monitor = CreateRefreshedMonitor();

        Assert.All(monitor.Connections, c =>
            Assert.False(c.Pid is 0 or 4, $"System PID {c.Pid} should have been dropped"));
    }

    [Fact]
    public void Connections_UdpShouldHaveNoStateAndNoRemoteAddress()
    {
        var monitor = CreateRefreshedMonitor();

        Assert.All(monitor.Connections.Where(c => c.Protocol == "UDP"), c =>
        {
            Assert.Equal("", c.State);
            Assert.Equal("", c.RemoteAddress);
            Assert.Equal(0, c.RemotePort);
        });
    }
}
