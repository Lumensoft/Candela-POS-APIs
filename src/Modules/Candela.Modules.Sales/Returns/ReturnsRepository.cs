using Candela.Platform.Data;

namespace Candela.Modules.Sales.Returns;

/// <summary>
/// Every statement is copied from the net48 ReturnsController (and, for the first two, from
/// the DAL functions it inlines). The two inlined DAL reads build their WHERE with string
/// concatenation on integer ids exactly as the VB did — no behaviour change, and the ids
/// are ints so there is nothing to parameterise away.
/// </summary>
public sealed class ReturnsRepository(IDb db) : IReturnsRepository
{
    public async Task<bool> IsValidInvoiceForReturnAsync(int shopId, int invoiceNo, CancellationToken ct)
    {
        // SaleAndReturnDAL.vb:437 — verbatim, Val(ExecuteScaler) > 0.
        string sql =
            "select distinct(tblsales.sale_id) from tblSales inner join tblsaleslineitems on  " +
            "tblSales.sale_id=tblsaleslineitems.sale_id and tblSales.shop_id=tblsaleslineitems.shop_id " +
            "where tblSales.sale_id=" + invoiceNo + " and tblSales.shop_id=" + shopId + " and is_return_item<>1";

        var v = await db.ExecuteScalarAsync<object>(sql, null, ct);
        return v is not null && v != DBNull.Value && Convert.ToDouble(v) > 0;
    }

    public async Task<string> GetCustomerAgainstInvoiceAsync(int shopId, int invoiceNo, CancellationToken ct)
    {
        // SaleAndReturnDAL.vb:457 — verbatim.
        string sql =
            "Select RIGHT('000' + tblDefShops.ShopMembershipCode, 3) + '-' + " +
            "RIGHT('000000' + CONVERT(varchar(6),tblMemberInfo.member_no), 6) + '-' + " +
            "RIGHT('0' + CONVERT(varchar(2), tblMemberInfo.card_duplicate_no), 2)  AS [Customer Code] " +
            "from tblMemberInfo " +
            "inner join tblDefShops on tblDefShops.shop_id = tblMemberInfo.shop_id " +
            "inner join tblSales on tblSales.member_id = tblMemberInfo.member_id and " +
            "tblSales.memberShopID = tblMemberInfo.shop_id  " +
            "where sale_id = " + invoiceNo + " and tblSales.shop_id = " + shopId + " ";

        // The VB returned SQLHelper.ExecuteScaler directly (null -> ""), which the
        // controller then coalesced again with `?? ""`.
        return await db.ExecuteScalarAsync<string>(sql, null, ct) ?? "";
    }

