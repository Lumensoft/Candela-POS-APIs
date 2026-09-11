using Candela.Modules.Sales.Hardware.Dtos;
using Candela.Platform.Api;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Modules.Sales.Hardware;

/// <summary>
/// Till hardware, ported from the net48 HardwareController.
///
///   POST /api/hardware/drawer — open the cash drawer without printing a receipt
///
/// No DAL, no database. The Win32 spooler call and the raw TCP write both work on
/// .NET 10 unchanged, so nothing here is forwarded to the legacy host.
///
/// The decisions live in DrawerService; this only reads pos_code off the token and
/// rejects an empty body, exactly as before.
/// </summary>
[Route("api/hardware")]
public sealed class HardwareController(IDrawerService drawer) : CandelaControllerBase
{
    /// <summary>
    /// POST /api/hardware/drawer — supply PrinterIp (LAN, raw TCP:9100) or PrinterName
    /// (Windows spooler). PrinterPort is TCP-only and defaults to 9100.
    /// </summary>
    [HttpPost("drawer")]
    public async Task<IActionResult> OpenDrawer([FromBody] DrawerRequest? req, CancellationToken ct)
    {
        if (req == null)
            return Fail(StatusCodes.Status400BadRequest, "Request body is required");

        var result = await drawer.OpenAsync(req, PosCode, ct);

        return new JsonResult(result);
    }
}
