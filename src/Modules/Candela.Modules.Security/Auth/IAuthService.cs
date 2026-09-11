using Candela.Modules.Security.Auth.Dtos;

namespace Candela.Modules.Security.Auth;

/// <summary>
/// The rules behind signing in. This slice earns a service because almost all of it is
/// decisions rather than data: whether the password matches, whether the tablet is
/// allowed, whether the user's group may work at the shop the tablet belongs to, and —
/// for an override — the three conditions the desktop's Override Login applies.
///
/// Every refusal is thrown as the matching ApiException so the middleware writes the same
/// status and body the net48 controller returned.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Validates credentials, registers or claims the tablet seat, checks shop
    /// entitlement, and issues the JWT plus everything the tablet caches for the shift.
    /// </summary>
    Task<LoginResponse> LoginAsync(LoginRequest req, CancellationToken ct);

    /// <summary>
    /// Validates credentials for Candela_WebInterface and issues a JWT. No device, no
    /// tablet seat, no shop-entitlement gate — a back-office login is not shop-bound the
    /// way a till is. See the type doc on WebLoginRequest.
    /// </summary>
    Task<WebLoginResponse> WebLoginAsync(WebLoginRequest req, CancellationToken ct);

    /// <summary>
    /// The control rights this user's group has been granted on one Candela screen —
    /// New/Save/Update/Delete for frmDefCity, and the same for any other form. Empty when
    /// the group has no rows configured for that form (the screen renders read-only,
    /// exactly like the desktop does before Group Rights has been set up for it).
    /// </summary>
    Task<FormRightsResponse> GetFormRightsAsync(int userId, string formName, CancellationToken ct);

    /// <summary>
    /// Verifies a second user so the cashier may exceed a limit. Returns who authorised
    /// it; the cashier's own token is never reissued.
    /// </summary>
    Task<SupervisorResponse> SupervisorAsync(SupervisorRequest req, int cashierUserId,
        int cashierShopId, int cashierGroupType, CancellationToken ct);

    /// <summary>Revokes a token by recording its signature until that token's own expiry.</summary>
    Task BlocklistTokenAsync(string rawToken, CancellationToken ct);
}
