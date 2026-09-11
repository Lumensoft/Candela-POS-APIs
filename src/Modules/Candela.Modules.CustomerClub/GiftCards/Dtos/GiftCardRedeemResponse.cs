using Newtonsoft.Json;

namespace Candela.Modules.CustomerClub.GiftCards.Dtos;

/// <summary>
/// POST /api/gift-cards/redeem. Like validate, this one has no data envelope — the net48
/// endpoint returned success alongside the fields, so <c>success</c> is a member here
/// rather than the usual ApiResponse wrapper.
///
/// <c>redeemed</c> is the decimal off the request and <c>new_balance</c> the double the
/// balance query produced; both types are kept as they were so the numbers serialise
/// identically.
/// </summary>
public sealed class GiftCardRedeemResponse
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("card_id")]
    public int CardId { get; set; }

    [JsonProperty("ledger_id")]
    public int LedgerId { get; set; }

    [JsonProperty("redeemed")]
    public decimal Redeemed { get; set; }

    [JsonProperty("new_balance")]
    public double NewBalance { get; set; }
}
