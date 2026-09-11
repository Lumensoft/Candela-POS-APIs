using Newtonsoft.Json;

namespace Candela.Modules.Sales.Invoices.Dtos;

/// <summary>
/// GET /api/sales/{id} — invoice header plus its line items, both as raw column rows
/// exactly as the net48 endpoint returned them (<c>{ success, data: header, items }</c>).
/// </summary>
public sealed class SaleDetailResponse
{
    [JsonProperty("success")] public bool Success { get; set; } = true;
    [JsonProperty("data")] public Dictionary<string, object?> Data { get; set; } = new();
    [JsonProperty("items")] public IReadOnlyList<Dictionary<string, object?>> Items { get; set; }
        = new List<Dictionary<string, object?>>();
}
