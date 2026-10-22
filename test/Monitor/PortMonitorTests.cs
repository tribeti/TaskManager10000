using System.Net;
using System.Net.Sockets;
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

    private static TcpListener StartTcpListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    [Fact]
    public void Refresh_ShouldPopulateCache()
    {
        using var listener = StartTcpListener(out int port);

        var monitor = CreateRefreshedMonitor();

        var connection = Assert.Single(monitor.Connections,
            c => c.Protocol == "TCP" && c.LocalPort == port);
        Assert.Equal(Environment.ProcessId, connection.Pid);
        Assert.Equal("127.0.0.1", connection.LocalAddress);
        Assert.Equal("LISTEN", connection.State);
    }

    [Fact]
    public void Connections_ShouldHaveValidValues()
    {
        using var listener = StartTcpListener(out _);

        var monitor = CreateRefreshedMonitor();

        Assert.NotEmpty(monitor.Connections);
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
        using var listener = StartTcpListener(out _);

        var procMonitor = new ProcessMonitor();
        procMonitor.Refresh();
        var snapshot = procMonitor.Snapshot();
        var pids = snapshot.Select(p => p.Id).ToHashSet();

        var monitor = new PortMonitor();
        monitor.Refresh(snapshot);

        Assert.Contains(monitor.Connections, c => c.Pid == Environment.ProcessId);
        Assert.All(monitor.Connections, c => Assert.True(pids.Contains(c.Pid),
            $"Connection row with PID {c.Pid} should have been dropped"));
    }

    [Fact]
    public void Connections_ShouldDropSystemPids()
    {
        using var listener = StartTcpListener(out _);

        var monitor = CreateRefreshedMonitor();

        Assert.NotEmpty(monitor.Connections);
        Assert.All(monitor.Connections, c =>
            Assert.False(c.Pid is 0 or 4, $"System PID {c.Pid} should have been dropped"));
    }

    [Fact]
    public void Connections_UdpShouldHaveNoStateAndNoRemoteAddress()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        var monitor = CreateRefreshedMonitor();

        var connection = Assert.Single(monitor.Connections,
            c => c.Protocol == "UDP" && c.LocalPort == port);
        Assert.Equal(Environment.ProcessId, connection.Pid);
        Assert.Equal("", connection.State);
        Assert.Equal("", connection.RemoteAddress);
        Assert.Equal(0, connection.RemotePort);

        Assert.All(monitor.Connections.Where(c => c.Protocol == "UDP"), c =>
        {
            Assert.Equal("", c.State);
            Assert.Equal("", c.RemoteAddress);
            Assert.Equal(0, c.RemotePort);
        });
    }

    [Fact]
    public void Refresh_ShouldDropClosedConnections()
    {
        int port;
        using (var listener = StartTcpListener(out port))
        {
            var before = CreateRefreshedMonitor();
            Assert.Contains(before.Connections, c =>
                c.Protocol == "TCP" && c.LocalPort == port && c.Pid == Environment.ProcessId);
        }

        var after = CreateRefreshedMonitor();

        Assert.DoesNotContain(after.Connections, c =>
            c.Protocol == "TCP" && c.LocalPort == port && c.Pid == Environment.ProcessId);
    }

    [Fact]
    public void Refresh_WithNoProcesses_ShouldReturnNoConnections()
    {
        using var listener = StartTcpListener(out _);

        var monitor = new PortMonitor();
        monitor.Refresh([]);

        Assert.Empty(monitor.Connections);
    }
}
