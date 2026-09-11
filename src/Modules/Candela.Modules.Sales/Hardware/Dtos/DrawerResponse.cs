using Newtonsoft.Json;

namespace Candela.Modules.Sales.Hardware.Dtos;

/// <summary>
/// POST /api/hardware/drawer. No success/data envelope — the net48 endpoint returned
/// these fields at the top level.
///
/// The two paths answered with different field sets, and that is reproduced by leaving
/// the ones that do not apply null, which NullValueHandling.Ignore then drops:
///
///   TCP      { success: true, printer_ip: "10.0.0.5", printer_port: 9100, pos_code: "POS1" }
///   spooler  { success: true, printer_name: "POS-58", pos_code: "POS1" }
///
/// The declared order matches both, so neither shape gains or loses a key.
/// </summary>
public sealed class DrawerResponse
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("printer_ip")]
    public string? PrinterIp { get; set; }

    /// <summary>Nullable so the spooler path omits it rather than reporting port 0.</summary>
    [JsonProperty("printer_port")]
    public int? PrinterPort { get; set; }

    [JsonProperty("printer_name")]
    public string? PrinterName { get; set; }

    [JsonProperty("pos_code")]
    public string? PosCode { get; set; }
}
