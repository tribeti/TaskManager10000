using core.Helpers;
using core.Models;
using core.Monitor;
using Spectre.Console;
using src.Views;

namespace src;

class Program
{
    private static int _cachedWindowHeight = -1;
    private static int _cachedPageSize = 10;
    private static int _cachedWorkspace = -1;

    static void Main()
    {
        // ── Init Helpers ─────────────────────────────────────────
        using var cpu = new CpuHelper();
        using var gpu = new GpuHelper();
        using var network = new NetworkHelper();
        using var disk = new DiskHelper();
        var procMonitor = new ProcessMonitor();
        var (gpuName, gpuDriver, gpuVramTotalMB) = GpuHelper.GetGPUInfo();

        cpu.WarmUp();
        gpu.WarmUp();
        Thread.Sleep(1000);

        // ── Setup UI & State ─────────────────────────────────────
        var layout = AppShell.CreateLayout();
        var viewState = new ViewState();

        var procTable = ProcessTable.Create();

        var procPanel = new Panel(procTable).Expand();
        layout["Table"].Update(procPanel);

        // ── Create empty system workspace (user will decorate later) ──
        var systemLayout = AppShell.CreateSystemLayout();
        Layout[] workspaces = [layout, systemLayout];
        int shown = 0;

        // Set tab bar for each workspace
        for (int i = 0; i < workspaces.Length; i++)
            workspaces[i]["Tabs"].Update(AppShell.TabBar(i));

        // ── Background Tasks ─────────────────────────────────────
        StatsCollector.StartStatsCollector(cpu, gpu, network, disk);
        StatsCollector.StartProcessCollector(procMonitor);

        // ── Render Loop ──────────────────────────────────────────
        AnsiConsole.Live(layout)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Start(ctx =>
            {
                List<ProcessInfo> filtered = [];

                while (true)
                {
                    int pageSize = CalculatePageSize(viewState.Workspace);
                    var (inputChanged, shouldExit) = Input.HandleInput(viewState, filtered, pageSize);

                    if (shouldExit)
                        break;

                    if (viewState.Workspace != shown)
                    {
                        shown = viewState.Workspace;
                        ctx.UpdateTarget(workspaces[shown]);
                        StatsCollector.StatsDirty = true;
                        pageSize = CalculatePageSize(shown);
                    }

                    bool refreshed = false;

                    // Only render stats if not in system workspace
                    if (StatsCollector.StatsDirty && StatsCollector.Current is not null && viewState.Workspace != AppShell.SystemWorkspace)
                    {
                        AppShell.RenderStats(layout, StatsCollector.Current, gpuName, gpuDriver, gpuVramTotalMB);
                        StatsCollector.StatsDirty = false;
                        refreshed = true;
                    }

                    if (StatsCollector.ProcDirty || inputChanged)
                    {
                        filtered = procMonitor.GetFiltered(viewState.GetSearchQuery(), viewState.SortMode);

                        if (filtered.Count == 0)
                        {
                            viewState.SelectedIndex = 0;
                            viewState.ScrollOffset = 0;
                        }
                        else
                        {
                            viewState.SelectedIndex = Math.Clamp(viewState.SelectedIndex, 0, filtered.Count - 1);
                            viewState.ScrollOffset = Math.Clamp(viewState.ScrollOffset, 0, Math.Max(0, filtered.Count - pageSize));
                        }

                        ProcessTable.Render(procTable, filtered, viewState, pageSize);
                        StatsCollector.ProcDirty = false;
                        refreshed = true;
                    }

                    if (refreshed)
                        ctx.Refresh();
                    Thread.Sleep(16);
                }
            });
    }

    // ── Page Size ────────────────────────────────────────────────

    private static int CalculatePageSize(int workspace)
    {
        int currentHeight = Console.WindowHeight;
        if (currentHeight == _cachedWindowHeight && workspace == _cachedWorkspace)
            return _cachedPageSize;

        _cachedWindowHeight = currentHeight;
        _cachedWorkspace = workspace;
        const int ChromeHeight = 2; // tab bar + key hints bar
        const int TableVerticalPadding = 4;
        int remainingForRows = currentHeight - ChromeHeight;
        int processHeight = workspace == 0
            ? (int)(remainingForRows * 4.0 / 6.0)
            : remainingForRows;

        _cachedPageSize = Math.Max(3, processHeight - TableVerticalPadding);
        return _cachedPageSize;
    }
}
