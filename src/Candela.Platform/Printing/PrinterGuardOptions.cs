namespace Candela.Platform.Printing;

/// <summary>
/// The two Printing:* settings PrinterGuard reads.
///
/// In the .NET Framework host these came straight off ConfigurationManager.AppSettings
/// inside the guard itself. There is no ambient configuration on .NET 10, and a security
/// control that silently reads from a global is hard to test anyway, so they are passed
/// in — the values and their meaning are unchanged, and the keys in appsettings.json are
/// the same ones the Web.config used.
/// </summary>
public sealed class PrinterGuardOptions
{
    /// <summary>
    /// Comma separated hosts allowed even though they are outside the private ranges.
    /// Normally empty: printers live on the shop LAN.
    /// </summary>
    public string AllowedHosts { get; set; } = "";

    /// <summary>
    /// Comma separated TCP ports a drawer kick or raw print may target. Empty means the
    /// built-in list (9100-9103, 515, 631).
    /// </summary>
    public string AllowedPorts { get; set; } = "";
}
