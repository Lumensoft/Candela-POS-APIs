using Candela.Modules.CustomerClub.Loyalty.Dtos;
using Candela.Platform.Api;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Modules.CustomerClub.Loyalty;

/// <summary>
/// Ported from the net48 LoyaltyController without touching the SQL.
///
/// Route, response shape and field names are unchanged, so the tablet cannot tell which
/// process answered. The route prefix is <c>api/customers</c> because that is the URL
/// the tablet already calls — it is not a module boundary; loyalty is a CustomerClub
/// concern and the code lives there.
///
/// No service class: it is one lookup with no rules. No try/catch: a fault is thrown and
/// answered by ExceptionHandlingMiddleware. A member with no earnings row returns a zero
/// body, not 404 — same as the desktop's frmPointRedemption.
/// </summary>
[Route("api/customers")]
public sealed class LoyaltyController(ILoyaltyRepository loyalty) : CandelaControllerBase
{
    /// <summary>
    /// GET /api/customers/{memberId}/loyalty-points — points balance, one-point value,
    /// minimum redemption threshold and the birthday-points sub-total, for the checkout
    /// screen.
    /// </summary>
    [HttpGet("{memberId:int}/loyalty-points")]
    public async Task<IActionResult> GetLoyaltyPoints(int memberId, CancellationToken ct)
    {
        var data = await loyalty.GetPointsAsync(memberId, ShopId, ct) ?? LoyaltyPointsResponse.Zero;
        return new JsonResult(new { success = true, data });
    }
}
