using Candela.Modules.Configuration.Products.Dtos;
using Candela.Platform.Api;
using Candela.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Modules.Configuration.Products;

/// <summary>
/// Product Definition (frmDefProduct.vb) — the web equivalent, built tab by tab since the
/// desktop form is the largest single screen in Candela (11.7k lines, 9 tabs, ~75 DAL
/// methods). This slice is the Core Product tab (EnumTabPages.TabPgProduct), non-assortment
/// mode — see ProductRequest's doc comment for what that means and what's still to come
/// (Sizes/Colours, Price history, Assembly, Promotion, Alternate Barcode, Opening Stock,
/// Additional Attributes).
///
/// Routed under api/configuration/products, not api/products — that path already belongs
/// to the POS tablet's read-only lookup slice (Candela.Modules.Sales.Products), which
/// this must never collide with or touch.
///
/// Reads are served from SQL here. Writes are handed to the legacy host so ProductDAL can
/// do them — same split as CitiesController, see its doc comment.
/// </summary>
[Route("api/configuration/products")]
public sealed class ProductsController(IProductService products) : CandelaControllerBase
{
    /// <summary>GET api/configuration/products — the All Products grid.</summary>
    [HttpGet("")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await products.ListAsync(ct);
        return new JsonResult(new { success = true, count = rows.Count, data = rows });
    }

    /// <summary>GET api/configuration/products/{id}</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
        => new JsonResult(ApiResponse<ProductResponse>.Ok(await products.GetAsync(id, ct)));

    /// <summary>POST api/configuration/products</summary>
    [HttpPost("")]
    public async Task<IActionResult> Create([FromBody] ProductRequest request, CancellationToken ct)
        => new JsonResult(ApiResponse<ProductResponse>.Ok(await products.CreateAsync(request, UserId, ct)))
        {
            StatusCode = StatusCodes.Status201Created
        };

    /// <summary>PUT api/configuration/products/{id}</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ProductRequest request, CancellationToken ct)
        => new JsonResult(ApiResponse<ProductResponse>.Ok(await products.UpdateAsync(id, request, UserId, ct)));

    /// <summary>
    /// DELETE api/configuration/products/{id}
    /// Refused with 422 while sales, GRNs, opening stock or an assembly still reference it.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await products.DeleteAsync(id, UserId, ct);
        return new JsonResult(new { success = true, deleted = id });
    }

    /// <summary>
    /// GET api/configuration/products/lookups — everything that does not depend on which
    /// Line Item is selected. Fetch once when the form opens.
    /// </summary>
    [HttpGet("lookups")]
    public async Task<IActionResult> GetLookups(CancellationToken ct)
        => new JsonResult(ApiResponse<ProductLookupsResponse>.Ok(await products.GetLookupsAsync(ct)));

    /// <summary>
    /// GET api/configuration/products/lookups/by-line-item/{lineItemId} — Category,
    /// Product Group, Variable1, Variable2. Re-fetch every time Line Item changes.
    /// </summary>
    [HttpGet("lookups/by-line-item/{lineItemId:int}")]
    public async Task<IActionResult> GetLineItemLookups(int lineItemId, CancellationToken ct)
        => new JsonResult(ApiResponse<ProductLineItemLookupsResponse>.Ok(
            await products.GetLineItemLookupsAsync(lineItemId, ct)));

    /// <summary>
    /// GET api/configuration/products/lookups/by-category/{categoryId} — SubCategory.
    /// Re-fetch every time Category changes; empty out this field when it does, exactly
    /// like the desktop does when Line Item changes underneath Category.
    /// </summary>
    [HttpGet("lookups/by-category/{categoryId:int}")]
    public async Task<IActionResult> GetSubCategories(int categoryId, CancellationToken ct)
        => new JsonResult(ApiResponse<ProductSubCategoryLookupResponse>.Ok(
            await products.GetSubCategoriesAsync(categoryId, ct)));
}
