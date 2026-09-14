using Candela.Modules.Configuration.Products.Dtos;
using Candela.Shared.Exceptions;

namespace Candela.Modules.Configuration.Products;

/// <summary>
/// Ported from frmDefProduct.vb's IsValidate (EnumTabPages.TabPgProduct branch) — see
/// that function for the source of every rule below. The mandatory-field checks are
/// data-driven (tblDefScreenCustomization) rather than hardcoded, exactly like the
/// desktop, so a shop that has configured Category as mandatory here gets the same
/// refusal on the web that it gets on the till floor.
/// </summary>
public sealed class ProductService(IProductRepository products, IProductLookupsRepository lookups) : IProductService
{
    public Task<IReadOnlyList<ProductListItemResponse>> ListAsync(CancellationToken ct) => products.GetAllAsync(ct);

    public async Task<ProductResponse> GetAsync(int productId, CancellationToken ct)
        => await products.GetByIdAsync(productId, ct)
           ?? throw new NotFoundException($"Product {productId} was not found.");

    public Task<ProductLookupsResponse> GetLookupsAsync(CancellationToken ct) => lookups.GetGlobalLookupsAsync(ct);

    public Task<ProductLineItemLookupsResponse> GetLineItemLookupsAsync(int lineItemId, CancellationToken ct)
        => lookups.GetLineItemLookupsAsync(lineItemId, ct);

    public Task<ProductSubCategoryLookupResponse> GetSubCategoriesAsync(int categoryId, CancellationToken ct)
        => lookups.GetSubCategoriesAsync(categoryId, ct);

    public async Task<ProductResponse> CreateAsync(ProductRequest request, int userId, CancellationToken ct)
    {
        await ValidateAsync(request, isNew: true, editingProductId: null, ct);

        var (sizeId, combinationId) = await GetDefaultSizeAndCombinationOrThrowAsync(request.LineItemId, ct);
        var productId = await products.CreateAsync(request, sizeId, combinationId, userId, ct);

        return await GetAsync(productId, ct);
    }

    public async Task<ProductResponse> UpdateAsync(int productId, ProductRequest request, int userId,
        CancellationToken ct)
    {
        _ = await products.GetByIdAsync(productId, ct)
            ?? throw new NotFoundException($"Product {productId} was not found.");

        await ValidateAsync(request, isNew: false, editingProductId: productId, ct);

        var (sizeId, combinationId) = await GetDefaultSizeAndCombinationOrThrowAsync(request.LineItemId, ct);
        await products.UpdateAsync(productId, request, sizeId, combinationId, userId, ct);

        return await GetAsync(productId, ct);
    }

    public async Task DeleteAsync(int productId, int userId, CancellationToken ct)
    {
        _ = await products.GetByIdAsync(productId, ct)
            ?? throw new NotFoundException($"Product {productId} was not found.");

        // Whether it may actually go is ProductDAL.IsValidateForDelete's call, run by the
        // legacy host before the DELETE — it checks sales, GRNs, opening stock and
        // assemblies. That refusal comes back as a 422, same as Cities.
        await products.DeleteAsync(productId, userId, ct);
    }

