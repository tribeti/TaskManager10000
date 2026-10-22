using core.Helpers;
using core.Models;
using core.Monitor;
using Spectre.Console;
using src.Views;

namespace src;

class Program
{
    private static int _cachedWindowHeight = -1;
    private static readonly int[] _cachedPageSizes = new int[AppShell.WorkspaceNames.Length];

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

                    int processPageSize = CalculatePageSize(0);
                    int portPageSize = CalculatePageSize(AppShell.PortWorkspace);
                    int activePageSize = viewState.Workspace == AppShell.PortWorkspace ? portPageSize : processPageSize;
                    var (inputChanged, shouldExit) = Input.HandleInput(viewState, filtered, activePageSize, connections);

                    if (shouldExit)
                        break;

                    if (viewState.Workspace != shown)
                    {
                        shown = viewState.Workspace;
                        ctx.UpdateTarget(workspaces[shown]);
                        StatsCollector.StatsDirty = true;
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
                            viewState.ScrollOffset = Math.Clamp(viewState.ScrollOffset, 0, Math.Max(0, filtered.Count - processPageSize));
                        }

                        ProcessTable.Render(procTable, filtered, viewState, processPageSize);
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
                            viewState.PortScrollOffset = Math.Clamp(viewState.PortScrollOffset, 0, Math.Max(0, connections.Count - portPageSize));
                        }

                        PortsTable.Render(portsTable, connections, viewState, portPageSize);
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
        if (currentHeight != _cachedWindowHeight)
        {
            _cachedWindowHeight = currentHeight;
            Array.Clear(_cachedPageSizes);
        }
        else if (_cachedPageSizes[workspace] > 0)
        {
            return _cachedPageSizes[workspace];
        }

        const int ChromeHeight = 2; // tab bar + hint bar
        int remainingForRows = currentHeight - ChromeHeight;
        int workspaceHeight = workspace == 0
            ? (int)(remainingForRows * 4.0 / 6.0)
            : remainingForRows;
        int tableChrome = workspace == 0 ? 4 : 3; // panel border + header (+ search caption)

        return _cachedPageSizes[workspace] = Math.Max(3, workspaceHeight - tableChrome);
    }
}
