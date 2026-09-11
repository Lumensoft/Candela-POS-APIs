using Newtonsoft.Json;

namespace Candela.Modules.Sales.Returns.Dtos;

/// <summary>
/// Body of POST /api/returns/validate. Names copied from the net48 ValidateReturnRequest.
/// source_shop_id is the shop that made the original sale — 0/omitted means the cashier's
/// own shop from the token.
/// </summary>
public sealed class ValidateReturnRequest
{
    [JsonProperty("invoice_no")]
    public int InvoiceNo { get; set; }

    [JsonProperty("source_shop_id")]
    public int SourceShopId { get; set; }
}
