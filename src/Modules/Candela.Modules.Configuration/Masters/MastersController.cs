using Candela.Platform.Api;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Modules.Configuration.Masters;

/// <summary>
/// Master data for the tablet, ported from the net48 MastersController.
///
/// Nineteen GETs, no writes, every one answering with the same envelope:
/// <c>{ success: true, count: n, data: [ … ] }</c> — which is what
/// CandelaControllerBase.Rows() produces, so the old controller's private Ok() helper is
/// gone without the responses changing.
///
/// The try/catch that wrapped every action is gone too: an unexpected fault reaches
/// ExceptionHandlingMiddleware, which logs it and writes the same generic 500 the old
/// Err() helper did. Only the deliberate 400s are still written here.
///
/// Query parameter names are kept exactly as they were, product_item_id included — it is
/// what the tablet sends, so it is what the parameter has to be called.
/// </summary>
[Route("api/masters")]
public sealed class MastersController(IMastersRepository masters) : CandelaControllerBase
{
    /// <summary>GET /api/masters/products[?since=…] — catalogue, full or delta.</summary>
    [HttpGet("products")]
    public async Task<IActionResult> GetProducts([FromQuery] string? since, CancellationToken ct) =>
        Rows(await masters.GetProductsAsync(ShopId, ParseSince(since), ct));

    /// <summary>
    /// GET /api/masters/products/scan?q=X — the scan bar. Matches product_code first, then
    /// CustomerSKUCode 1-5, and always returns at most one row.
    /// </summary>
    [HttpGet("products/scan")]
    public async Task<IActionResult> ScanProduct([FromQuery] string? q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Fail(StatusCodes.Status400BadRequest, "q is required");

        return Rows(await masters.ScanProductAsync(ShopId, q.Trim(), ct));
    }

    /// <summary>
    /// GET /api/masters/products/search?barcode=X or ?code=X — kept for backward
    /// compatibility; the scan bar uses /products/scan now.
    /// </summary>
    [HttpGet("products/search")]
    public async Task<IActionResult> SearchProduct([FromQuery] string? barcode,
        [FromQuery] string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(barcode) && string.IsNullOrWhiteSpace(code))
            return Fail(StatusCodes.Status400BadRequest, "barcode or code is required");

