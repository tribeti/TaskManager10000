namespace core.Models;

public record ConnectionInfo(
    string Protocol,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    string State,
    int Pid,
    string ProcessName
);
