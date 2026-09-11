using Newtonsoft.Json;

namespace Candela.Modules.CustomerClub.GiftCards.Dtos;

/// <summary>
/// The <c>data</c> body of POST /api/gift-cards/topup: the ledger row that was written
/// and the card it was written against.
/// </summary>
public sealed class GiftCardTopupResponse
{
    [JsonProperty("ledger_id")]
    public int LedgerId { get; set; }

    [JsonProperty("card_id")]
    public int CardId { get; set; }
}
