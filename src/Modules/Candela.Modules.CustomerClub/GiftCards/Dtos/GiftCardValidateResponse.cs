using Newtonsoft.Json;

namespace Candela.Modules.CustomerClub.GiftCards.Dtos;

/// <summary>
/// POST /api/gift-cards/validate. No success/data envelope — the net48 endpoint returned
/// these fields at the top level and the payment screen reads them that way.
///
/// Every field except <c>valid</c> is nullable so the serializer's NullValueHandling.Ignore
/// drops it, which is how the four legacy bodies are reproduced exactly:
///
///   not found (404)  { valid: false, reason: "…" }
///   deactivated      { valid: false, card_id: n, reason: "…" }
///   no balance       { valid: false, card_id: n, display_no: "…", available_balance: 0, reason: "…" }
///   ok               { valid: true,  card_id: n, display_no: "…", available_balance: 500.0 }
/// </summary>
public sealed class GiftCardValidateResponse
{
    [JsonProperty("valid")]
    public bool Valid { get; set; }

    [JsonProperty("card_id")]
    public int? CardId { get; set; }

    [JsonProperty("display_no")]
    public string? DisplayNo { get; set; }

    /// <summary>
    /// Typed as object, not double, on purpose. The net48 endpoint wrote a literal int
    /// <c>0</c> in the no-balance branch and the double <c>balance</c> everywhere else,
    /// so Newtonsoft emitted <c>0</c> there and <c>500.0</c> here. A double would turn
    /// that first case into <c>0.0</c> — harmless to a JSON parser, but this is a frozen
    /// contract and the cheapest way to keep it frozen is to not change the type.
    /// </summary>
    [JsonProperty("available_balance")]
    public object? AvailableBalance { get; set; }

    [JsonProperty("reason")]
    public string? Reason { get; set; }
}
