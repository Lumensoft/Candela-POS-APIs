using Candela.Platform.Api;
using Candela.Platform.Legacy;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Modules.Sales.Invoices;

/// <summary>
/// Sale invoices, ported from the net48 SalesController.
///
///   GET    /api/sales               paginated invoice search        -> SQL here
///   GET    /api/sales/{id}          invoice header + line items      -> SQL here
///   POST   /api/sales               finalise a sale                 -> legacy DAL, verbatim
///   PUT    /api/sales/{id}          update a sale                   -> legacy DAL, verbatim
///   DELETE /api/sales/{id}          soft-void a sale                -> legacy DAL, verbatim
///   DELETE /api/sales/{id}/hard     hard-delete a sale              -> legacy DAL, verbatim
///
/// The writes all run SaleAndReturnDAL under a long chain of validations the desktop
/// applies (idempotency, credit check, coupon status, below-cost, adjustment limit,
/// FonePay dedupe) and an idempotent-replay recovery path — none of that is split, so the
/// whole of each write endpoint stays in the legacy host and this controller forwards the
/// request and relays the reply unchanged (200 success envelope, 409 conflict, 422
/// business rule, 500 DAL failure).
///
/// <c>POST /api/sales/quote</c> is a different route (QuoteController) and is not served
/// here; it falls through to the proxy until the pricing engine is migrated.
/// </summary>
[Route("api/sales")]
public sealed class SalesController(ISalesService sales) : CandelaControllerBase
{
    /// <summary>GET /api/sales?page=&amp;page_size=&amp;q=&amp;from=&amp;to=&amp;invoice_no=</summary>
    [HttpGet("")]
    public async Task<IActionResult> GetSales(
        [FromQuery] int page = 1,
        [FromQuery] int page_size = 20,
        [FromQuery] string? q = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string? invoice_no = null,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (page_size < 1 || page_size > 100) page_size = 20;

        var result = await sales.SearchAsync(ShopId, page, page_size, q, from, to, invoice_no, ct);
        return new JsonResult(result);
    }

    /// <summary>GET /api/sales/{id} — 404 when the sale is not this shop's.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetSale(int id, CancellationToken ct)
    {
        var result = await sales.GetSaleAsync(id, ShopId, ct);
        if (result is null)
            return Fail(StatusCodes.Status404NotFound, "Sale not found");

        return new JsonResult(result);
    }

    /// <summary>POST /api/sales — the request body is forwarded to /legacy/sales as-is.</summary>
    [HttpPost("")]
    public async Task<IActionResult> PostSale(CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct);
        var reply = await sales.ForwardPostAsync(body, ct);
        return Relay(reply);
    }

    /// <summary>PUT /api/sales/{id} — forwarded to PUT /legacy/sales/{id}.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateSale(int id, CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct);
        var reply = await sales.ForwardUpdateAsync(id, body, ct);
        return Relay(reply);
    }

    /// <summary>DELETE /api/sales/{id} — soft void; forwarded to DELETE /legacy/sales/{id}.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> VoidSale(int id, CancellationToken ct)
    {
        var reply = await sales.ForwardVoidAsync(id, ct);
        return Relay(reply);
    }

    /// <summary>DELETE /api/sales/{id}/hard — forwarded to DELETE /legacy/sales/{id}/hard.</summary>
    [HttpDelete("{id:int}/hard")]
    public async Task<IActionResult> HardDeleteSale(int id, CancellationToken ct)
    {
        var reply = await sales.ForwardHardDeleteAsync(id, ct);
        return Relay(reply);
    }

    private async Task<string> ReadBodyAsync(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        return await reader.ReadToEndAsync(ct);
    }

    private static ContentResult Relay(LegacyRawResponse reply) => new()
    {
        StatusCode = reply.StatusCode,
        Content = reply.Body,
        ContentType = reply.ContentType,
    };
}
