using Candela.Modules.Configuration.Products.Dtos;
using Candela.Platform.Data;
using Candela.Platform.Legacy;

namespace Candela.Modules.Configuration.Products;

/// <summary>
/// Reads go straight to SQL through <see cref="IDb"/>; writes go through the legacy host
/// (ProductsLegacyController in Candela.LegacyHost), which calls the real ProductDAL so
/// the SQL log, activity log and SKU creation stay exactly what the desktop produces —
/// same split as CityRepository, see its doc comment.
///
/// Column names are taken from ProductDAL.GetAll's own SELECT (DAL/Configuration/
/// ProductDAL.vb) — that query has been rewritten under a dozen change requests over the
/// years and only the newest, uncommented version is live, but every prior version uses
/// the same column names, so they were a reliable source even before finding it.
/// </summary>
public sealed class ProductRepository(IDb db, ILegacyHostClient legacy) : IProductRepository
{
    public async Task<IReadOnlyList<ProductListItemResponse>> GetAllAsync(CancellationToken ct)
    {
        // Price is NOT a column on tblDefProducts — confirmed against ProductDAL's own
        // INSERT (it isn't in that column list at all). It lives one hop away: a product
        // has one or more tblProductItem SKU rows, each with its own current price in
        // tblDefProductPrice (the row with end_date IS NULL). OUTER APPLY with TOP 1
        // picks one representative price even for a product that already has multiple
        // SKUs from the desktop (sizes/colours) — this list only shows one number, it
        // doesn't need every price.
        const string sql = @"
SELECT
    p.product_id                    AS ProductId,
    p.Product_code                  AS ProductCode,
    p.item_name                     AS ItemName,
    isnull(li.field_name, '')       AS LineItemName,
    cat.field_name                  AS CategoryName,
    sup.field_name                  AS SupplierName,
    isnull(price.product_price, 0)  AS Price,
    CASE WHEN p.status = 1 THEN 1 ELSE 0 END AS Active
FROM tblDefProducts p
LEFT JOIN tblDefLineItems li ON li.line_item_id = p.line_item_id
LEFT JOIN tblDefCategory cat ON cat.category_id = p.category_id
LEFT JOIN tblDefSuppliers sup ON sup.supplier_id = p.supplier_id
OUTER APPLY (
    SELECT TOP 1 pp.product_price
    FROM tblProductItem pi
    INNER JOIN tblDefProductPrice pp
        ON pp.product_item_id = pi.Product_Item_ID AND pp.end_date IS NULL
    WHERE pi.Product_ID = p.product_id
    ORDER BY pi.Product_Item_ID
) price
ORDER BY p.item_name";

        return await db.QueryAsync<ProductListItemResponse>(sql, ct: ct);
    }

