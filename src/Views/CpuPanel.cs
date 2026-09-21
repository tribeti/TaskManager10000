using core.Helpers;
using Spectre.Console;

namespace src.Views;

public static class CpuPanel
{
    public static Panel Build(double cpuPct)
    {
        var grid = new Grid().AddColumn().AddColumn();
        // draw cpu usage bar
        grid.AddRow(
            new Markup("Usage"),
            new BreakdownChart()
            .ShowPercentage()
            .Compact()
            .AddItem("Used", cpuPct, Color.Red)
            .AddItem("Free", 100 - cpuPct, Color.Green)
        );
        // get cpu name
        grid.AddRow("CPU", $"[bold]{Markup.Escape(CpuHelper.GetProcessorCoreName())}[/]");
        // get os version
        grid.AddRow("OS Version", $"[bold]{Markup.Escape(CpuHelper.GetOSName())}[/]");
        // get uptime
        grid.AddRow("Uptime", $"[bold]{CpuHelper.GetUptime():dd\\.hh\\:mm\\:ss}[/]");
        // get mainboard and bios info
        (string? MainName, string? MainVer, string? BIOSVer) = CpuHelper.GetMainboardInfo();
        grid.AddRow("Mainboard", $"[bold]{Markup.Escape(MainName + " " + MainVer)}[/]");
        grid.AddRow("BIOS", $"[bold]{Markup.Escape(BIOSVer ?? "Unknown")}[/]");

        return new Panel(grid)
            .Header("[bold]CPU[/]", Justify.Center)
            .Border(BoxBorder.Rounded)
            .Expand();
    }
}