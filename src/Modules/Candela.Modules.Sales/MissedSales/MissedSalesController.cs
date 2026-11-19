using Candela.Platform.Api;
using Candela.Platform.Legacy;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace Candela.Modules.Sales.MissedSales;

/// <summary>
/// POST /api/missed-sales — record a missed sale (EnableMissedSaleRecording). The insert and its SQL-log row go
/// through the legacy host, so replication to head office stays correct. The shop and user come from the token (the
/// legacy host reads them off the X-Ctx-* headers), never from the body.
/// </summary>
[Route("api/missed-sales")]
public sealed class MissedSalesController(ILegacyHostClient legacy) : CandelaControllerBase
{
    [HttpPost("")]
    public async Task<IActionResult> Record([FromBody] MissedSaleRequest? req, CancellationToken ct)
    {
        if (req is null || req.ProductItemId <= 0)
            return Fail(StatusCodes.Status400BadRequest, "product_item_id is required");
        if (req.MissedQty <= 0)
            return Fail(StatusCodes.Status400BadRequest, "missed_qty must be greater than zero");
        if ((req.Remarks ?? "").Length > 150)
            return Fail(StatusCodes.Status400BadRequest, "Maximum length of remarks exceeded.");

        var result = await legacy.PostAsync<MissedSaleResponse>("missed-sales", req, ct);
        return new JsonResult(new { success = true, missed_sale_id = result.MissedSaleId });
    }
}

public sealed class MissedSaleRequest
{
    [JsonProperty("product_item_id")] public int ProductItemId { get; set; }
    [JsonProperty("missed_qty")] public double MissedQty { get; set; }
    [JsonProperty("remarks")] public string? Remarks { get; set; }
}

public sealed class MissedSaleResponse
{
    [JsonProperty("missed_sale_id")] public int MissedSaleId { get; set; }
}
