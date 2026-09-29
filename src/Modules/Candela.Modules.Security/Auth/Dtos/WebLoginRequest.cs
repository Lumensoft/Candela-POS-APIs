using Newtonsoft.Json;

namespace Candela.Modules.Security.Auth.Dtos;

/// <summary>
/// Body of POST /api/auth/web-login — sign-in for Candela_WebInterface (back-office
/// screens: Cities and the rest of Configuration/Security/etc.).
///
/// Carries the same device_id contract as LoginRequest: AuthService.WebLoginAsync claims a
/// tblComputerList seat for it exactly like the tablet's AuthService.LoginAsync does, so a
/// browser profile is bound to a shop the same way a till is. See AuthService.WebLoginAsync.
/// </summary>
public sealed class WebLoginRequest
{
    [JsonProperty("username")] public string? Username { get; set; }
    [JsonProperty("password")] public string? Password { get; set; }
    [JsonProperty("device_id")] public string? DeviceId { get; set; }
}
