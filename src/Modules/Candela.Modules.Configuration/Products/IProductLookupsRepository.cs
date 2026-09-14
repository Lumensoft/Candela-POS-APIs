using Candela.Modules.Configuration.Products.Dtos;

namespace Candela.Modules.Configuration.Products;

/// <summary>Every dropdown source frmDefProduct.vb's FillCombos loads, ported one query at a time.</summary>
public interface IProductLookupsRepository
{
    /// <summary>Everything that does not depend on which Line Item is selected.</summary>
    Task<ProductLookupsResponse> GetGlobalLookupsAsync(CancellationToken ct);

    /// <summary>Category, Product Group, Variable1, Variable2 — filtered by Line Item.</summary>
    Task<ProductLineItemLookupsResponse> GetLineItemLookupsAsync(int lineItemId, CancellationToken ct);

    /// <summary>SubCategory — filtered by Category (tbldefdefects' own parent column, despite
    /// being named line_item_id in the table, actually holds the category id).</summary>
    Task<ProductSubCategoryLookupResponse> GetSubCategoriesAsync(int categoryId, CancellationToken ct);
}
