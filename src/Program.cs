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
        var portMonitor = new PortMonitor();
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

        // ── Workspace 2: live TCP/UDP connections ──
        var portsTable = PortsTable.Create();
        var systemLayout = AppShell.CreateSystemLayout(portsTable);
        Layout[] workspaces = [layout, systemLayout];
        int shown = 0;

        // Set tab bar for each workspace
        for (int i = 0; i < workspaces.Length; i++)
            workspaces[i]["Tabs"].Update(AppShell.TabBar(i));

        // ── Background Tasks ─────────────────────────────────────
        StatsCollector.StartStatsCollector(cpu, gpu, network, disk);
        StatsCollector.StartProcessCollector(procMonitor);
        StatsCollector.StartPortCollector(procMonitor, portMonitor);

        // ── Render Loop ──────────────────────────────────────────
        AnsiConsole.Live(layout)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Start(ctx =>
            {
                List<ProcessInfo> filtered = [];

                while (true)
                {
                    var connections = portMonitor.Connections;

                    int pageSize = CalculatePageSize(viewState.Workspace);
                    var (inputChanged, shouldExit) = Input.HandleInput(viewState, filtered, pageSize, connections);

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
                    if (StatsCollector.StatsDirty && StatsCollector.Current is not null && viewState.Workspace != AppShell.PortWorkspace)
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

                    if (StatsCollector.PortDirty || inputChanged)
                    {
                        if (connections.Count == 0)
                        {
                            viewState.PortSelectedIndex = 0;
                            viewState.PortScrollOffset = 0;
                        }
                        else
                        {
                            viewState.PortSelectedIndex = Math.Clamp(viewState.PortSelectedIndex, 0, connections.Count - 1);
                            viewState.PortScrollOffset = Math.Clamp(viewState.PortScrollOffset, 0, Math.Max(0, connections.Count - pageSize));
                        }

                        PortsTable.Render(portsTable, connections, viewState, pageSize);
                        AppShell.UpdatePortsStatus(systemLayout, connections.Count);
                        StatsCollector.PortDirty = false;
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
        const int ChromeHeight = 2; // tab bar + hint bar
        int remainingForRows = currentHeight - ChromeHeight;
        int processHeight = workspace == 0
            ? (int)(remainingForRows * 4.0 / 6.0)
            : remainingForRows;
        int tableChrome = workspace == 0 ? 4 : 3; // panel border + header (+ search caption)

        _cachedPageSize = Math.Max(3, processHeight - tableChrome);
        return _cachedPageSize;
    }
}