    public Task<ProductResponse?> GetByIdAsync(int productId, CancellationToken ct)
    {
        // Column names verified against ProductDAL's own live INSERT INTO tblDefProducts
        // (DAL/Configuration/ProductDAL.vb) after an initial version of this query used
        // three that don't exist there: RFIDProduct (not RFIDEnabled), Allow_Price_Change
        // (not AllowPriceChange) and NotForDiscount (not ItemNotForDiscount). Price,
        // WholeSalePrice and Comments are not columns on this table at all — Price and
        // WholeSalePrice live on tblProductItem/tblDefProductPrice (see GetAllAsync's
        // comment), and Comments has no home here that the desktop's Core Product tab
        // ever writes to, so it is left unmapped (ProductResponse.Comments stays null)
        // rather than selected from a column that would 500 the request.
        const string sql = @"
SELECT
    p.product_id             AS ProductId,
    p.line_item_id            AS LineItemId,
    p.Product_code             AS ProductCode,
    p.item_name                AS ItemName,
    isnull(p.category_id, 0)             AS CategoryId,
    isnull(p.subcategory_id, 0)          AS SubCategoryId,
    isnull(p.Product_group_id, 0)        AS ProductGroupId,
    isnull(p.Age_groups_id, 0)           AS Variable1Id,
    isnull(p.Packaging_code_id, 0)       AS Variable2Id,
    isnull(p.product_life_type, 0)       AS Variable3Id,
    isnull(p.Gender, 0)                  AS Variable4Id,
    isnull(p.Value_addition_by_ID, 0)    AS Variable5Id,
    isnull(p.calendar_season_id, 0)      AS CalendarSeasonId,
    isnull(p.supplier_id, 0)             AS SupplierId,
    isnull(p.Acquire_type, 0)            AS AcquireType,
    isnull(p.Purchase_type, 0)           AS PurchaseType,
    isnull(p.Manufacture_type, 0)        AS ManufactureType,
    isnull(p.Pur_Con_Unit, 0)            AS PurConUnit,
    isnull(p.Pur_Con_factor, 0)          AS PurConFactor,
    isnull(p.SalesTaxCodeID, 0)          AS SaleTaxCodeId,
    isnull(p.Sale_Tax, 0)                AS SaleTax,
    isnull(p.vat, 0)                     AS Vat,
    -- vat_type is nvarchar, and older rows still hold the pre-CR#1712 text (Percentage /
    -- Value) rather than the 0 / 1 every save writes now (frmDefProduct.vb:4317's own
    -- comment names this exact migration) -- comparing it to the int literal 1 crashes on
    -- any row still holding the old text, so both spellings of true are matched here.
    CASE WHEN p.vat_type = '1' OR p.vat_type = 'Value' THEN 1 ELSE 0 END AS VatIsValue,
    CASE WHEN p.Tax_At_Retail_Price = 1 THEN 1 ELSE 0 END AS TaxAtRetailPrice,
    isnull(price.product_price, 0)       AS Price,
    isnull(sku.WholeSalePrice, 0)        AS WholeSalePrice,
    isnull(p.Average_cost, 0)            AS AverageCost,
    CASE WHEN p.IsUserDefine = 1 THEN 1 ELSE 0 END    AS UserPrice,
    isnull(p.FloatingPrice, 0)           AS FloatingPrice,
    isnull(p.CurrencyID, 0)              AS CurrencyId,
    CASE WHEN p.status = 1 THEN 1 ELSE 0 END          AS Active,
    CASE WHEN p.IsDefault = 1 THEN 1 ELSE 0 END       AS IsDefault,
    CASE WHEN p.Barcode_Print = 1 THEN 1 ELSE 0 END   AS NoBarcodePrint,
    CASE WHEN isnull(p.RFIDProduct, 0) = 1 THEN 1 ELSE 0 END      AS RfidEnabled,
    CASE WHEN isnull(p.Allow_Below_Cost, 0) = 1 THEN 1 ELSE 0 END AS AllowBelowCost,
    CASE WHEN isnull(p.Allow_Price_Change, 0) = 1 THEN 1 ELSE 0 END AS AllowPriceChange,
    CASE WHEN isnull(p.isWebItem, 0) = 1 THEN 1 ELSE 0 END        AS IsWebItem,
    CASE WHEN isnull(p.NotForDiscount, 0) = 1 THEN 1 ELSE 0 END   AS ItemNotForDiscount,
    CASE WHEN p.Basic_designed = 1 THEN 1 ELSE 0 END  AS IsBasicType,
    isnull(p.number_of_pieces, 0)        AS NumberOfPieces,
    p.HSCode                             AS HsCode,
    p.Other_Code                         AS VendorCode,
    p.Technical_details                  AS TechnicalDetails,
    p.InternalComments                   AS InternalComments,
    EnteredUser.user_name                AS EnteredBy,
    p.EnteredDate                        AS EnteredDate,
    EditedUser.user_name                 AS EditedBy,
    p.EditedDate                         AS EditedDate
FROM tblDefProducts p
LEFT JOIN TblSecurityUser EnteredUser ON EnteredUser.user_id = p.EnteredBy
LEFT JOIN TblSecurityUser EditedUser  ON EditedUser.user_id = p.EditedBy
OUTER APPLY (
    SELECT TOP 1 pi.Product_Item_ID, pi.WholeSalePrice
    FROM tblProductItem pi
    WHERE pi.Product_ID = p.product_id
    ORDER BY pi.Product_Item_ID
) sku
OUTER APPLY (
    SELECT TOP 1 pp.product_price
    FROM tblDefProductPrice pp
    WHERE pp.product_item_id = sku.Product_Item_ID AND pp.end_date IS NULL
) price
WHERE p.product_id = @productId";