    public async Task<Dictionary<string, object?>?> QuerySaleHeaderAsync(int shopId, int invoiceNo,
        CancellationToken ct)
    {
        const string sql = @"
SELECT
    s.sale_id, s.shop_id, s.sale_date,
    isnull(s.member_id, 0)    AS customer_id,
    isnull(s.GT_amount, 0)    AS gross_total,
    isnull(s.NT_amount, 0)    AS net_total,
    isnull(s.Mark_discount,0) AS marketing_discount,
    isnull(s.vat, 0)          AS vat_amount,
    isnull(s.adjustment_amount, 0) AS adjustment_amount,
    isnull(s.Adjustment_comments,'') AS comments,
    isnull(s.invoice_type,'') AS invoice_type,
    isnull(s.iscreditsale, 0)  AS iscreditsale,
    isnull(s.credit_card_id, 0) AS credit_card_id
FROM tblSales s
WHERE s.sale_id = @invoiceNo AND s.shop_id = @shopId";

        var rows = await db.QueryRowsAsync(sql, new { invoiceNo, shopId }, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> QuerySaleItemsAsync(int shopId,
        int invoiceNo, CancellationToken ct)
    {
        const string sql = @"
SELECT
    li.sale_line_item_id,
    li.Product_Item_ID          AS product_item_id,
    pd.item_name,
    li.qty                      AS quantity,
    li.Unit_price,
    isnull(li.product_discount_amount, 0) AS unit_discount,
    isnull(li.mem_discount_amount, 0)     AS customer_discount_per_unit,
    isnull(li.pro_vat, 0)        AS vat_value,
    isnull(li.VatFactor, 0)      AS vat_factor,
    isnull(li.Vat_Type, '')      AS vat_type,
    isnull(li.PriceIncludeVat,0) AS price_include_vat,
    isnull(li.additional_tax_percent,0) AS additional_tax_percent,
    isnull(li.additional_tax, 0) AS additional_tax,
    isnull(li.Taged_Price, 0)    AS tagged_price,
    isnull(li.PriceAfterDiscount,0) AS price_after_discount,
    isnull(li.DiscountCategory,'')  AS disc_category,
    isnull(li.discount_ID, 0)       AS discount_id,
    isnull(li.Loyality_CashDiscount,0) AS loyalty_cash_discount,
    isnull(li.CustomerDiscount, 0)  AS customer_discount
FROM tblSalesLineItems li
JOIN tblProductItem pi   ON pi.Product_Item_ID = li.Product_Item_ID
JOIN tblDefProducts pd   ON pd.product_id = pi.product_id
WHERE li.sale_id = @invoiceNo
  AND li.shop_id = @shopId
  AND isnull(li.is_return_item, 0) = 0";

        return db.QueryRowsAsync(sql, new { invoiceNo, shopId }, ct);
    }

    public async Task<IReadOnlyDictionary<int, Dictionary<string, object?>>> QuerySaleItemsForPreviewAsync(
        int shopId, int saleId, IReadOnlyList<int> productItemIds, CancellationToken ct)
    {
        var map = new Dictionary<int, Dictionary<string, object?>>();
        if (productItemIds.Count == 0) return map;

        // The id list is interpolated exactly as the net48 code did — the values are ints
        // from the request, already filtered to > 0 by the caller.
        var ids = string.Join(",", productItemIds.Select(id => id.ToString()));
        var sql = $@"
SELECT
    li.Product_Item_ID                   AS product_item_id,
    li.Unit_price                        AS unit_price,
    isnull(li.product_discount_amount,0) AS unit_discount,
    isnull(li.mem_discount_amount,0)     AS customer_discount_per_unit,
    isnull(li.pro_vat,0)                 AS vat_value,
    isnull(li.VatFactor,0)               AS vat_factor,
    isnull(li.Vat_Type,'')               AS vat_type,
    isnull(li.PriceIncludeVat,0)         AS price_include_vat,
    isnull(li.additional_tax_percent,0)  AS additional_tax_percent,
    isnull(li.additional_tax,0)          AS additional_tax,
    isnull(li.Taged_Price,0)             AS tagged_price,
    isnull(li.PriceAfterDiscount,0)      AS price_after_discount,
    isnull(li.DiscountCategory,'')       AS disc_category,
    isnull(li.discount_ID,0)             AS discount_id,
    isnull(li.Loyality_CashDiscount,0)   AS loyalty_cash_discount
FROM tblSalesLineItems li
WHERE li.sale_id  = @saleId
  AND li.shop_id  = @shopId
  AND li.Product_Item_ID IN ({ids})
  AND isnull(li.is_return_item,0) = 0";

        var rows = await db.QueryRowsAsync(sql, new { saleId, shopId }, ct);
        foreach (var row in rows)
            map[Convert.ToInt32(row["product_item_id"])] = row;

        return map;
    }

    public async Task<double> GetDiscountDurationAsync(int discountId, CancellationToken ct)
    {
        var v = await db.ExecuteScalarAsync<object>(
            "SELECT isnull(discount_duration,0) FROM tblDefDiscounts WHERE discount_id = @did",
            new { did = discountId }, ct);

        return v is not null && v != DBNull.Value ? Convert.ToDouble(v) : 0;
    }
}
