using core.Models;
using Spectre.Console;

namespace src.Views;

public static class PortsTable
{
    public static Table Create()
    {
        var table = new Table().NoBorder().Expand();
        table.AddColumn(new TableColumn("[bold]Proto[/]"));
        table.AddColumn(new TableColumn("[bold]Local[/]"));
        table.AddColumn(new TableColumn("[bold]Remote[/]"));
        table.AddColumn(new TableColumn("[bold]State[/]"));
        table.AddColumn(new TableColumn("[bold]PID[/]").Alignment(Justify.Right));
        table.AddColumn(new TableColumn("[bold]Process[/]"));
        return table;
    }

    public static void Render(Table table, List<ConnectionInfo> connections, ViewState state, int pageSize)
    {
        table.Rows.Clear();

        table.Caption = new TableTitle($"[OrangeRed1]{connections.Count} connections[/]");

        int start = state.PortScrollOffset;
        int end = Math.Min(start + pageSize, connections.Count);

        for (int i = start; i < end; i++)
        {
            var c = connections[i];
            bool isSelected = i == state.PortSelectedIndex;

            string local = Endpoint(c.LocalAddress, c.LocalPort);
            string remote = Endpoint(c.RemoteAddress, c.RemotePort);
            string stateText = c.State.Length == 0 ? "-" : c.State;
            string processName = Markup.Escape(c.ProcessName);

            if (isSelected)
            {
                table.AddRow(
                    $"[black on white]{c.Protocol}[/]",
                    $"[black on white]{local}[/]",
                    $"[black on white]{remote}[/]",
                    $"[black on white]{stateText}[/]",
                    $"[black on white]{c.Pid}[/]",
                    $"[black on white]{processName}[/]");
            }
            else
            {
                string proto = c.Protocol == "UDP" ? "[blue]UDP[/]" : "[green]TCP[/]";
                string stateMarkup = c.State switch
                {
                    "ESTABLISHED" => "[green]ESTABLISHED[/]",
                    "LISTEN" => "[yellow]LISTEN[/]",
                    "" => "[dim]-[/]",
                    _ => Markup.Escape(c.State)
                };

                table.AddRow(proto, local, remote, stateMarkup, c.Pid.ToString(), processName);
            }
        }
    }

    // netstat style: "1.2.3.4:80"; missing endpoint becomes "*"
    private static string Endpoint(string address, int port) =>
        string.IsNullOrEmpty(address)
            ? "*"
            : $"{address}:{(port == 0 ? "*" : port)}";
}
