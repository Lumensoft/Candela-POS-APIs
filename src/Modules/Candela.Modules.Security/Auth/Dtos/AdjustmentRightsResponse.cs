using Newtonsoft.Json;

namespace Candela.Modules.Security.Auth.Dtos;

/// <summary>
/// The <c>data</c> body of GET /api/auth/adjustment-rights.
///
/// can_adjust is either flag; is_open reports the open-adjustment flag on its own, which
/// is what decides whether the cashier may type any amount rather than only a capped one.
/// </summary>
public sealed class AdjustmentRightsResponse
{
    [JsonProperty("can_adjust")] public bool CanAdjust { get; set; }
    [JsonProperty("is_open")] public bool IsOpen { get; set; }
}
