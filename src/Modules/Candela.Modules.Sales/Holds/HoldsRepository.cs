using System.Data;
using Candela.Platform.Data;
using Dapper;

namespace Candela.Modules.Sales.Holds;

/// <summary>
/// The two SELECTs are copied verbatim from the net48 HoldsController.QueryHolds — the
/// column aliases (hold_id, customer_id, unit_discount, …) are the wire contract, so
/// nothing about them is reformatted. The headers/lines join is done in memory exactly
/// as the original did: one pass to bucket lines by sale_id, one pass to attach them.
/// </summary>
public sealed class HoldsRepository(IDb db) : IHoldsRepository
{
    private const string HeaderSql = @"
SELECT
    h.Sale_id        AS hold_id,
    h.shop_id,
    h.sale_date,
    isnull(h.member_id, 0)   AS customer_id,
    isnull(h.Cust_name, '')  AS customer_name,
    h.GT_amount              AS gross_total,
    h.NT_amount              AS net_total,
    isnull(h.Mark_Discount, 0)       AS marketing_discount,
    isnull(h.adjustment_amount, 0)   AS adjustment_amount,
    isnull(h.Adjustment_comments,'') AS comments,
    isnull(h.isCreditSale, 0)        AS is_credit_sale,
    isnull(h.Cash_amt, 0)            AS cash_amount,
    isnull(h.Card_amt, 0)            AS card_amount,
    isnull(h.CreditAmount, 0)        AS credit_amount,
    isnull(h.vat, 0)                 AS vat_amount
FROM tblSalesHolding h
WHERE h.shop_id = @shopId
ORDER BY h.sale_date DESC";

    private const string LinesSql = @"
SELECT
    l.sale_id,
    l.Product_Item_ID            AS product_item_id,
    l.qty                        AS quantity,
    l.unit_price,
    isnull(l.product_discount_amount, 0) AS unit_discount,
    isnull(l.mem_discount_amount, 0)     AS customer_discount_per_unit,
    isnull(l.pro_vat, 0)                 AS vat_value,
    isnull(l.VatFactor, 0)               AS vat_factor,
    isnull(l.vat_type, '')               AS vat_type,
    isnull(l.PriceIncludeVat, 0)         AS price_include_vat,
    isnull(l.additional_tax_percent, 0)  AS additional_tax_percent,
    isnull(l.additional_tax, 0)          AS additional_tax,
    isnull(l.PriceForDiscount, 0)        AS tagged_price,
    isnull(l.PriceAfterDiscount, 0)      AS price_after_discount,
    isnull(l.DiscountCategory, '')       AS disc_category,
    isnull(l.discount_id, 0)             AS discount_id,
    isnull(l.Loyality_CashDiscount, 0)   AS loyalty_cash_discount
FROM tblSalesLineItemsHolding l
WHERE l.shop_id = @shopId";

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetHoldsAsync(int shopId, CancellationToken ct)
    {
        var headers = await db.QueryRowsAsync(HeaderSql, new { shopId }, ct);
        var lines   = await db.QueryRowsAsync(LinesSql,  new { shopId }, ct);

        // Bucket line rows by their sale_id, mirroring the original's lineMap.
        var byHold = new Dictionary<int, List<Dictionary<string, object?>>>();
        foreach (var line in lines)
        {
            var hId = Convert.ToInt32(line["sale_id"]);
            if (!byHold.TryGetValue(hId, out var bucket))
                byHold[hId] = bucket = new List<Dictionary<string, object?>>();
            bucket.Add(line);
        }

        // Attach items to each header. A header with no lines gets an empty list, not
        // null — same as the original.
        foreach (var header in headers)
        {
            var hId = Convert.ToInt32(header["hold_id"]);
            header["items"] = byHold.TryGetValue(hId, out var items)
                ? items
                : new List<Dictionary<string, object?>>();
        }

        return headers;
    }

    public async Task<bool> DeleteHoldAsync(int holdId, int shopId, CancellationToken ct)
    {
        try
        {
            await db.InTransactionAsync(async (con, tx) =>
            {
                // Lines first, then the header — the order the net48 endpoint used.
                await con.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM tblSalesLineItemsHolding WHERE sale_id = @holdId AND shop_id = @shopId",
                    new { holdId, shopId }, tx, cancellationToken: ct));

                var headerRows = await con.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM tblSalesHolding WHERE Sale_id = @holdId AND shop_id = @shopId",
                    new { holdId, shopId }, tx, cancellationToken: ct));

                // The original: `if (rows == 0) { tx.Rollback(); return NotFound; }`.
                // Throwing here makes InTransactionAsync roll back the line delete too,
                // so a non-existent hold leaves nothing changed — exactly the original.
                if (headerRows == 0) throw new HoldNotFound();
                return true;
            }, ct);

            return true;
        }
        catch (HoldNotFound)
        {
            return false;
        }
    }

    /// <summary>Internal signal: no header matched, roll the transaction back.</summary>
    private sealed class HoldNotFound : Exception;
}
