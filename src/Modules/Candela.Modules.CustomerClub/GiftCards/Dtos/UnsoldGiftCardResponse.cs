using Newtonsoft.Json;

namespace Candela.Modules.CustomerClub.GiftCards.Dtos;

/// <summary>
/// The <c>data</c> body of GET /api/gift-cards/unsold — the next gift card number
/// available to load, already formatted as the composite the cashier reads off the card
/// (ShopCode-PaddedCardNo-TypeCode).
/// </summary>
public sealed class UnsoldGiftCardResponse
{
    [JsonProperty("card_no")]
    public string CardNo { get; set; } = "";
}
