using Newtonsoft.Json;

namespace Candela.Modules.CustomerClub.Customers.Dtos;

/// <summary>
/// GET /api/customers/{id}/credit-outstanding.
///
/// Note there is no success/data envelope here — the net48 endpoint returned these four
/// fields at the top level and the checkout screen reads them that way, so the shape is
/// kept exactly as it was even though it differs from the rest of the API.
/// </summary>
public sealed class CreditOutstandingResponse
{
    [JsonProperty("member_id")]
    public int MemberId { get; set; }

    [JsonProperty("credit_limit")]
    public decimal CreditLimit { get; set; }

    [JsonProperty("credit_outstanding")]
    public decimal CreditOutstanding { get; set; }

    /// <summary>Limit minus outstanding, floored at zero — never negative, as before.</summary>
    [JsonProperty("credit_available")]
    public decimal CreditAvailable { get; set; }
}
