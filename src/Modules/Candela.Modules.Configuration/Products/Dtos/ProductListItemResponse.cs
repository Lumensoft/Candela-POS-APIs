namespace Candela.Modules.Configuration.Products.Dtos;

/// <summary>
/// One row of the All Products grid (TabPgAllProducts). The desktop's own grid joins
/// ~15 lookup tables for a much wider set of columns (ProductDAL.GetAll); this keeps the
/// columns an admin actually scans a list by, matching the density of the Cities list.
/// Opening the row for edit fetches the rest via GET .../products/{id}.
/// </summary>
public sealed class ProductListItemResponse
{
    public int ProductId { get; set; }
    public string ProductCode { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string LineItemName { get; set; } = "";
    public string? CategoryName { get; set; }
    public string? SupplierName { get; set; }
    public double Price { get; set; }
    public bool Active { get; set; }
}
