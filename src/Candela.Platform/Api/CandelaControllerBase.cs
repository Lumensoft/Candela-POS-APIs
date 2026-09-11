using Candela.Shared.Auth;
using Candela.Platform.Auth;
using Candela.Platform.Errors;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Platform.Api;

/// <summary>
/// Base for every POS controller.
///
/// The .NET Framework host read session values as untyped casts off
/// Request.Properties — 107 of them, each able to throw its own InvalidCastException.
/// Here they are typed properties resolved once from the validated token, so a
/// controller never restates how a claim is stored.
/// </summary>
[ApiController]
public abstract class CandelaControllerBase : ControllerBase
{
    private int? _userId;
    private int? _shopId;
    private int? _groupType;
    private string? _posCode;
    private string? _userName;
    private string? _deviceId;
    private string? _groupName;
    private decimal? _saleReturnLimit;

    protected int UserId => _userId ??= JwtHelper.GetUserId(User);
    protected int ShopId => _shopId ??= JwtHelper.GetShopId(User);
    protected int GroupType => _groupType ??= JwtHelper.GetGroupType(User);
    protected string PosCode => _posCode ??= JwtHelper.GetPosCode(User);
    protected string UserName => _userName ??= JwtHelper.GetUserName(User);

    // Only the refresh path needs these three: a reissued token has to carry every claim
    // the old one did, and a claim that is quietly dropped there is a right the cashier
    // loses for the rest of the shift.
    protected string DeviceId => _deviceId ??= JwtHelper.GetDeviceId(User);
    protected string GroupName => _groupName ??= JwtHelper.GetGroupName(User);
    protected decimal SaleReturnLimit => _saleReturnLimit ??= JwtHelper.GetSaleReturnLimit(User);

    /// <summary>
    /// The bearer token exactly as it arrived — needed only by refresh and logout, to
    /// revoke it. JwtAuthMiddleware stores it after validating the token, so it is null on
    /// the anonymous login route.
    /// </summary>
    protected string? RawToken => HttpContext.Items["raw_token"] as string;

    protected bool HasBelowCostRight => JwtHelper.GetBelowCostRight(User);
    protected HashSet<string> ControlRights => JwtHelper.GetControlRightsSet(User);

    /// <summary>
    /// The list envelope the tablet already receives from Masters and Products:
    ///     { "success": true, "count": n, "data": [ ... ] }
    /// Kept byte-identical on purpose — this is a wire contract, not a style choice.
    /// </summary>
    protected IActionResult Rows(IReadOnlyCollection<Dictionary<string, object?>> rows) =>
        new JsonResult(new { success = true, count = rows.Count, data = rows });

    /// <summary>
    /// A business failure the caller can act on: keeps its own status code and its own
    /// message. Never routed through ApiError, which is only for unexpected faults.
    /// </summary>
    protected IActionResult Fail(int statusCode, string message) =>
        new JsonResult(new { error = message }) { StatusCode = statusCode };

    /// <summary>Unexpected fault: logged in full, generic body, correlation id returned.</summary>
    protected IActionResult Internal(Exception ex, string context) =>
        ApiError.Internal(ex, context);
}
