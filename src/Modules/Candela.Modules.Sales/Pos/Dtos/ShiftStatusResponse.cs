using Newtonsoft.Json;

namespace Candela.Modules.Sales.Pos.Dtos;

/// <summary>
/// The cash breakdown returned by GET /api/pos/cash-status and GET /api/pos/shift-status.
///
/// One type for both because the net48 endpoints returned almost the same object — the
/// only difference is that shift-status also carried <c>net_sales</c> (a duplicate of
/// <c>cash_sales</c>). Here <see cref="NetSales"/> is left null for cash-status, and
/// NullValueHandling.Ignore drops the key, so each endpoint still serialises exactly the
/// field set it did before.
///
/// <c>available_cash = opening + cash_received + cash_sales - cash_skimmed</c>, and the
/// tablet caches this whole object in IndexedDB (cash_status), so the names and the
/// nullability are a contract.
/// </summary>
public sealed class ShiftStatusResponse
{
    [JsonProperty("success")]
    public bool Success { get; set; } = true;

    [JsonProperty("shift_open")]
    public bool ShiftOpen { get; set; }

    [JsonProperty("opening")]
    public double Opening { get; set; }

    [JsonProperty("cash_received")]
    public double CashReceived { get; set; }

    [JsonProperty("cash_sales")]
    public double CashSales { get; set; }

    /// <summary>Set only by shift-status; null (and omitted) for cash-status.</summary>
    [JsonProperty("net_sales")]
    public double? NetSales { get; set; }

    [JsonProperty("cash_skimmed")]
    public double CashSkimmed { get; set; }

    [JsonProperty("available_cash")]
    public double AvailableCash { get; set; }

    /// <summary>"yyyy-MM-dd HH:mm" while a shift is open, otherwise null.</summary>
    [JsonProperty("shift_since")]
    public string? ShiftSince { get; set; }

    [JsonProperty("pos_code")]
    public string PosCode { get; set; } = "";

    [JsonProperty("shop_id")]
    public int ShopId { get; set; }

    /// <summary>The last closed shift's summary — present only when no shift is open.</summary>
    [JsonProperty("last_closed")]
    public LastClosedShift? LastClosed { get; set; }

    /// <summary>The open shift's id, or null when none is open.</summary>
    [JsonProperty("pos_cash_management_id")]
    public int? PosCashManagementId { get; set; }
}

/// <summary>
/// The "last closed shift" summary embedded in <see cref="ShiftStatusResponse.LastClosed"/>.
/// Same four fields the net48 GetLastClosedShiftSummary returned.
/// </summary>
public sealed class LastClosedShift
{
    [JsonProperty("pos_cash_management_id")]
    public int PosCashManagementId { get; set; }

    [JsonProperty("closed_at")]
    public string ClosedAt { get; set; } = "";

    [JsonProperty("cash_counted")]
    public double CashCounted { get; set; }

    [JsonProperty("cash_difference")]
    public double CashDifference { get; set; }
}
