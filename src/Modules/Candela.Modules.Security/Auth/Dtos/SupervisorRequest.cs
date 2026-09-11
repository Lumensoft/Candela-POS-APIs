using Newtonsoft.Json;

namespace Candela.Modules.Security.Auth.Dtos;

/// <summary>
/// Body of POST /api/auth/supervisor — a second user authorising the cashier to exceed a
/// discount or price limit. scope is accepted and unused, as before.
/// </summary>
public sealed class SupervisorRequest
{
    [JsonProperty("username")] public string? Username { get; set; }
    [JsonProperty("password")] public string? Password { get; set; }
    [JsonProperty("scope")] public string? Scope { get; set; }
}
