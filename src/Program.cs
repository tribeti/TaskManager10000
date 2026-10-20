using core.Helpers;
using core.Models;
using core.Monitor;
using Spectre.Console;
using src.Views;

namespace src;

class Program
{
    private record SystemStats(
        double Cpu,
        (double UsagePercent, double VramUsedMB) Gpu,
        (double TotalGB, double FreeGB, double UsedPct) Ram,
        (double Download, double Upload) Network,
        (long Ping, int Loss) PingInfo,
        List<DriveMetrics> Drives,
        DiskMetrics Disk
    );

    private class ViewState
    {
        public int SelectedIndex { get; set; }
        public int ScrollOffset { get; set; }
        public int Workspace { get; set; }
        public SortMode SortMode { get; set; } = SortMode.MemoryDesc;
        public char[] SearchBuffer = new char[64];
        public int SearchLength { get; set; } = 0;
        public bool SearchMode { get; set; }
        public string GetSearchQuery() => SearchLength == 0 ? "" : new string(SearchBuffer, 0, SearchLength);
    }

    private static volatile SystemStats? _currentStats;
    private static volatile bool _statsDirty = true;
    private static volatile bool _procDirty = true;

    private static int _cachedWindowHeight = -1;
    private static int _cachedPageSize = 10;
    private static int _cachedWorkspace = -1;
    private static readonly string[] WorkspaceNames = ["Dashboard", "System"];
    private const int SystemWorkspace = 1;

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
        var layout = CreateLayout();
        var viewState = new ViewState();

        var procTable = new Table().NoBorder().Expand();
        procTable.AddColumn(new TableColumn("[bold]PID[/]"));
        procTable.AddColumn(new TableColumn("[bold]Name[/]"));
        procTable.AddColumn(new TableColumn("[bold]Memory (MB)[/]").Alignment(Justify.Center));
        procTable.AddColumn(new TableColumn("[bold]CPU Usage[/]").Alignment(Justify.Center));

        var procPanel = new Panel(procTable).Expand();
        layout["Table"].Update(procPanel);

        // ── Create empty system workspace (user will decorate later) ──
        var systemLayout = CreateSystemLayout();
        Layout[] workspaces = [layout, systemLayout];
        int shown = 0;

        // Set tab bar for each workspace
        for (int i = 0; i < workspaces.Length; i++)
            workspaces[i]["Tabs"].Update(TabBar(i));

        // ── Background Tasks ─────────────────────────────────────
        StartStatsCollector(cpu, gpu, network, disk);
        StartProcessCollector(procMonitor);

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
                    var (inputChanged, shouldExit) = HandleInput(viewState, filtered, pageSize);

                    if (shouldExit)
                        break;

                    if (viewState.Workspace != shown)
                    {
                        shown = viewState.Workspace;
                        ctx.UpdateTarget(workspaces[shown]);
                        _statsDirty = true;
                        pageSize = CalculatePageSize(shown);
                    }

                    bool refreshed = false;

                    // Only render stats if not in system workspace
                    if (_statsDirty && _currentStats is not null && viewState.Workspace != SystemWorkspace)
                    {
                        RenderStats(layout, _currentStats, gpuName, gpuDriver, gpuVramTotalMB);
                        _statsDirty = false;
                        refreshed = true;
                    }

                    if (_procDirty || inputChanged)
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

