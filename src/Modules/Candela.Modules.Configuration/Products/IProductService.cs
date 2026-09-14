using Candela.Modules.Configuration.Products.Dtos;

namespace Candela.Modules.Configuration.Products;

/// <summary>
/// The Core Product tab's rules — frmDefProduct.vb's IsValidate/Save/Update/Delete for
/// EnumTabPages.TabPgProduct, non-assortment mode. See ProductRequest's doc comment for
/// what "non-assortment" means and what is deliberately out of scope for now.
/// </summary>
public interface IProductService
{
    Task<IReadOnlyList<Dtos.ProductListItemResponse>> ListAsync(CancellationToken ct);

    Task<ProductResponse> GetAsync(int productId, CancellationToken ct);

    Task<ProductResponse> CreateAsync(ProductRequest request, int userId, CancellationToken ct);

    Task<ProductResponse> UpdateAsync(int productId, ProductRequest request, int userId, CancellationToken ct);

    Task DeleteAsync(int productId, int userId, CancellationToken ct);

    Task<ProductLookupsResponse> GetLookupsAsync(CancellationToken ct);

    Task<ProductLineItemLookupsResponse> GetLineItemLookupsAsync(int lineItemId, CancellationToken ct);

    Task<ProductSubCategoryLookupResponse> GetSubCategoriesAsync(int categoryId, CancellationToken ct);
}
