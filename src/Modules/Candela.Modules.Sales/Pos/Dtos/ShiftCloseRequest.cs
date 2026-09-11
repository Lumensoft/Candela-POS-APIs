using Newtonsoft.Json;

namespace Candela.Modules.Sales.Pos.Dtos;

/// <summary>
/// Body of POST /api/pos/shift-close. Names and types copied from the net48
/// ShiftCloseRequest: cash_counted / cash_submitted are double (not decimal), notes has
/// no JsonProperty (bare "notes"), and closing_date is a nullable DateTime the frontend
/// picks — the server falls back to its own clock when it is omitted.
/// </summary>
public sealed class ShiftCloseRequest
{
    [JsonProperty("cash_counted")]
    public double CashCounted { get; set; }

    [JsonProperty("cash_submitted")]
    public double CashSubmitted { get; set; }

    public string? Notes { get; set; }

    [JsonProperty("closing_date")]
    public DateTime? ClosingDate { get; set; }

    public DenominationsDto? Denominations { get; set; }
}