        return db.QueryFirstOrDefaultAsync<ProductResponse>(sql, new { productId }, ct);
    }

    public async Task<bool> ProductCodeExistsAsync(string productCode, int? excludingProductId, CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT(1) FROM tblDefProducts
WHERE Product_code = @productCode
  AND (@excludingProductId IS NULL OR product_id <> @excludingProductId)";

        var count = await db.ExecuteScalarAsync<int?>(sql, new { productCode, excludingProductId }, ct);
        return (count ?? 0) > 0;
    }

    public async Task<(int SizeId, int CombinationId)?> GetDefaultSizeAndCombinationAsync(int lineItemId,
        CancellationToken ct)
    {
        // frmDefProduct.vb:4104-4116 — the one READONLY size and combination row a line
        // item keeps for products with no real size/colour matrix.
        const string sizeSql =
            "SELECT TOP 1 size_id FROM tblDefSizes WHERE line_item_id = @lineItemId AND readonly = 'READONLY'";
        const string combinationSql =
            "SELECT TOP 1 combinition_id FROM tblDefCombinitions WHERE line_item_id = @lineItemId AND readonly = 'READONLY'";

        var p = new { lineItemId };
        var sizeId = await db.ExecuteScalarAsync<int?>(sizeSql, p, ct);
        var combinationId = await db.ExecuteScalarAsync<int?>(combinationSql, p, ct);

        if (sizeId is null || combinationId is null) return null;
        return (sizeId.Value, combinationId.Value);
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetMandatoryFieldsAsync(CancellationToken ct)
    {
        // frmDefProduct.vb:10673 GetMendatoryFieldList — the column really is called
        // Hidden in tblDefScreenCustomization, but for Formid = 0 it is reused to mean
        // "mandatory on frmDefProduct", not visibility. Kept as-is rather than renamed on
        // the way in, so a reader diffing against the desktop query isn't confused by a
        // "fixed" name that no longer matches the schema.
        const string sql =
            "SELECT ControlName, Hidden FROM tblDefScreenCustomization WHERE Formid = 0";

        var rows = await db.QueryAsync<MandatoryFieldRow>(sql, ct: ct);
        return rows.ToDictionary(
            r => r.ControlName.Trim().ToLowerInvariant(),
            r => r.Hidden,
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed class MandatoryFieldRow
    {
        public string ControlName { get; set; } = "";
        public bool Hidden { get; set; }
    }

    public async Task<int> CreateAsync(ProductRequest request, int sizeId, int combinationId, int userId,
        CancellationToken ct)
    {
        var result = await legacy.PostAsync<LegacyProductResult>("products",
            ToLegacy(request, sizeId, combinationId, userId), ct);
        return result.ProductId;
    }

    public Task UpdateAsync(int productId, ProductRequest request, int sizeId, int combinationId, int userId,
        CancellationToken ct)
        => legacy.PutAsync<LegacyProductResult>($"products/{productId}",
            ToLegacy(request, sizeId, combinationId, userId), ct);

    public Task DeleteAsync(int productId, int userId, CancellationToken ct)
        => legacy.DeleteAsync<LegacyProductResult>($"products/{productId}?userId={userId}", ct);

    /// <summary>
    /// Everything ProductsLegacyController needs to build a Model.Configuration.Product —
    /// field names deliberately match ProductRequest, camelCase, since both sides of this
    /// hop are code this repo owns and there is no legacy wire shape to preserve here
    /// (unlike LoginResponse's snake_case).
    /// </summary>
    private static object ToLegacy(ProductRequest r, int sizeId, int combinationId, int userId) => new
    {
        lineItemId = r.LineItemId,
        productCode = r.ProductCode,
        itemName = r.ItemName,
        categoryId = r.CategoryId,
        subCategoryId = r.SubCategoryId,
        productGroupId = r.ProductGroupId,
        variable1Id = r.Variable1Id,
        variable2Id = r.Variable2Id,
        variable3Id = r.Variable3Id,
        variable4Id = r.Variable4Id,
        variable5Id = r.Variable5Id,
        calendarSeasonId = r.CalendarSeasonId,
        supplierId = r.SupplierId,
        acquireType = r.AcquireType,
        purchaseType = r.PurchaseType,
        manufactureType = r.ManufactureType,
        purConUnit = r.PurConUnit,
        purConFactor = r.PurConFactor,
        saleTaxCodeId = r.SaleTaxCodeId,
        saleTax = r.SaleTax,
        vat = r.Vat,
        vatIsValue = r.VatIsValue,
        taxAtRetailPrice = r.TaxAtRetailPrice,
        price = r.Price,
        wholeSalePrice = r.WholeSalePrice,
        averageCost = r.AverageCost,
        userPrice = r.UserPrice,
        floatingPrice = r.FloatingPrice,
        currencyId = r.CurrencyId,
        active = r.Active,
        isDefault = r.IsDefault,
        noBarcodePrint = r.NoBarcodePrint,
        rfidEnabled = r.RfidEnabled,
        allowBelowCost = r.AllowBelowCost,
        allowPriceChange = r.AllowPriceChange,
        isWebItem = r.IsWebItem,
        itemNotForDiscount = r.ItemNotForDiscount,
        isBasicType = r.IsBasicType,
        numberOfPieces = r.NumberOfPieces,
        hsCode = r.HsCode,
        vendorCode = r.VendorCode,
        technicalDetails = r.TechnicalDetails,
        comments = r.Comments,
        internalComments = r.InternalComments,
        sizeId,
        combinationId,
        userId
    };

    /// <summary>What the legacy host returns from a write: the id it settled on.</summary>
    private sealed class LegacyProductResult
    {
        [Newtonsoft.Json.JsonProperty("product_id")]
        public int ProductId { get; set; }
    }
}
