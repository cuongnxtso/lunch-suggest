using System.Net;

namespace LunchSuggest.Web;

/// <summary>
/// Who may open the Settings page: only this computer.
/// With an admin port (Docker), only requests that arrived on it: Docker Desktop hides callers' real IPs from the container,
/// so the port is published on the host's 127.0.0.1 only and LAN machines can't even connect to it.
/// Without one (dotnet run), only loopback callers.
/// </summary>
public static class AdminAccess
{
    public const string Path = "/settings";

    public static bool IsAllowed(IPAddress? remote, int localPort, int? adminPort)
    {
        if (adminPort is { } port) return localPort == port;
        if (remote is null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4(); // "::ffff:127.0.0.1"
        return IPAddress.IsLoopback(remote);
    }
}
