namespace Candela.Modules.Security.Auth.Dtos;

/// <summary>
/// Body of POST /api/auth/web-login — sign-in for Candela_WebInterface (back-office
/// screens: Cities and the rest of Configuration/Security/etc.), not the tablet.
///
/// Deliberately has no device_id: a browser tab is not a tablet seat, so this path never
/// touches tblComputerList. See AuthService.WebLoginAsync for why reusing the POS login's
/// device-claiming here would be wrong (it would consume or require a fake tablet slot for
/// every office user).
/// </summary>
public sealed class WebLoginRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
}
