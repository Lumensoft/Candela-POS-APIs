using Candela.Platform.Data;

namespace Candela.Modules.Sales.Products;

/// <summary>
/// The SQL is copied verbatim from the .NET Framework host. Reformatting it, "fixing" an
/// isnull or tidying a join would change column names or null handling — and the column
/// names ARE the wire contract here, because rows go back to the tablet as dictionaries
/// keyed by column name. That is also why this uses QueryRowsAsync rather than a typed
/// result: the shape is decided by the SQL, not by a class.
/// </summary>
public sealed class ProductRepository(IDb db) : IProductRepository
{
    public Task<IReadOnlyList<Dictionary<string, object?>>> GetAlternatesAsync(
        int productItemId, int shopId, CancellationToken ct)
    {
        // Substitute/alternate items — shown when the scanned product has alternatives.
        // tblDefProductAlternates links the original to its substitutes.
        const string sql = @"
SELECT
    alt.alternate_item_id                   AS product_item_id,
    pd.item_name,
    pd.product_code,
    isnull(pi.CustomerSKUCode, '')          AS barcode,
    isnull(pp.product_price, 0)            AS price,
    isnull(pd.vat, 0)                      AS vat,
    isnull(pd.vat_type, '')                AS vat_type,
    isnull(inv.quantity, 0)                AS stock_qty
FROM tblDefProductAlternates alt
JOIN tblProductItem pi   ON pi.Product_Item_ID = alt.alternate_item_id
JOIN tblDefProducts pd   ON pd.product_id = pi.product_id
LEFT JOIN tblDefProductPrice pp
       ON pp.product_item_id = alt.alternate_item_id
      AND ((pp.start_date < GETDATE() AND pp.end_date IS NULL)
        OR  (pp.start_date < GETDATE() AND pp.end_date > GETDATE()))
LEFT JOIN tblShopProductInventory inv
       ON inv.product_item_id = alt.alternate_item_id AND inv.shop_id = @shopId
WHERE alt.product_item_id = @id";

        return db.QueryRowsAsync(sql, new { id = productItemId, shopId }, ct);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetBatchesAsync(
        int productItemId, int shopId, CancellationToken ct)
    {
        // Batch/expiry list for a product at this shop.
        // Only batches with qty > 0 — no point showing empty stock.
        const string sql = @"
SELECT
    b.batch_id,
    isnull(b.batch_no, '')                  AS batch_no,
    b.expiry_date,
    isnull(b.quantity, 0)                   AS quantity,
    isnull(b.manufacturing_date, NULL)      AS manufacturing_date
FROM tblProductBatch b
WHERE b.product_item_id = @id
  AND b.shop_id         = @shopId
  AND isnull(b.quantity, 0) > 0
ORDER BY b.expiry_date ASC";

        return db.QueryRowsAsync(sql, new { id = productItemId, shopId }, ct);
    }
}
