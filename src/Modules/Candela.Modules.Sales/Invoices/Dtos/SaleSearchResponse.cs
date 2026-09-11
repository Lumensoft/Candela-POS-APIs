using Newtonsoft.Json;

namespace Candela.Modules.Sales.Invoices.Dtos;

/// <summary>
/// GET /api/sales — one page of invoice summary rows for the search modal. <c>data</c>
/// stays a list of dictionaries: the SELECT's column aliases are the wire contract.
/// </summary>
public sealed class SaleSearchResponse
{
    [JsonProperty("success")] public bool Success { get; set; } = true;
    [JsonProperty("total")] public int Total { get; set; }
    [JsonProperty("page")] public int Page { get; set; }
    [JsonProperty("page_size")] public int PageSize { get; set; }
    [JsonProperty("count")] public int Count { get; set; }
    [JsonProperty("data")] public IReadOnlyList<Dictionary<string, object?>> Data { get; set; }
        = new List<Dictionary<string, object?>>();
}
