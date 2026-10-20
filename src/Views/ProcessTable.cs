using core.Models;
using Spectre.Console;

namespace src.Views;

public static class ProcessTable
{
    public static Table Create()
    {
        var procTable = new Table().NoBorder().Expand();
        procTable.AddColumn(new TableColumn("[bold]PID[/]"));
        procTable.AddColumn(new TableColumn("[bold]Name[/]"));
        procTable.AddColumn(new TableColumn("[bold]Memory (MB)[/]").Alignment(Justify.Center));
        procTable.AddColumn(new TableColumn("[bold]CPU Usage[/]").Alignment(Justify.Center));
        return procTable;
    }

    public static void Render(Table procTable, List<ProcessInfo> processes, ViewState state, int pageSize)
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
}
