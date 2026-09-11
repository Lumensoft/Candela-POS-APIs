using System.Net;
using System.Net.Sockets;

namespace Candela.Platform.Printing;

/// <summary>
/// Validates client-supplied printer targets before the server connects to them.
///
/// Both /hardware/drawer and /print accept a printer from the request body, and
/// the server then opens a socket (TCP 9100) or hands the name to the Windows
/// spooler. Unvalidated, that is a server-side request forgery primitive: any
/// holder of a token could aim the shop server at an arbitrary host and port and
/// use the differing errors as a network scanner, or point it at a UNC path and
/// collect an NTLM handshake from the app-pool identity.
///
/// Printers live on the shop LAN, so the rule is simply: the target must resolve
/// to a private, loopback or link-local address. Anything genuinely outside that
/// can be listed in the Printing:AllowedHosts setting.
///
/// A bare spooler queue name ("POS-58") is allowed unchecked — it is configured on
/// the machine by an administrator, not supplied by the caller in any meaningful
/// sense. Only UNC paths reach out over the network.
///
/// Ported from the .NET Framework host with one change: the settings arrive through
/// PrinterGuardOptions instead of ConfigurationManager. Every decision this makes —
/// the port list, the private ranges, the UNC handling, the messages — is unchanged,
/// because each one is a rule somebody deliberately chose.
/// </summary>
public sealed class PrinterGuard(PrinterGuardOptions options)
{
    private static readonly int[] DefaultPorts = { 9100, 9101, 9102, 9103, 515, 631 };

    public sealed class Result
    {
        public bool Ok { get; private set; }
        public string Reason { get; private set; } = "";
        public static Result Allow() => new() { Ok = true };
        public static Result Deny(string reason) => new() { Ok = false, Reason = reason };
    }

    private HashSet<string> AllowedHosts() =>
        new((options.AllowedHosts ?? "").Split(',')
                .Select(s => s.Trim()).Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);

    private int[] AllowedPorts()
    {
        var raw = options.AllowedPorts;
        if (string.IsNullOrWhiteSpace(raw)) return DefaultPorts;

        var list = new List<int>();
        foreach (var part in raw.Split(','))
            if (int.TryParse(part.Trim(), out int p) && p > 0 && p <= 65535) list.Add(p);

        return list.Count > 0 ? list.ToArray() : DefaultPorts;
    }

    /// <summary>TCP target for the ESC/POS drawer kick.</summary>
    public Result CheckTcp(string? host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) return Result.Deny("printer_ip is required");
        if (host.Length > 253) return Result.Deny("printer_ip is invalid");

        if (!AllowedPorts().Contains(port))
            return Result.Deny("printer_port is not an allowed printer port");

        return CheckHostReachableRange(host.Trim());
    }

    /// <summary>Spooler target: a local queue name, or a UNC path we must vet.</summary>
    public Result CheckPrinterName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Result.Deny("printer_name is required");
        name = name.Trim();
        if (name.Length > 260) return Result.Deny("printer_name is invalid");

        // Not a UNC path -> local spooler queue, nothing reaches out over the network.
        if (!name.StartsWith(@"\\", StringComparison.Ordinal)) return Result.Allow();

        var rest = name.Substring(2);
        var slash = rest.IndexOf(@"\", StringComparison.Ordinal);
        var host = slash > 0 ? rest.Substring(0, slash) : rest;
        if (string.IsNullOrWhiteSpace(host)) return Result.Deny("printer_name is invalid");

        return CheckHostReachableRange(host);
    }

    private Result CheckHostReachableRange(string host)
    {
        if (AllowedHosts().Contains(host)) return Result.Allow();

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            try { addresses = Dns.GetHostAddresses(host); }
            catch { return Result.Deny("printer host could not be resolved"); }
        }

        if (addresses.Length == 0)
            return Result.Deny("printer host could not be resolved");

        foreach (var a in addresses)
        {
            if (!IsPrivate(a))
                return Result.Deny("printer host is outside the local network");
        }
        return Result.Allow();
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal
                   || (ip.IsIPv4MappedToIPv6 && IsPrivate(ip.MapToIPv4()));

        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;

        var b = ip.GetAddressBytes();
        if (b[0] == 10) return true;                              // 10.0.0.0/8
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
        if (b[0] == 192 && b[1] == 168) return true;              // 192.168.0.0/16
        if (b[0] == 169 && b[1] == 254) return true;              // 169.254.0.0/16
        if (b[0] == 127) return true;
        return false;
    }
}
