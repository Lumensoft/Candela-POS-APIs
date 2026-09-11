using Candela.Modules.Security.Auth.Dtos;
using Candela.Platform.Api;
using Candela.Shared;
using Candela.Shared.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Modules.Security.Auth;

/// <summary>
/// Sign in, sign out and override, ported from the net48 AuthController.
///
///   POST /api/auth/login             credentials -> JWT + the shift's cached settings
///   POST /api/auth/refresh           new expiry window, old token revoked
///   POST /api/auth/logout            revoke the current token
///   POST /api/auth/supervisor        a second user authorises an over-limit action
///   GET  /api/auth/adjustment-rights may this user apply a manual adjustment
///   POST /api/auth/web-login         Candela_WebInterface sign-in (no tablet involved)
///   GET  /api/auth/form-rights/{f}   what this user's group may do on screen {f}
///
/// Nothing is forwarded to the legacy host: Auth never called the Candela DAL. The only
/// Candela code involved is the password cipher, which runs here through
/// Candela.Platform.Security.SymmetricEncryption.
///
/// /api/auth/login and /api/auth/web-login are the only anonymous routes in the
/// application — JwtAuthMiddleware exempts them by path, exactly as the net48
/// JwtAuthHandler exempted /api/auth/login. Every other action here runs with a validated
/// token, which is why they read the caller off the base class rather than the body.
/// </summary>
[Route("api/auth")]
public sealed class AuthController(IAuthService auth, IAuthRepository repo) : CandelaControllerBase
{
    /// <summary>POST /api/auth/login — the only route that does not need a token.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest? req, CancellationToken ct)
    {
        if (req == null || string.IsNullOrEmpty(req.Username) || string.IsNullOrEmpty(req.Password))
            return Fail(StatusCodes.Status400BadRequest, "username and password are required");

        if (string.IsNullOrEmpty(req.DeviceId))
            return Fail(StatusCodes.Status400BadRequest, "device_id is required");

        var data = await auth.LoginAsync(req, ct);

        return new JsonResult(ApiResponse<LoginResponse>.Ok(data));
    }

    /// <summary>
    /// POST /api/auth/refresh — a fresh expiry window from the claims already on the
    /// token, and the old token revoked so it cannot be replayed.
    ///
    /// Every claim is carried across, rights included. Omitting the last two arguments to
    /// Generate() is what used to strip the cashier of BelowCostSales and every other
    /// frmSaleAndReturn right for the rest of the shift, silently, on each refresh.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var rawToken = RawToken;
        if (string.IsNullOrEmpty(rawToken))
            return Fail(StatusCodes.Status401Unauthorized, "No token found on request");

        await auth.BlocklistTokenAsync(rawToken, ct);

        var newToken = JwtHelper.Generate(UserId, UserName, ShopId, PosCode, DeviceId,
            GroupName, GroupType, SaleReturnLimit, HasBelowCostRight,
            string.Join(",", ControlRights));

        return new JsonResult(ApiResponse<object>.Ok(new { token = newToken }));
    }

    /// <summary>
    /// POST /api/auth/logout — revoke this token now. The tablet seat stays registered in
    /// tblComputerList, so the next login reuses the same slot.
    ///
    /// Answers 200 even with no token: logging out is idempotent and the caller's intent
    /// is already satisfied.
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var rawToken = RawToken;
        if (!string.IsNullOrEmpty(rawToken))
            await auth.BlocklistTokenAsync(rawToken, ct);

        return new JsonResult(ApiResponse<object>.Ok(new { logged_out = true }));
    }

    /// <summary>
    /// POST /api/auth/supervisor — verify a second user so the cashier may exceed a
    /// discount or price limit. The cashier's token is not reissued; React holds the
    /// returned name and id for the length of the elevated action.
    /// </summary>
    [HttpPost("supervisor")]
    public async Task<IActionResult> SupervisorLogin([FromBody] SupervisorRequest? req, CancellationToken ct)
    {
        if (req == null || string.IsNullOrEmpty(req.Username) || string.IsNullOrEmpty(req.Password))
            return Fail(StatusCodes.Status400BadRequest, "username and password are required");

        var data = await auth.SupervisorAsync(req, UserId, ShopId, GroupType, ct);

        return new JsonResult(ApiResponse<SupervisorResponse>.Ok(data));
    }

    /// <summary>
    /// GET /api/auth/adjustment-rights — the per-user flags on TblSecurityUser, not group
    /// control rights. The config gate (AdjustmentLimit &gt; 0) is checked by the tablet
    /// from IndexedDB before it calls this.
    /// </summary>
    [HttpGet("adjustment-rights")]
    public async Task<IActionResult> AdjustmentRights(CancellationToken ct)
    {
        var rights = await repo.GetAdjustmentRightsAsync(UserId, ct);

        return new JsonResult(ApiResponse<AdjustmentRightsResponse>.Ok(new AdjustmentRightsResponse
        {
            CanAdjust = rights.ApplyOpenAdjustment || rights.ApplyAdjustment,
            IsOpen = rights.ApplyOpenAdjustment,
        }));
    }

    /// <summary>
    /// POST /api/auth/web-login — sign-in for Candela_WebInterface. The other anonymous
    /// route in the application; see the class doc.
    /// </summary>
    [HttpPost("web-login")]
    public async Task<IActionResult> WebLogin([FromBody] WebLoginRequest? req, CancellationToken ct)
    {
        if (req == null || string.IsNullOrEmpty(req.Username) || string.IsNullOrEmpty(req.Password))
            return Fail(StatusCodes.Status400BadRequest, "username and password are required");

        var data = await auth.WebLoginAsync(req, ct);

        return new JsonResult(ApiResponse<WebLoginResponse>.Ok(data));
    }

    /// <summary>
    /// GET /api/auth/form-rights/{formName} — the New/Save/Update/Delete-style control
    /// rights this user's group has on one screen, e.g. "frmDefCity". Any authenticated
    /// user may ask about any form: this only reveals which buttons a screen should show,
    /// the same information the desktop already keeps in a client-side DataTable, and it
    /// carries no data of its own.
    /// </summary>
    [HttpGet("form-rights/{formName}")]
    public async Task<IActionResult> FormRights(string formName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(formName))
            return Fail(StatusCodes.Status400BadRequest, "formName is required");

        var data = await auth.GetFormRightsAsync(UserId, formName, ct);

        return new JsonResult(ApiResponse<FormRightsResponse>.Ok(data));
    }
}