    /// <summary>
    /// frmDefProduct.vb:3163-3617 — every ddlXxx.Visible check collapses here to "is it
    /// mandatory for this install", since the web form always shows every field (nothing
    /// is Visible=False the way the desktop conditionally hides controls per Line Item
    /// category). A field left at its zero value is only an error when the config table
    /// says this install requires it.
    /// </summary>
    private async Task ValidateAsync(ProductRequest r, bool isNew, int? editingProductId, CancellationToken ct)
    {
        // Product code: letters/digits only (frmDefProduct.vb:3123-3132, CR#2677).
        var code = r.ProductCode.Trim();
        if (code.Length == 0)
            throw new ValidationException("Product code is required.");
        foreach (var c in code)
        {
            if (!char.IsLetterOrDigit(c))
                throw new ValidationException("Product code may only contain letters and digits.");
        }

        if (r.ItemName.Trim().Length == 0)
            throw new ValidationException("Product name is required.");

        if (isNew && await products.ProductCodeExistsAsync(code, null, ct))
            throw new ValidationException($"Product code \"{code}\" already exists.");
        if (!isNew && await products.ProductCodeExistsAsync(code, editingProductId, ct))
            throw new ValidationException($"Product code \"{code}\" already exists.");

        var mandatory = await products.GetMandatoryFieldsAsync(ct);

        RequireIfMandatory(mandatory, "ddlcategory", r.CategoryId == 0, "Category is required.");
        RequireIfMandatory(mandatory, "ddlsubcategory", r.SubCategoryId == 0, "Sub category is required.");
        RequireIfMandatory(mandatory, "ddlproductgroup", r.ProductGroupId == 0, "Product group is required.");
        RequireIfMandatory(mandatory, "ddlproductvariable1", r.Variable1Id == 0, "Product Variable 1 is required.");
        RequireIfMandatory(mandatory, "ddlproductvariable2", r.Variable2Id == 0, "Product Variable 2 is required.");
        RequireIfMandatory(mandatory, "ddlcalseason", r.CalendarSeasonId == 0, "Calendar season is required.");
        RequireIfMandatory(mandatory, "ddlproductvariable3", r.Variable3Id == 0, "Product Variable 3 is required.");
        RequireIfMandatory(mandatory, "ddlproductvariable4", r.Variable4Id == 0, "Product Variable 4 is required.");
        RequireIfMandatory(mandatory, "ddlproductvariable5", r.Variable5Id == 0, "Product Variable 5 is required.");
        RequireIfMandatory(mandatory, "ddlsupplier", r.SupplierId == 0, "Supplier is required.");
        RequireIfMandatory(mandatory, "ddlacquiretype", r.AcquireType == 0, "Acquire type is required.");
        RequireIfMandatory(mandatory, "ddlpurchasetype", r.PurchaseType == 0, "Purchase type is required.");
        RequireIfMandatory(mandatory, "ddlmanufacture", r.ManufactureType == 0, "Manufacturer is required.");
        RequireIfMandatory(mandatory, "ddlpurconunit", r.PurConUnit == 0, "Purchase unit is required.");
        RequireIfMandatory(mandatory, "txtpurconfactor", r.PurConFactor == 0, "Purchase conversion factor is required.");
        RequireIfMandatory(mandatory, "cmbcurrency", r.CurrencyId == 0, "Currency is required.");
        RequireIfMandatory(mandatory, "txtvendorcode", string.IsNullOrWhiteSpace(r.VendorCode), "Vendor code is required.");
        RequireIfMandatory(mandatory, "txttechnicaldetail", string.IsNullOrWhiteSpace(r.TechnicalDetails), "Technical details are required.");

        // frmDefProduct.vb:3462 — price is only mandatory in New mode when neither a
        // retail price nor the "user enters price at sale" flag is set.
        if (isNew && r.Price == 0 && !r.UserPrice)
            RequireIfMandatory(mandatory, "txtprice", true, "Price is required.");

        // frmDefProduct.vb:3725-3729 — floating price needs a currency chosen, regardless
        // of whether Currency is separately configured mandatory.
        if (r.FloatingPrice && r.CurrencyId == 0)
            throw new ValidationException("Select a currency for a floating-price product.");
    }

    private static void RequireIfMandatory(IReadOnlyDictionary<string, bool> mandatory, string controlName,
        bool isMissing, string message)
    {
        if (isMissing && mandatory.TryGetValue(controlName, out var required) && required)
            throw new ValidationException(message);
    }

    private async Task<(int SizeId, int CombinationId)> GetDefaultSizeAndCombinationOrThrowAsync(int lineItemId,
        CancellationToken ct)
    {
        var pair = await products.GetDefaultSizeAndCombinationAsync(lineItemId, ct);
        if (pair is null)
            throw new ValidationException(
                "This line item has no default size/colour configured yet — set one up in Sizes and Combinations before adding products to it.");
        return pair.Value;
    }
}
