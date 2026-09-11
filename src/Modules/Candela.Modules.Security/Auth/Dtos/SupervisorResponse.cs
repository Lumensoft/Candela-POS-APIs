using Newtonsoft.Json;

namespace Candela.Modules.Security.Auth.Dtos;

/// <summary>
/// The <c>data</c> body of POST /api/auth/supervisor. The cashier's own token is never
/// reissued — React holds these two values for the length of the elevated action.
/// </summary>
public sealed class SupervisorResponse
{
    [JsonProperty("supervisor_id")] public int SupervisorId { get; set; }
    [JsonProperty("supervisor_name")] public string SupervisorName { get; set; } = "";
}
