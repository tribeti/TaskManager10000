using core.Models;
using core.Monitor;
using src.Views;

namespace src;

public class ViewState
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

public static class Input
{
    public static (bool Changed, bool ShouldExit) HandleInput(ViewState state, List<ProcessInfo> processes, int pageSize, int scrollCount = -1)
    {
        bool changed = false;
        while (Console.KeyAvailable)
        {
            var ki = Console.ReadKey(true);
            var key = ki.Key;

            // Workspace switch: 1 -> 9
            int ws = -1;
            if (!state.SearchMode && key is >= ConsoleKey.D1 and <= ConsoleKey.D9)
                ws = key - ConsoleKey.D1;

            if (ws >= 0)
            {
                if (ws < AppShell.WorkspaceNames.Length && ws != state.Workspace)
                {
                    state.Workspace = ws;
                    changed = true;
                }
                continue;
            }

            if (!state.SearchMode)
            {
                if (key == ConsoleKey.UpArrow)
                {
                    state.SelectedIndex = Math.Max(0, state.SelectedIndex - 1);
                    if (state.SelectedIndex < state.ScrollOffset)
                        state.ScrollOffset = state.SelectedIndex;
                    changed = true;
                    continue;
                }

                if (key == ConsoleKey.DownArrow)
                {
                    int count = scrollCount >= 0 ? scrollCount : processes.Count;
                    int maxIdx = Math.Max(0, count - 1);
                    state.SelectedIndex = Math.Min(maxIdx, state.SelectedIndex + 1);
                    if (state.SelectedIndex >= state.ScrollOffset + pageSize)
                        state.ScrollOffset = state.SelectedIndex - pageSize + 1;
                    changed = true;
                    continue;
                }
            }

            // Ports workspace has no process actions: only Q works
            if (state.Workspace == AppShell.PortWorkspace)
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
}
