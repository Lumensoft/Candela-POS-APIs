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
///   DELETE shifts/{closingId}                     delete a whole shift closing -> legacy DAL
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
    /// GET /api/pos/shifts?from=&amp;to=&amp;pos=&amp;page=&amp;page_size= — closings of this shop.
    /// <c>pos</c> is the POS filter: absent = this till (unchanged behaviour), a POS code = that till, "*" = every
    /// till of the shop. page defaults to 1, page_size to 20 (1-200), matching the net48 clamps.
    /// </summary>
    [HttpGet("shifts")]
    public async Task<IActionResult> SearchShifts(
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery(Name = "pos")] string? posFilterParam = null,
        [FromQuery] int page = 1,
        [FromQuery] int page_size = 20,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (page_size < 1 || page_size > 200) page_size = 20;

        // The shop always comes from the token, so another shop's tills can never be reached from here.
        var posFilter = string.IsNullOrWhiteSpace(posFilterParam) ? PosCode
                      : posFilterParam == "*" ? null
                      : posFilterParam.Trim();

        var result = await pos.GetShiftsAsync(ShopId, posFilter, from, to, page, page_size, ct);
        return new JsonResult(result);
    }

    /// <summary>GET /api/pos/shift-pos-codes — the shop's POS codes, for the shift search's POS filter.</summary>
    [HttpGet("shift-pos-codes")]
    public async Task<IActionResult> GetShiftPosCodes(CancellationToken ct)
        => new JsonResult(new { success = true, data = await pos.GetShopPosCodesAsync(ShopId, ct) });

    /// <summary>GET /api/pos/shifts/{closingId}/detail — the entries behind one closing's totals.</summary>
    [HttpGet("shifts/{closingId:int}/detail")]
    public async Task<IActionResult> GetShiftDetail(int closingId, CancellationToken ct)
        => new JsonResult(await pos.GetShiftDetailAsync(closingId, ShopId, PosCode, ct));

    /// <summary>
    /// DELETE /api/pos/shifts/{closingId} — delete a whole shift closing (frmPOSCashManagement btnDelete).
    /// 422 with Candela's wording when it is not this till's latest shift or the shop day is closed.
    /// The btnDelete right is checked on the screen, as with the other shift rights.
    /// </summary>
    [HttpDelete("shifts/{closingId:int}")]
    public async Task<IActionResult> DeleteShift(int closingId, CancellationToken ct)
        => new JsonResult(await pos.DeleteShiftAsync(closingId, ct));

    /// <summary>
    /// DELETE /api/pos/shifts/{closingId}/detail/{detailId} — remove one Received/Skimmed
    /// entry and subtract its amount from the shift's running total. 404 when the entry is
    /// not this till's.
    /// </summary>
    [HttpDelete("shifts/{closingId:int}/detail/{detailId:int}")]
    public async Task<IActionResult> DeleteShiftDetail(int closingId, int detailId, CancellationToken ct)
    {
        // frmPOSCashManagement.vb:289 — the delete button on a detail row exists only for a group holding
        // the screen's btnUpdate right. Those rights ride in the JWT, prefixed with the form name.
        if (!ControlRights.Contains("frmPOSCashFlow.btnUpdate"))
            return Fail(StatusCodes.Status403Forbidden, "You do not have the right to delete cash entries.");

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