        return Rows(await masters.SearchProductAsync(ShopId, barcode, code, ct));
    }

    /// <summary>
    /// GET /api/masters/customers — full list, ?since= for delta sync, ?q= for the live
    /// search the app falls back to when IndexedDB misses.
    /// </summary>
    [HttpGet("customers")]
    public async Task<IActionResult> GetCustomers([FromQuery] string? since,
        [FromQuery] string? q, CancellationToken ct) =>
        Rows(await masters.GetCustomersAsync(ShopId, ParseSince(since), q, ct));

    /// <summary>GET /api/masters/employees[?since=…] — salespeople for this shop.</summary>
    [HttpGet("employees")]
    public async Task<IActionResult> GetEmployees([FromQuery] string? since, CancellationToken ct) =>
        Rows(await masters.GetEmployeesAsync(ShopId, ParseSince(since), ct));

    /// <summary>GET /api/masters/credit-cards[?since=…] — card types this shop accepts.</summary>
    [HttpGet("credit-cards")]
    public async Task<IActionResult> GetCreditCards([FromQuery] string? since, CancellationToken ct) =>
        Rows(await masters.GetCreditCardsAsync(ShopId, ParseSince(since), ct));

    /// <summary>GET /api/masters/member-types[?since=…] — customer tiers.</summary>
    [HttpGet("member-types")]
    public async Task<IActionResult> GetMemberTypes([FromQuery] string? since, CancellationToken ct) =>
        Rows(await masters.GetMemberTypesAsync(ParseSince(since), ct));

    /// <summary>GET /api/masters/customer-groups — the Add Customer group dropdown.</summary>
    [HttpGet("customer-groups")]
    public async Task<IActionResult> GetCustomerGroups(CancellationToken ct) =>
        Rows(await masters.GetCustomerGroupsAsync(ct));

    /// <summary>
    /// GET /api/masters/payment-methods — mobile payment providers and their flags. The
    /// app hides the Mobile tab when the list comes back empty.
    /// </summary>
    [HttpGet("payment-methods")]
    public async Task<IActionResult> GetPaymentMethods(CancellationToken ct) =>
        Rows(await masters.GetPaymentMethodsAsync(ct));

    /// <summary>
    /// GET /api/masters/config[?since=…] — shop and global config as key/value rows.
    /// since= is accepted and ignored: config has no reliable timestamp, and the tables
    /// are small enough to return whole. That was true of the net48 endpoint too.
    /// </summary>
    [HttpGet("config")]
    public async Task<IActionResult> GetConfig([FromQuery] string? since, CancellationToken ct) =>
        Rows(await masters.GetConfigAsync(ShopId, ct));

    /// <summary>
    /// GET /api/masters/line-items[?since=…] — category tabs. since= is accepted for
    /// consistency; the table has no timestamp so the full set always comes back.
    /// </summary>
    [HttpGet("line-items")]
    public async Task<IActionResult> GetLineItems([FromQuery] string? since, CancellationToken ct) =>
        Rows(await masters.GetLineItemsAsync(ct));

    /// <summary>GET /api/masters/batches?product_item_id=123 — batches with stock, FEFO order.</summary>
    [HttpGet("batches")]
    public async Task<IActionResult> GetBatches([FromQuery] int product_item_id, CancellationToken ct)
    {
        if (product_item_id <= 0)
            return Fail(StatusCodes.Status400BadRequest, "product_item_id is required");

        return Rows(await masters.GetBatchesAsync(product_item_id, ct));
    }

    /// <summary>GET /api/masters/assembly-items?product_item_id=123 — an assembly's components.</summary>
    [HttpGet("assembly-items")]
    public async Task<IActionResult> GetAssemblyItems([FromQuery] int product_item_id,
        CancellationToken ct)
    {
        if (product_item_id <= 0)
            return Fail(StatusCodes.Status400BadRequest, "product_item_id is required");

        return Rows(await masters.GetAssemblyItemsAsync(product_item_id, ShopId, ct));
    }

    /// <summary>GET /api/masters/blocked-products — products this shop may not sell.</summary>
    [HttpGet("blocked-products")]
    public async Task<IActionResult> GetBlockedProducts(CancellationToken ct) =>
        Rows(await masters.GetBlockedProductsAsync(ShopId, ct));

    /// <summary>
    /// GET /api/masters/promotion-products — currently-running cross-sell promotions.
    /// Global, not shop-scoped, matching frmSaleAndReturn's showAlert query.
    /// </summary>
    [HttpGet("promotion-products")]
    public async Task<IActionResult> GetPromotionProducts(CancellationToken ct) =>
        Rows(await masters.GetPromotionProductsAsync(ct));

    /// <summary>GET /api/masters/str/{strNo}/products — products on a Stock Transfer Request.</summary>
    [HttpGet("str/{strNo}/products")]
    public async Task<IActionResult> GetStrProducts(string strNo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(strNo))
            return Fail(StatusCodes.Status400BadRequest, "strNo is required");

        return Rows(await masters.GetStrProductsAsync(strNo, ShopId, ct));
    }

    /// <summary>GET /api/masters/shops — every shop, for the cross-shop return modal.</summary>
    [HttpGet("shops")]
    public async Task<IActionResult> GetShops(CancellationToken ct) =>
        Rows(await masters.GetShopsAsync(ct));

    /// <summary>GET /api/masters/departments — this shop's departments.</summary>
    [HttpGet("departments")]
    public async Task<IActionResult> GetDepartments(CancellationToken ct) =>
        Rows(await masters.GetDepartmentsAsync(ShopId, ct));

    /// <summary>GET /api/masters/adjustment-reasons — global; shown when an adjustment is entered.</summary>
    [HttpGet("adjustment-reasons")]
    public async Task<IActionResult> GetAdjustmentReasons(CancellationToken ct) =>
        Rows(await masters.GetAdjustmentReasonsAsync(ct));

    /// <summary>GET /api/masters/return-reasons — global; required per line when enforced.</summary>
    [HttpGet("return-reasons")]
    public async Task<IActionResult> GetReturnReasons(CancellationToken ct) =>
        Rows(await masters.GetReturnReasonsAsync(ct));

    /// <summary>
    /// An unparseable ?since= means "no delta", not an error — the original used
    /// DateTime.TryParse and fell through to the full set. Kept, so a client sending a
    /// malformed timestamp still gets data instead of a 400 it has never handled.
    /// </summary>
    private static DateTime? ParseSince(string? since)
    {
        if (!string.IsNullOrEmpty(since) && DateTime.TryParse(since, out DateTime d))
            return d;
        return null;
    }
}