                        RenderProcesses(procTable, filtered, viewState, pageSize);
                        _procDirty = false;
                        refreshed = true;
                    }

                    if (refreshed)
                        ctx.Refresh();
                    Thread.Sleep(16);
                }
            });
    }

    // ── 3. Helper Methods ────────────────────────────────────────

    private static Layout CreateLayout()
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

    private static Layout CreateSystemLayout()
    {
        // Empty layout for user to decorate
        var l = new Layout("Root").SplitRows(
            new Layout("Tabs").Size(1),
            new Layout("Content"),
            new Layout("Intro").Size(1));

        l["Content"].Update(new Panel("[dim]Empty workspace - decorate as you wish[/]").Expand());
        l["Intro"].Update(IntroBar());
        return l;
    }

    private static Markup TabBar(int active) =>
        new(string.Concat(WorkspaceNames.Select((name, i) => i == active
            ? $"[black on yellow] {i + 1}:{name} [/]"
            : $"[grey] {i + 1}:{name} [/]")));

    private static Panel IntroBar() =>
        new Panel("[green]K[/]: Kill | [green]Shift+K[/]: Kill All | [cyan]P[/]: Pause/Resume | [blue]S[/]: Sort | [yellow]F[/]: Find | [Wheat1]1 / 2[/]: Switch views | [red]ESC[/]: Clear | [red]Q[/]: Quit")
            .Border(BoxBorder.None).Collapse();

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

    private static (bool Changed, bool ShouldExit) HandleInput(ViewState state, List<ProcessInfo> processes, int pageSize)
    {
        bool changed = false;
        while (Console.KeyAvailable)
        {
            var ki = Console.ReadKey(true);
            var key = ki.Key;

            // Workspace switch: 1 -> 9
            int ws = -1;
            if (key is >= ConsoleKey.D1 and <= ConsoleKey.D9)
                ws = key - ConsoleKey.D1;

            if (ws >= 0)
            {
                if (ws < WorkspaceNames.Length && ws != state.Workspace)
                {
                    state.Workspace = ws;
                    changed = true;
                }
                continue;
            }

            // System workspace has no process table: only Q works
            if (state.Workspace == SystemWorkspace)
            {
                if (key == ConsoleKey.Q)
                    return (false, true);
                continue;
            }

            if (state.SearchMode)
            {
                switch (key)
                {
                    case ConsoleKey.Escape:
                        state.SearchMode = false;
                        state.SearchLength = 0;
                        changed = true;
                        break;
                    case ConsoleKey.Backspace when state.SearchLength > 0:
                        state.SearchLength--;
                        changed = true;
                        break;
                    case ConsoleKey.Enter:
                        state.SearchMode = false;
                        changed = true;
                        break;
                    default:
                        if (!char.IsControl(ki.KeyChar) && state.SearchLength < state.SearchBuffer.Length)
                        {
                            state.SearchBuffer[state.SearchLength++] = ki.KeyChar;
                            changed = true;
                        }
                        break;
                }
            }
            else
            {
                switch (key)
                {
                    case ConsoleKey.UpArrow:
                        state.SelectedIndex = Math.Max(0, state.SelectedIndex - 1);
                        if (state.SelectedIndex < state.ScrollOffset)
                            state.ScrollOffset = state.SelectedIndex;
                        changed = true;
                        break;

                    case ConsoleKey.DownArrow:
                        int maxIdx = Math.Max(0, processes.Count - 1);
                        state.SelectedIndex = Math.Min(maxIdx, state.SelectedIndex + 1);
                        if (state.SelectedIndex >= state.ScrollOffset + pageSize)
                            state.ScrollOffset = state.SelectedIndex - pageSize + 1;
                        changed = true;
                        break;

                    case ConsoleKey.K when processes.Count > 0:
                        var target = processes[Math.Min(state.SelectedIndex, processes.Count - 1)];
                        if (ki.Modifiers.HasFlag(ConsoleModifiers.Shift))
                            ProcessMonitor.KillAllByName(target.Name);
                        else
                            ProcessMonitor.Kill(target.Id);
                        changed = true;
                        break;

                    case ConsoleKey.P when processes.Count > 0:
                        var targetP = processes[Math.Min(state.SelectedIndex, processes.Count - 1)];
                        ProcessMonitor.TogglePause(targetP.Id);
                        changed = true;
                        break;

                    case ConsoleKey.S:
                        state.SortMode = state.SortMode switch
                        {
                            SortMode.MemoryDesc => SortMode.CpuDesc,
                            SortMode.CpuDesc => SortMode.NameAsc,
                            _ => SortMode.MemoryDesc
                        };
                        changed = true;
                        break;

                    case ConsoleKey.F:
                        state.SearchMode = true;
                        state.SearchLength = 0;
                        changed = true;
                        break;

                    case ConsoleKey.Escape:
                        if (state.SearchLength > 0)
                        {
                            state.SearchLength = 0;
                            changed = true;
                        }
                        break;

                    case ConsoleKey.Q:
                        return (false, true);
                }
            }
        }
        return (changed, false);
    }

    private static void RenderStats(Layout layout, SystemStats stats, string gpuName, string gpuDriver, double gpuVramTotalMB)
    {
        layout["CPU"].Update(CpuPanel.Build(stats.Cpu));
        double usedRamGB = stats.Ram.TotalGB * stats.Ram.UsedPct / 100.0;
        layout["RAM"].Update(RamPanel.Build(stats.Ram.UsedPct, usedRamGB, stats.Ram.TotalGB));
        layout["GPU"].Update(GpuPanel.Build(stats.Gpu.UsagePercent, gpuName, gpuDriver, stats.Gpu.VramUsedMB, gpuVramTotalMB));
        layout["Network"].Update(NetworkPanel.Build(stats.Network.Download, stats.Network.Upload, stats.PingInfo.Ping, stats.PingInfo.Loss));
        layout["Disk"].Update(DiskPanel.Build(stats.Drives, stats.Disk));
    }

    private static void RenderProcesses(Table procTable, List<ProcessInfo> processes, ViewState state, int pageSize)
    {
        procTable.Rows.Clear();

        if (state.SearchMode || state.SearchLength > 0)
        {
            procTable.Caption = new TableTitle(
                $"[yellow]Search: {Markup.Escape(state.GetSearchQuery())}{(state.SearchMode ? "_" : "")}[/] ([dim]{processes.Count} results[/])");
        }
        else
        {
            procTable.Caption = null;
        }

        int start = state.ScrollOffset;
        int end = Math.Min(start + pageSize, processes.Count);

        for (int i = start; i < end; i++)
        {
            var p = processes[i];
            bool isSelected = i == state.SelectedIndex;
            string safeName = Markup.Escape(p.Name);
            string pausedTag = p.IsSuspended ? " [red](Paused)[/]" : "";

            if (isSelected)
            {
                string selectedName = p.IsSuspended ? $"{safeName} (Paused)" : safeName;
                procTable.AddRow(
                    $"[black on white]{p.Id}[/]",
                    $"[black on white]{selectedName}[/]",
                    $"[black on white]{p.MemoryUsage:F2}[/]",
                    $"[black on white]{p.CpuUsage:F1}%[/]"
                );
            }
            else
            {
                string memColor = p.MemoryUsage > 500 ? "red" : p.MemoryUsage > 300 ? "yellow" : "green";
                string cpuColor = p.CpuUsage > 25 ? "red" : p.CpuUsage > 10 ? "yellow" : "green";

                procTable.AddRow(
                    p.Id.ToString(),
                    $"{safeName}{pausedTag}",
                    $"[{memColor}]{p.MemoryUsage:F2}[/]",
                    $"[{cpuColor}]{p.CpuUsage:F1}%[/]"
                );
            }
        }
    }

    private static void StartStatsCollector(CpuHelper cpu, GpuHelper gpu, NetworkHelper network, DiskHelper disk)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                _currentStats = new SystemStats(
                    cpu.GetUsage(),
                    (gpu.GetGPUUsage(), GpuHelper.GetTotalVRamUsage()),
                    RamHelper.GetMemoryStatus(),
                    network.NetworkSpeed(),
                    NetworkHelper.GetPingAndPacketLoss(),
                    disk.GetAllDrivesUsage(),
                    disk.GetDiskMetrics()
                );

                _statsDirty = true;
                await Task.Delay(1000);
            }
        });
    }

    private static void StartProcessCollector(ProcessMonitor procMonitor)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                procMonitor.Refresh();
                _procDirty = true;
                await Task.Delay(1000);
            }
        });
    }
}
