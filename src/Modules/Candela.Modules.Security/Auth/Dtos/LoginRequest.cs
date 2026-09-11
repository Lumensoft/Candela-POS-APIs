using Newtonsoft.Json;

namespace Candela.Modules.Security.Auth.Dtos;

/// <summary>
/// Body of POST /api/auth/login. Names copied from the net48 LoginRequest.
///
/// friendly_name and device_model are accepted and never used — the net48 endpoint bound
/// them and ignored them too. They stay so an existing tablet build's payload is not
/// rejected.
/// </summary>
public sealed class LoginRequest
{
    [JsonProperty("device_id")] public string? DeviceId { get; set; }
    [JsonProperty("friendly_name")] public string? FriendlyName { get; set; }
    [JsonProperty("device_model")] public string? DeviceModel { get; set; }
    [JsonProperty("username")] public string? Username { get; set; }
    [JsonProperty("password")] public string? Password { get; set; }
}
