using Newtonsoft.Json;

namespace Candela.Modules.CustomerClub.GiftCards.Dtos;

/// <summary>
/// Body of POST /api/gift-cards/validate.
/// amount = 0 asks only "is this card usable"; amount &gt; 0 also checks that the balance
/// covers it. Same two fields, same names, as the net48 request class.
/// </summary>
public sealed class GiftCardValidateRequest
{
    [JsonProperty("card_no")] public string? CardNo { get; set; }
    [JsonProperty("amount")] public decimal Amount { get; set; }
}
