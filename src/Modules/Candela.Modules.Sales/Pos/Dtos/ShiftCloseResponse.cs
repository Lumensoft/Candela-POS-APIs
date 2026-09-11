using Newtonsoft.Json;

namespace Candela.Modules.Sales.Pos.Dtos;

/// <summary>
/// POST /api/pos/shift-close. The closed shift's reconciled figures, field-for-field as
/// the net48 endpoint returned them.
///
/// <c>cash_difference</c> is computed by POSCashManagmentDAL.UpdateShiftClosing (the
/// legacy wrapper reads it back off the model), so the reconciliation formula stays the
/// desktop's: CashCounted - (Opening + CashReceived + NetSales - CashSkimmed).
/// </summary>
public sealed class ShiftCloseResponse
{
    [JsonProperty("success")]
    public bool Success { get; set; } = true;

    [JsonProperty("closed_at")]
    public string ClosedAt { get; set; } = "";

    [JsonProperty("cash_counted")]
    public double CashCounted { get; set; }

    [JsonProperty("cash_submitted")]
    public double CashSubmitted { get; set; }

    [JsonProperty("closing_cash")]
    public double ClosingCash { get; set; }

    [JsonProperty("net_sales")]
    public double NetSales { get; set; }

    [JsonProperty("opening")]
    public double Opening { get; set; }

    [JsonProperty("cash_received")]
    public double CashReceived { get; set; }

    [JsonProperty("cash_skimmed")]
    public double CashSkimmed { get; set; }

    [JsonProperty("cash_difference")]
    public double CashDifference { get; set; }

    [JsonProperty("pos_code")]
    public string PosCode { get; set; } = "";

    [JsonProperty("shop_id")]
    public int ShopId { get; set; }
}
