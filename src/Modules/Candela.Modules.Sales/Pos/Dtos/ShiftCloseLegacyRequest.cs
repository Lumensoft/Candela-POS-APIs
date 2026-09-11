using Newtonsoft.Json;

namespace Candela.Modules.Sales.Pos.Dtos;

/// <summary>
/// The internal request PosService sends to POST /legacy/pos/shift-close — NOT a public
/// contract, and not what the tablet posts.
///
/// It carries the shift breakdown that Candela.Api computed (Opening / CashReceived /
/// CashSkimmed / NetSales) alongside the cashier's counted/submitted figures, so the
/// legacy wrapper can build the POSCashManagment model and call UpdateShiftClosing
/// without re-running the breakdown query. The optional denomination count rides along
/// so the wrapper can write tblPOSShiftCashCount right after the close, as the net48
/// endpoint did.
/// </summary>
public sealed class ShiftCloseLegacyRequest
{
    [JsonProperty("pos_cash_management_id")]
    public int PosCashManagementId { get; set; }

    [JsonProperty("opening")]
    public double Opening { get; set; }

    [JsonProperty("cash_received")]
    public double CashReceived { get; set; }

    [JsonProperty("cash_skimmed")]
    public double CashSkimmed { get; set; }

    [JsonProperty("net_sales")]
    public double NetSales { get; set; }

    [JsonProperty("cash_counted")]
    public double CashCounted { get; set; }

    [JsonProperty("cash_submitted")]
    public double CashSubmitted { get; set; }

    [JsonProperty("closing_cash")]
    public double ClosingCash { get; set; }

    [JsonProperty("notes")]
    public string Notes { get; set; } = "";

    /// <summary>"yyyy-MM-dd HH:mm:ss" — the frontend's closing_date, or the server clock.</summary>
    [JsonProperty("closed_at")]
    public string ClosedAt { get; set; } = "";

    [JsonProperty("denominations")]
    public DenominationsDto? Denominations { get; set; }
}
