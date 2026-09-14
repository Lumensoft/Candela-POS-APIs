using Candela.Modules.Configuration.Products.Dtos;
using Candela.Platform.Data;

namespace Candela.Modules.Configuration.Products;

/// <summary>
/// Straight ports of frmDefProduct.vb's FillCombos queries (one DAL class each on the
/// desktop — CalSeasonDAL, AcquireTypeDAL, SupplierDAL, …), consolidated here because
/// every one of them is the same two-column id/name shape. Every column is aliased to
/// "Id"/"Name" in SQL so LookupItem binds without a mapper.
/// </summary>
public sealed class ProductLookupsRepository(IDb db) : IProductLookupsRepository
{
    public async Task<ProductLookupsResponse> GetGlobalLookupsAsync(CancellationToken ct)
    {
        // AcquireType, PurchaseType, ManufactureType, Variable3 (Life Type) and Variable4
        // (Gender) are all rows of one shared table, cboTableCollection, distinguished by
        // cboTableName — confirmed against AcquireTypeDAL, PurchaseTypeDAL,
        // ManufacturerDAL and CBOCollectionDAL.vb:996. cboID (aliased "Code" on the
        // desktop) is what the product record actually stores, not the identity column —
        // CR#7615 switched every one of these five from id to code.
        var acquireTypes = await GetCboCollectionAsync("cboProductAcquireType", ct);
        var purchaseTypes = await GetCboCollectionAsync("cboProductPurchaseType", ct);
        var manufacturers = await GetCboCollectionAsync("cboProductManufacturedBy", ct);
        var variable3s = await GetCboCollectionAsync("cboProductLifeType", ct);
        var variable4s = await GetCboCollectionAsync("cboProductGender", ct);

        const string lineItemsSql =
            "SELECT line_item_id AS Id, field_name AS Name FROM tblDefLineItems " +
            "ORDER BY sort_order, field_name";

        const string calSeasonsSql =
            "SELECT calendar_season_id AS Id, field_name AS Name FROM tblDefCalendarSeasons " +
            "ORDER BY sort_order, field_name";

        const string variable5Sql =
            "SELECT Value_Addition_By_ID AS Id, Field_Name AS Name FROM tblDefProductValueAdditionBy " +
            "ORDER BY sort_order, field_name";

        const string suppliersSql =
            "SELECT supplier_id AS Id, field_name AS Name FROM tblDefSuppliers " +
            "ORDER BY sort_order, field_name";

        // "Unit" reuses cboDiscountTypes — a naming leftover from whatever this table was
        // built for originally, confirmed against UnitDAL.GetAll.
        const string unitsSql =
            "SELECT discount_type_id AS Id, field_name AS Name FROM cboDiscountTypes " +
            "ORDER BY field_name";

        const string currenciesSql =
            "SELECT Currency_id AS Id, Currency AS Name FROM tblDefCurrency ORDER BY Currency_id";

        return new ProductLookupsResponse
        {
            LineItems = (await db.QueryAsync<LookupItem>(lineItemsSql, ct: ct)).ToList(),
            CalSeasons = (await db.QueryAsync<LookupItem>(calSeasonsSql, ct: ct)).ToList(),
            AcquireTypes = acquireTypes,
            PurchaseTypes = purchaseTypes,
            Manufacturers = manufacturers,
            Suppliers = (await db.QueryAsync<LookupItem>(suppliersSql, ct: ct)).ToList(),
            Units = (await db.QueryAsync<LookupItem>(unitsSql, ct: ct)).ToList(),
            Currencies = (await db.QueryAsync<LookupItem>(currenciesSql, ct: ct)).ToList(),
            Variable3s = variable3s,
            Variable4s = variable4s,
            Variable5s = (await db.QueryAsync<LookupItem>(variable5Sql, ct: ct)).ToList(),
            // TODO: read the real per-install captions — GetSystemConfigurationValue("Life
            // Type" / "Product Gender" / "Value Addition"), same table AuthRepository
            // already reads tblRCMSConfiguration from. English defaults until then.
        };
    }

    public async Task<ProductLineItemLookupsResponse> GetLineItemLookupsAsync(int lineItemId, CancellationToken ct)
    {
        const string categoriesSql =
            "SELECT category_id AS Id, field_name AS Name FROM tblDefCategory " +
            "WHERE line_item_id = @lineItemId ORDER BY sort_order, field_name";

        const string productGroupsSql =
            "SELECT product_group_id AS Id, field_name AS Name FROM tblDefProductGroups " +
            "WHERE line_item_id = @lineItemId ORDER BY sort_order, field_name";

        // Variable1 = Age Groups, Variable2 = Packaging Codes on a stock install — both
        // labels are themselves configurable, same as Variable3/4/5.
        const string variable1Sql =
            "SELECT age_group_id AS Id, field_name AS Name FROM tblDefAgeGroups " +
            "WHERE line_item_id = @lineItemId ORDER BY sort_order, field_name";

        const string variable2Sql =
            "SELECT Packaging_Code_Id AS Id, field_name AS Name FROM tblDefPackagingCodes " +
            "WHERE line_item_id = @lineItemId ORDER BY sort_order, field_name";

        var p = new { lineItemId };

        return new ProductLineItemLookupsResponse
        {
            Categories = (await db.QueryAsync<LookupItem>(categoriesSql, p, ct)).ToList(),
            ProductGroups = (await db.QueryAsync<LookupItem>(productGroupsSql, p, ct)).ToList(),
            Variable1s = (await db.QueryAsync<LookupItem>(variable1Sql, p, ct)).ToList(),
            Variable2s = (await db.QueryAsync<LookupItem>(variable2Sql, p, ct)).ToList(),
        };
    }

    public async Task<ProductSubCategoryLookupResponse> GetSubCategoriesAsync(int categoryId, CancellationToken ct)
    {
        // tbldefdefects' parent column is physically named line_item_id but holds the
        // CATEGORY id — confirmed against SubCategoryDAL.GetAll, which aliases it
        // "[Category ID]" rather than "[Line Item ID]". Not a typo; kept as-is on purpose
        // so a future reader diffing against the desktop query isn't confused by a
        // "fixed" column name that no longer matches the schema.
        const string sql =
            "SELECT defect_id AS Id, field_name AS Name FROM tbldefdefects " +
            "WHERE line_item_id = @categoryId ORDER BY sort_order, field_name";

        var rows = await db.QueryAsync<LookupItem>(sql, new { categoryId }, ct);
        return new ProductSubCategoryLookupResponse { SubCategories = rows.ToList() };
    }

    /// <summary>
    /// cboTableCollection rows for one logical list — AcquireType, PurchaseType,
    /// ManufactureType, Variable3 and Variable4 are all this same table, one filter value
    /// each. cboID is aliased Id (not ID) because that is what the product record stores
    /// after CR#7615 — see the class doc.
    /// </summary>
    private async Task<List<LookupItem>> GetCboCollectionAsync(string cboTableName, CancellationToken ct)
    {
        const string sql =
            "SELECT cboID AS Id, ENGL_US AS Name FROM cboTableCollection " +
            "WHERE cboTableName = @cboTableName ORDER BY SortOrder, ENGL_US";

        var rows = await db.QueryAsync<LookupItem>(sql, new { cboTableName }, ct);
        return rows.ToList();
    }
}
