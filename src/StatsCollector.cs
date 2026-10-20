using core.Helpers;
using core.Monitor;

namespace src;

public record SystemStats(
    double Cpu,
    (double UsagePercent, double VramUsedMB) Gpu,
    (double TotalGB, double FreeGB, double UsedPct) Ram,
    (double Download, double Upload) Network,
    (long Ping, int Loss) PingInfo,
    List<DriveMetrics> Drives,
    DiskMetrics Disk
);

public static class StatsCollector
{
    public static volatile SystemStats? Current;
    public static volatile bool StatsDirty = true;
    public static volatile bool ProcDirty = true;
    public static volatile bool PortDirty = true;

    public static void StartStatsCollector(CpuHelper cpu, GpuHelper gpu, NetworkHelper network, DiskHelper disk)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                Current = new SystemStats(
                    cpu.GetUsage(),
                    (gpu.GetGPUUsage(), GpuHelper.GetTotalVRamUsage()),
                    RamHelper.GetMemoryStatus(),
                    network.NetworkSpeed(),
                    NetworkHelper.GetPingAndPacketLoss(),
                    disk.GetAllDrivesUsage(),
                    disk.GetDiskMetrics()
                );

                StatsDirty = true;
                await Task.Delay(1000);
            }
        });
    }

    public static void StartProcessCollector(ProcessMonitor procMonitor)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                procMonitor.Refresh();
                ProcDirty = true;
                await Task.Delay(1000);
            }
        });
    }

    public static void StartPortCollector(ProcessMonitor procMonitor, PortMonitor portMonitor)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                // Names come from the process snapshot the ProcessMonitor is
                // already refreshing every second - no second sweep needed.
                portMonitor.Refresh(procMonitor.Snapshot());
                PortDirty = true;
                await Task.Delay(1000);
            }
        });
    }
}
