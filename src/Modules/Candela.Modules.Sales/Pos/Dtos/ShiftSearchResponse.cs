using Newtonsoft.Json;

namespace Candela.Modules.Sales.Pos.Dtos;

/// <summary>
/// GET /api/pos/shifts — one page of shift closings plus the unpaginated totals for the
/// whole filtered set, matching the legacy grid's summary footer.
///
/// <c>data</c> stays a list of dictionaries: the SELECT's column aliases (closing_id,
/// cash_counted, cash_difference, …) are the wire contract, as with the other list
/// endpoints.
/// </summary>
public sealed class ShiftSearchResponse
{
    [JsonProperty("success")]
    public bool Success { get; set; } = true;

    [JsonProperty("total")]
    public int Total { get; set; }

    [JsonProperty("page")]
    public int Page { get; set; }

    [JsonProperty("page_size")]
    public int PageSize { get; set; }

    [JsonProperty("count")]
    public int Count { get; set; }

    [JsonProperty("data")]
    public IReadOnlyList<Dictionary<string, object?>> Data { get; set; } = new List<Dictionary<string, object?>>();

    [JsonProperty("totals")]
    public ShiftSearchTotals Totals { get; set; } = new();
}

/// <summary>The eight SUM(...) columns the totals query returns, all doubles as before.</summary>
public sealed class ShiftSearchTotals
{
    [JsonProperty("cash_counted")] public double CashCounted { get; set; }
    [JsonProperty("cash_submitted")] public double CashSubmitted { get; set; }
    [JsonProperty("closing_cash")] public double ClosingCash { get; set; }
    [JsonProperty("net_sales")] public double NetSales { get; set; }
    [JsonProperty("opening")] public double Opening { get; set; }
    [JsonProperty("cash_skimmed")] public double CashSkimmed { get; set; }
    [JsonProperty("cash_received")] public double CashReceived { get; set; }
    [JsonProperty("cash_difference")] public double CashDifference { get; set; }
}
