using Newtonsoft.Json;

namespace Candela.Modules.CustomerClub.GiftCards.Dtos;

/// <summary>
/// Body of POST /api/gift-cards/redeem — a standalone redemption, recorded independently
/// of any sale. sale_id is optional and links the ledger row to a sale when there is one;
/// redemptions that are part of a sale go through gift_card_payments[] on POST /api/sales
/// instead, where SaleAndReturnDAL writes the ledger row inside the sale transaction.
/// </summary>
public sealed class GiftCardRedeemRequest
{
    [JsonProperty("card_id")] public int CardId { get; set; }
    [JsonProperty("amount")] public decimal Amount { get; set; }
    [JsonProperty("sale_id")] public int SaleId { get; set; }
}
