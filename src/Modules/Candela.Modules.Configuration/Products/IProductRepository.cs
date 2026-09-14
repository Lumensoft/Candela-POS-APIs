using Candela.Modules.Configuration.Products.Dtos;

namespace Candela.Modules.Configuration.Products;

public interface IProductRepository
{
    Task<IReadOnlyList<ProductListItemResponse>> GetAllAsync(CancellationToken ct);

    Task<ProductResponse?> GetByIdAsync(int productId, CancellationToken ct);

    Task<bool> ProductCodeExistsAsync(string productCode, int? excludingProductId, CancellationToken ct);

    /// <summary>
    /// The one Size and one Combination every non-assortment product attaches to
    /// (frmDefProduct.vb:4104-4116 — the READONLY row tblDefSizes/tblDefCombinitions
    /// keep per line item for products that carry no real size/colour). Null when the
    /// line item has no such row configured, which the service turns into a clear error
    /// rather than a DAL failure with no context.
    /// </summary>
    Task<(int SizeId, int CombinationId)?> GetDefaultSizeAndCombinationAsync(int lineItemId, CancellationToken ct);

    /// <summary>
    /// The per-install "is this control mandatory" flags from tblDefScreenCustomization
    /// (Formid = 0) — despite the column being called Hidden, this table doubles as the
    /// mandatory-field switchboard for frmDefProduct specifically (GetMendatoryFieldList,
    /// frmDefProduct.vb:10673). Keyed by control name, lower-cased.
    /// </summary>
    Task<IReadOnlyDictionary<string, bool>> GetMandatoryFieldsAsync(CancellationToken ct);

    /// <summary>Create through the legacy host — ProductDAL.Add, SQL log and activity log included.</summary>
    Task<int> CreateAsync(ProductRequest request, int sizeId, int combinationId, int userId, CancellationToken ct);

    /// <summary>Update through the legacy host — ProductDAL.Update.</summary>
    Task UpdateAsync(int productId, ProductRequest request, int sizeId, int combinationId, int userId,
        CancellationToken ct);

    /// <summary>Delete through the legacy host — ProductDAL.Deleted, refused with 422 if referenced elsewhere.</summary>
    Task DeleteAsync(int productId, int userId, CancellationToken ct);
}
