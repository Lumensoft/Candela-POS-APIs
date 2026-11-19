using Newtonsoft.Json;

namespace Candela.Modules.CustomerClub.Loyalty.Dtos;

/// <summary>POST /api/customers/{memberId}/verify-pin — the PIN the cashier keyed in, and the customer's own shop.</summary>
public sealed class VerifyPinRequest
{
    /// <summary>What was typed. Empty is allowed: it asks "does this customer have a PIN at all?".</summary>
    [JsonProperty("pin")]
    public string? Pin { get; set; }

    /// <summary>The customer's shop (tblMemberInfo.shop_id) — a customer belongs to the shop that registered them.</summary>
    [JsonProperty("shop_id")]
    public int ShopId { get; set; }
}

/// <summary>
/// <c>pin_required</c> false = the customer has no PIN on file, so there is nothing to ask (<c>valid</c> is true).
/// Otherwise <c>valid</c> says whether the typed PIN matched. The PIN itself never leaves the server.
/// </summary>
public sealed class VerifyPinResponse
{
    [JsonProperty("pin_required")] public bool PinRequired { get; set; }
    [JsonProperty("valid")]        public bool Valid { get; set; }
}
