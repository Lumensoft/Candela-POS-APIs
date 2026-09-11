using Newtonsoft.Json;

namespace Candela.Modules.Sales.Pos.Dtos;

/// <summary>
/// The body returned by POST /api/pos/cash-skim and POST /api/pos/cash-receive.
///
/// The net48 endpoints returned almost the same object; the only difference is the
/// timestamp key — <c>skimmed_at</c> for a skim, <c>received_at</c> for a receive. The
/// other one is left null and NullValueHandling.Ignore drops it, so each endpoint still
/// serialises exactly its original field set.
///
/// <c>amount</c> is the decimal echoed back from the request, unrounded, as before. The
/// tablet reads <c>pos_cash_management_id</c> to then fetch that shift's detail.
/// </summary>
public sealed class CashMovementResponse
{
    [JsonProperty("success")]
    public bool Success { get; set; } = true;

    [JsonProperty("amount")]
    public decimal Amount { get; set; }

    [JsonProperty("notes")]
    public string Notes { get; set; } = "";

    [JsonProperty("pos_code")]
    public string PosCode { get; set; } = "";

    [JsonProperty("shop_id")]
    public int ShopId { get; set; }

    [JsonProperty("skimmed_at")]
    public string? SkimmedAt { get; set; }

    [JsonProperty("received_at")]
    public string? ReceivedAt { get; set; }

    [JsonProperty("pos_cash_management_id")]
    public int PosCashManagementId { get; set; }
}
