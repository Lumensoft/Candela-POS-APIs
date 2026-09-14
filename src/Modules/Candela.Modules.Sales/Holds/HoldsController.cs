using Candela.Modules.Sales.Contracts;
using Candela.Platform.Api;
using Candela.Platform.Legacy;
using Candela.Shared;
using Candela.Shared.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Modules.Sales.Holds;

/// <summary>
/// Parked carts — the web equivalent of the desktop's Hold / Recall.
///
///   POST   parks a cart      -> legacy host (SaleAndReturnDAL.AddToHold)
///   GET    lists parked carts -> SQL here
///   DELETE discards one       -> SQL here (transaction)
///
/// Routes, request body (SaleRequest) and response shapes are unchanged from the net48
/// HoldsController, so the tablet cannot tell which process answered. No try/catch —
/// ExceptionHandlingMiddleware turns a NotFoundException into the same 404 body the
/// original returned.
/// </summary>
[Route("api/holds")]
public sealed class HoldsController(IHoldsRepository holds, ILegacyHostClient legacy) : CandelaControllerBase
{
    /// <summary>POST /api/holds — park the current cart.</summary>
    [HttpPost("")]
    public async Task<IActionResult> Park([FromBody] SaleRequest req)
    {
        if (req?.Items is null || req.Items.Count == 0)
            return new JsonResult(new { error = "items cannot be empty" })
            {
                StatusCode = StatusCodes.Status400BadRequest
            };

        // The legacy host owns the model-building and the AddToHold call, verbatim from
        // the old HoldsController. It returns the id it assigned.
        var result = await legacy.PostAsync<LegacyHoldResult>("holds", req, HttpContext.RequestAborted);

        return new JsonResult(ApiResponse<object>.Ok(new { hold_id = result.HoldId }));
    }

    /// <summary>GET /api/holds — every parked cart for this shop AND this POS terminal, newest first.</summary>
    [HttpGet("")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await holds.GetHoldsAsync(ShopId, PosCode, ct);
        return new JsonResult(new { success = true, count = rows.Count, data = rows });
    }

    /// <summary>DELETE /api/holds/{id} — discard a parked cart. 404 when it is not there.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await holds.DeleteHoldAsync(id, ShopId, ct);
        if (!deleted)
            throw new NotFoundException("Hold not found");

        return new JsonResult(ApiResponse<object>.Ok(new { deleted = true }));
    }

    /// <summary>The legacy host's reply from POST /legacy/holds: the id it assigned.</summary>
    private sealed class LegacyHoldResult
    {
        [Newtonsoft.Json.JsonProperty("hold_id")]
        public int HoldId { get; set; }
    }
}
