using Newtonsoft.Json;

namespace Candela.Modules.Sales.Pos.Dtos;

/// <summary>
/// GET /api/pos/shifts/{closingId}/detail — the individual Cash Received / Cash Skimmed
/// events that make up one shift closing's aggregate totals, split into two lists exactly
/// as the net48 endpoint returned them.
/// </summary>
public sealed class ShiftDetailResponse
{
    [JsonProperty("success")]
    public bool Success { get; set; } = true;

    [JsonProperty("closing_id")]
    public int ClosingId { get; set; }

    [JsonProperty("received")]
    public List<ShiftDetailEntry> Received { get; set; } = new();

    [JsonProperty("skimmed")]
    public List<ShiftDetailEntry> Skimmed { get; set; } = new();
}

/// <summary>One tblPOSCashManagementDetail row, shaped as the net48 endpoint shaped it.</summary>
public sealed class ShiftDetailEntry
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("amount")]
    public double Amount { get; set; }

    /// <summary>"yyyy-MM-dd HH:mm:ss".</summary>
    [JsonProperty("detail_date")]
    public string DetailDate { get; set; } = "";

    [JsonProperty("notes")]
    public string Notes { get; set; } = "";
}
