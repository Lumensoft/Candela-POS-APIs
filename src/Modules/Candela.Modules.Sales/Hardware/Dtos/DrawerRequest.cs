namespace Candela.Modules.Sales.Hardware.Dtos;

/// <summary>
/// Body of POST /api/hardware/drawer.
///
/// The property names are PascalCase and carry no [JsonProperty] because that is
/// literally what the tablet sends — posService.js:116 posts { PrinterIp, PrinterName,
/// PrinterPort }. Renaming them, or adding a camelCase attribute, would bind nothing and
/// the drawer would silently stop opening.
///
/// Supply exactly one of PrinterIp (LAN printer, raw TCP) or PrinterName (Windows
/// spooler). PrinterPort applies to the TCP path only and defaults to 9100.
/// </summary>
public sealed class DrawerRequest
{
    public string? PrinterIp { get; set; }
    public string? PrinterName { get; set; }
    public int PrinterPort { get; set; }
}
