using Newtonsoft.Json;

namespace Candela.Modules.Sales.Returns.Dtos;

/// <summary>Body of POST /api/returns/preview.</summary>
public sealed class ReturnPreviewRequest
{
    [JsonProperty("sale_id")]
    public int SaleId { get; set; }

    [JsonProperty("source_shop_id")]
    public int SourceShopId { get; set; }

    [JsonProperty("items")]
    public List<ReturnPreviewItem>? Items { get; set; }
}

/// <summary>One line of a return-preview request.</summary>
public sealed class ReturnPreviewItem
{
    [JsonProperty("product_item_id")]
    public int ProductItemId { get; set; }

    [JsonProperty("qty")]
    public int Qty { get; set; }
}
