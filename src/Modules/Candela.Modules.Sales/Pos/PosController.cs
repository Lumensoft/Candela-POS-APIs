using Candela.Modules.Sales.Pos.Dtos;
using Candela.Platform.Api;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Modules.Sales.Pos;

/// <summary>
/// Till cash management, ported from the net48 PosController.
///
///   GET    cash-status                            live drawer breakdown for this till
///   GET    shift-status                           same, plus net_sales and open/closed history
///   GET    shifts                                 paginated shift-closing search + totals
///   GET    shifts/{closingId}/detail              the Received / Skimmed entries under one closing
///   DELETE shifts/{closingId}/detail/{detailId}   remove one entry, roll its amount back out
///   POST   cash-skim                              record a mid-shift removal   -> legacy DAL
///   POST   cash-receive                           record a mid-shift top-up    -> legacy DAL
///   POST   shift-open                             open the till shift          -> legacy DAL
///   POST   shift-close                            close and reconcile          -> legacy DAL
///
/// The reads and the DELETE are plain SQL here. The four writes go through
/// POSCashManagmentDAL — which keeps Candela's activity log and shift bookkeeping
/// correct — so they are forwarded to the legacy host. Routes, bodies and response
/// shapes are unchanged; shop_id and pos_code come from the token, never the body.
/// </summary>
[Route("api/pos")]
public sealed class PosController(IPosService pos) : CandelaControllerBase
{
    /// <summary>GET /api/pos/cash-status — opening + received + cash sales - skimmed.</summary>
    [HttpGet("cash-status")]
    public async Task<IActionResult> GetCashStatus(CancellationToken ct)
        => new JsonResult(await pos.GetCashStatusAsync(ShopId, PosCode, ct));

    /// <summary>GET /api/pos/shift-status — the cash-status breakdown plus net_sales.</summary>
    [HttpGet("shift-status")]
    public async Task<IActionResult> GetShiftStatus(CancellationToken ct)
        => new JsonResult(await pos.GetShiftStatusAsync(ShopId, PosCode, ct));

    /// <summary>
    /// GET /api/pos/shifts?from=&amp;to=&amp;page=&amp;page_size= — this till's closings.
    /// page defaults to 1, page_size to 20 (1-200), matching the net48 clamps.
    /// </summary>
    [HttpGet("shifts")]
    public async Task<IActionResult> SearchShifts(
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int page_size = 20,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (page_size < 1 || page_size > 200) page_size = 20;

        var result = await pos.GetShiftsAsync(ShopId, PosCode, from, to, page, page_size, ct);
        return new JsonResult(result);
    }

    /// <summary>GET /api/pos/shifts/{closingId}/detail — the entries behind one closing's totals.</summary>
    [HttpGet("shifts/{closingId:int}/detail")]
    public async Task<IActionResult> GetShiftDetail(int closingId, CancellationToken ct)
        => new JsonResult(await pos.GetShiftDetailAsync(closingId, ShopId, PosCode, ct));

    /// <summary>
    /// DELETE /api/pos/shifts/{closingId}/detail/{detailId} — remove one Received/Skimmed
    /// entry and subtract its amount from the shift's running total. 404 when the entry is
    /// not this till's.
    /// </summary>
    [HttpDelete("shifts/{closingId:int}/detail/{detailId:int}")]
    public async Task<IActionResult> DeleteShiftDetail(int closingId, int detailId, CancellationToken ct)
    {
        var deleted = await pos.DeleteShiftDetailAsync(closingId, detailId, ShopId, PosCode, ct);
        if (!deleted)
            return Fail(StatusCodes.Status404NotFound, "Entry not found.");

        return new JsonResult(new { success = true });
    }

    /// <summary>POST /api/pos/cash-skim — { amount, notes }. amount must be &gt; 0.</summary>
    [HttpPost("cash-skim")]
    public async Task<IActionResult> CashSkim([FromBody] CashMovementRequest? req, CancellationToken ct)
    {
        if (req == null || req.Amount <= 0)
            return Fail(StatusCodes.Status400BadRequest, "amount is required and must be > 0");

        return new JsonResult(await pos.CashSkimAsync(req, ct));
    }

    /// <summary>POST /api/pos/cash-receive — { amount, notes }. amount must be &gt; 0.</summary>
    [HttpPost("cash-receive")]
    public async Task<IActionResult> ReceiveCash([FromBody] CashMovementRequest? req, CancellationToken ct)
    {
        if (req == null || req.Amount <= 0)
            return Fail(StatusCodes.Status400BadRequest, "amount is required and must be > 0");

        return new JsonResult(await pos.CashReceiveAsync(req, ct));
    }

    /// <summary>POST /api/pos/shift-open — no body. Returns the opening cash whether it opened one or found one.</summary>
    [HttpPost("shift-open")]
    public async Task<IActionResult> OpenShift(CancellationToken ct)
        => new JsonResult(await pos.OpenShiftAsync(ct));

    /// <summary>
    /// POST /api/pos/shift-close — { cash_counted, cash_submitted, notes, denominations,
    /// closing_date }. cash_counted must be &gt;= 0; 422 when no shift is open.
    /// </summary>
    [HttpPost("shift-close")]
    public async Task<IActionResult> CloseShift([FromBody] ShiftCloseRequest? req, CancellationToken ct)
    {
        if (req == null || req.CashCounted < 0)
            return Fail(StatusCodes.Status400BadRequest, "cash_counted is required and must be >= 0");

        return new JsonResult(await pos.CloseShiftAsync(req, ShopId, PosCode, ct));
    }
}
