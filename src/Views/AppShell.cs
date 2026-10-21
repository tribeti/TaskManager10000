using Spectre.Console;

namespace src.Views;

public static class AppShell
{
    public static readonly string[] WorkspaceNames = ["Dashboard", "Ports"];
    public const int PortWorkspace = 1;

    public static Layout CreateLayout()
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Tabs").Size(1),
                new Layout("Stat").Ratio(3).MinimumSize(10),
                new Layout("Process").Ratio(5),
                new Layout("Intro").Size(1));

        layout["Stat"].SplitColumns(
            new Layout("CPU").Ratio(2),
            new Layout("RAM").Ratio(1),
            new Layout("GPU").Ratio(2));

        layout["Process"].SplitColumns(
            new Layout("Table").Ratio(6),
            new Layout("Info").Ratio(4));

        layout["Info"].SplitRows(
            new Layout("Network").Ratio(1),
            new Layout("Disk").Ratio(2));

        layout["Intro"].Update(IntroBar());

        return layout;
    }

    public static Layout CreateSystemLayout(Table portsTable)
    {
        // Workspace 2: full-height live connection table
        var l = new Layout("Root").SplitRows(
            new Layout("Tabs").Size(1),
            new Layout("Content"));

        l["Content"].Update(new Panel(portsTable).Expand());
        return l;
    }

    public static Markup TabBar(int active) =>
        new(string.Concat(WorkspaceNames.Select((name, i) => i == active
            ? $"[black on yellow] {i + 1}:{name} [/]"
            : $"[grey] {i + 1}:{name} [/]")));

    private static Panel IntroBar() =>
        new Panel("[green]K[/]: Kill | [green]Shift+K[/]: Kill All | [cyan]P[/]: Pause/Resume | [blue]S[/]: Sort | [yellow]F[/]: Find | [Wheat1]1 / 2[/]: Switch views | [red]ESC[/]: Clear | [red]Q[/]: Quit")
            .Border(BoxBorder.None).Collapse();

    public static void RenderStats(Layout layout, SystemStats stats, string gpuName, string gpuDriver, double gpuVramTotalMB)
    {
        layout["CPU"].Update(CpuPanel.Build(stats.Cpu));
        double usedRamGB = stats.Ram.TotalGB * stats.Ram.UsedPct / 100.0;
        layout["RAM"].Update(RamPanel.Build(stats.Ram.UsedPct, usedRamGB, stats.Ram.TotalGB));
        layout["GPU"].Update(GpuPanel.Build(stats.Gpu.UsagePercent, gpuName, gpuDriver, stats.Gpu.VramUsedMB, gpuVramTotalMB));
        layout["Network"].Update(NetworkPanel.Build(stats.Network.Download, stats.Network.Upload, stats.PingInfo.Ping, stats.PingInfo.Loss));
        layout["Disk"].Update(DiskPanel.Build(stats.Drives, stats.Disk));
    }
}
