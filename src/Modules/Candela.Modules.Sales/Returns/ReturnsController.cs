using Candela.Modules.Sales.Returns.Dtos;
using Candela.Platform.Api;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Modules.Sales.Returns;

/// <summary>
/// Sale returns, ported from the net48 ReturnsController.
///
///   POST /api/returns/validate   is this invoice returnable, and what did it contain  -> SQL here
///   POST /api/returns/preview    re-price a proposed return cart for qty-threshold discounts -> SQL here
///   POST /api/returns            process the return                                   -> legacy DAL, verbatim
///
/// validate's two SaleAndReturnDAL calls are single-value reads with no side effects, so
/// they are inlined here (the same treatment QuoteController gave a dozen DAL helpers).
/// PostReturn is not split: it runs SaleAndReturnDAL.Add under a per-invoice lock and has
/// an idempotent-replay recovery path, so the whole method stays in the legacy host and
/// this endpoint forwards the request and relays the reply unchanged.
/// </summary>
[Route("api/returns")]
public sealed class ReturnsController(IReturnsService returns) : CandelaControllerBase
{
    /// <summary>POST /api/returns/validate — invoice_no (and optional source_shop_id).</summary>
    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] ValidateReturnRequest? req, CancellationToken ct)
    {
        if (req == null || req.InvoiceNo <= 0)
            return Fail(StatusCodes.Status400BadRequest, "invoice_no is required");

        int sourceShopId = req.SourceShopId > 0 ? req.SourceShopId : ShopId;

        var result = await returns.ValidateAsync(req.InvoiceNo, ShopId, sourceShopId, ct);
        return new JsonResult(result);
    }

    /// <summary>POST /api/returns/preview — sale_id + items; returns the re-priced item rows.</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] ReturnPreviewRequest? req, CancellationToken ct)
    {
        if (req == null || req.SaleId <= 0 || req.Items == null || req.Items.Count == 0)
            return Fail(StatusCodes.Status400BadRequest, "sale_id and items are required");

        int sourceShopId = req.SourceShopId > 0 ? req.SourceShopId : ShopId;

        var items = await returns.PreviewAsync(req, ShopId, sourceShopId, ct);
        return new JsonResult(new { success = true, items });
    }

    /// <summary>
    /// GET /api/returns/{id} — a saved return (header, lines, and the original invoice it belongs to) for the return
    /// screen's edit mode. 404 when it is not this shop's.
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetReturnForEdit(int id, CancellationToken ct)
    {
        var result = await returns.GetReturnForEditAsync(id, ShopId, ct);
        if (result is null)
            return Fail(StatusCodes.Status404NotFound, "Return not found");

        return new JsonResult(result);
    }

    /// <summary>
    /// PUT /api/returns/{id} — update a saved return. Forwarded to /legacy/returns/{id} as-is (the link to the original
    /// invoice is read from the saved return there, never taken from the request); its answer is passed straight back.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateReturn(int id, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(ct);

        var reply = await returns.ForwardUpdateReturnAsync(id, rawBody, ct);

        return new ContentResult
        {
            StatusCode = reply.StatusCode,
            Content = reply.Body,
            ContentType = reply.ContentType,
        };
    }

    /// <summary>
    /// POST /api/returns — the request body is forwarded to /legacy/returns as-is, and
    /// its answer (200 success envelope, 409 duplicate, 422 business rule, 500 DAL
    /// failure) is passed straight back with the same status and body.
    /// </summary>
    [HttpPost("")]
    public async Task<IActionResult> PostReturn(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(ct);

        var reply = await returns.ForwardPostReturnAsync(rawBody, ct);

        return new ContentResult
        {
            StatusCode = reply.StatusCode,
            Content = reply.Body,
            ContentType = reply.ContentType,
        };
    }
}
