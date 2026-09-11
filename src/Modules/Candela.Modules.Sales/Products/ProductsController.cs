using Candela.Platform.Api;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Modules.Sales.Products;

/// <summary>
/// Ported from the .NET Framework host. This slice never used the Candela DAL — it is
/// plain queries — so it moves whole, with no call back into the legacy host. Routes,
/// envelope and column names are unchanged, so the tablet cannot tell which process
/// answered.
///
/// No try/catch and no service class here:
///   - failures are thrown and answered by ExceptionHandlingMiddleware
///   - there are no business rules to hold, only a lookup, so a service would be
///     ceremony. A slice earns a service when it has rules worth naming.
/// </summary>
[Route("api/products")]
public sealed class ProductsController(IProductRepository products) : CandelaControllerBase
{
    /// <summary>
    /// GET api/products/{id}/alternates — substitute items, auto-opened on scan when the
    /// scanned product has alternatives configured (tblDefProductAlternates).
    /// </summary>
    [HttpGet("{id:int}/alternates")]
    public async Task<IActionResult> GetAlternates(int id, CancellationToken ct)
        => Rows(await products.GetAlternatesAsync(id, ShopId, ct));

    /// <summary>
    /// GET api/products/{id}/batches — batches and expiry dates for the Batch modal, so
    /// the cashier can pick a specific batch.
    /// </summary>
    [HttpGet("{id:int}/batches")]
    public async Task<IActionResult> GetBatches(int id, CancellationToken ct)
        => Rows(await products.GetBatchesAsync(id, ShopId, ct));
}
