namespace Candela.Modules.Configuration.Products.Dtos;

/// <summary>
/// Every dropdown the Product form needs, fetched once — the same idea as the desktop's
/// FillCombos, which loads all of these from an app-wide cache in one pass rather than
/// re-querying per field (frmDefProduct.vb:569).
///
/// Three different shapes hide in here, and the frontend has to respect all of them:
///   * Global lists (LineItems, CalSeasons, Variable3/4/5, AcquireTypes, PurchaseTypes,
///     Manufacturers, Suppliers, Units, Currencies) — fetch once, never change with the
///     rest of the form.
///   * Line-item-scoped lists (Categories, ProductGroups, Variable1s, Variable2s) — empty
///     until a line item is chosen, then re-fetched via
///     GET .../lookups/by-line-item/{lineItemId} every time it changes.
///   * Category-scoped (SubCategories) — a second cascade level: tblDefDefects stores its
///     parent as a column literally called line_item_id, but it actually holds the
///     CATEGORY id, not the line item's (a schema leftover, confirmed against
///     SubCategoryDAL.GetAll). So SubCategory follows Category, not Line Item, and is
///     fetched separately via GET .../lookups/by-category/{categoryId}.
/// Selecting a new line item invalidates whatever the user had picked in any of these
/// five fields, exactly like the desktop does.
///
/// Variable3Label/Variable4Label/Variable5Label carry the configurable captions
/// (GetSystemConfigurationValue("Life Type") etc.) — the field is always "Product
/// Variable 3", but what the user sees it called is a per-install setting.
/// </summary>
public sealed class ProductLookupsResponse
{
    public List<LookupItem> LineItems { get; set; } = new();
    public List<LookupItem> CalSeasons { get; set; } = new();
    public List<LookupItem> AcquireTypes { get; set; } = new();
    public List<LookupItem> PurchaseTypes { get; set; } = new();
    public List<LookupItem> Manufacturers { get; set; } = new();
    public List<LookupItem> Suppliers { get; set; } = new();
    public List<LookupItem> Units { get; set; } = new();
    public List<LookupItem> Currencies { get; set; } = new();

    public List<LookupItem> Variable3s { get; set; } = new();
    public List<LookupItem> Variable4s { get; set; } = new();
    public List<LookupItem> Variable5s { get; set; } = new();
    public string Variable3Label { get; set; } = "Life Type";
    public string Variable4Label { get; set; } = "Product Gender";
    public string Variable5Label { get; set; } = "Value Addition";
}

/// <summary>
/// The four dropdowns that depend on which Line Item is selected — fetched separately, on
/// every line-item change, rather than bundled into ProductLookupsResponse. See its doc
/// comment for why the split exists. SubCategory is NOT here — it cascades from Category,
/// see ProductSubCategoryLookupResponse.
/// </summary>
public sealed class ProductLineItemLookupsResponse
{
    public List<LookupItem> Categories { get; set; } = new();
    public List<LookupItem> ProductGroups { get; set; } = new();
    public List<LookupItem> Variable1s { get; set; } = new();
    public List<LookupItem> Variable2s { get; set; } = new();
    public string Variable1Label { get; set; } = "Product Variable 1";
    public string Variable2Label { get; set; } = "Product Variable 2";
}

/// <summary>The SubCategory dropdown — cascades from Category, one level below Line Item.</summary>
public sealed class ProductSubCategoryLookupResponse
{
    public List<LookupItem> SubCategories { get; set; } = new();
}
