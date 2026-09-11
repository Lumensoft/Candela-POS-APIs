using Newtonsoft.Json;

namespace Candela.Modules.CustomerClub.GiftCards.Dtos;

/// <summary>
/// Body of POST /api/gift-cards/topup. The [JsonProperty] names are copied from the
/// net48 GiftCardTopupRequest — the tablet posts snake_case, so removing them would
/// silently null every field.
///
/// member_name, phone_mobile and credit_card_id are accepted and ignored, exactly as
/// before: the net48 endpoint bound them but never wrote them. They stay on the contract
/// so an existing caller's payload is not rejected.
/// </summary>
public sealed class GiftCardTopupRequest
{
    [JsonProperty("card_no")] public string? CardNo { get; set; }
    [JsonProperty("topup_amount")] public decimal TopupAmount { get; set; }
    [JsonProperty("cash_amount")] public decimal CashAmount { get; set; }
    [JsonProperty("card_amount")] public decimal CardAmount { get; set; }
    [JsonProperty("exp_days")] public int ExpDays { get; set; }
    [JsonProperty("member_name")] public string? MemberName { get; set; }
    [JsonProperty("phone_mobile")] public string? PhoneMobile { get; set; }
    [JsonProperty("credit_card_id")] public int CreditCardId { get; set; }
}
