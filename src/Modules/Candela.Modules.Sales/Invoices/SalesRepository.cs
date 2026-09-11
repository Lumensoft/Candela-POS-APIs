using System.Text;
using Candela.Modules.Sales.Invoices.Dtos;
using Candela.Platform.Data;
using Dapper;

namespace Candela.Modules.Sales.Invoices;

/// <summary>
/// GetSales and GetSale copied from the net48 SalesController — the SELECT columns, the
/// conditional filter appends, the count wrapper and the OFFSET/FETCH pagination are all
/// byte-identical.
/// </summary>
public sealed class SalesRepository(IDb db) : ISalesRepository
{
    public async Task<SaleSearchResponse> SearchAsync(int shopId, int page, int pageSize,
        string? q, string? from, string? to, string? invoiceNo, CancellationToken ct)
    {
        const string baseSql = @"
SELECT
    s.sale_id,
    s.invoice_no,
    s.sale_date,
    isnull(m.member_name, s.cust_name)           AS customer_name,
    m.member_id                                   AS customer_id,
    isnull(m.phone_Mobile, '')                    AS customer_phone,
    isnull(e.field_name, '')                      AS salesperson_name,
    s.GT_amount                                   AS gross_total,
    s.NT_amount                                   AS net_total,
    isnull(s.Mark_discount, 0)                    AS marketing_discount,
    isnull(s.vat, 0)                              AS vat,
    isnull(s.Cash_amt, 0)                         AS cash_amt,
    isnull(s.Card_amt, 0)                         AS card_amt,
    isnull(s.isCreditSale, 0)                     AS is_credit_sale,
    isnull(s.isMixSale, 0)                        AS is_mix_sale,
    CASE WHEN isnull(s.SaleReturningNo, 0) > 0
              OR isnull(s.NT_amount, 0) < 0
         THEN 1 ELSE 0 END                        AS is_return,
    isnull(s.SaleReturningNo, 0)                  AS return_of_sale_id,
    isnull(s.invoice_type, '')                    AS invoice_type,
    isnull(s.cust_bal, 0)                         AS balance
FROM tblSales s
LEFT JOIN tblMemberInfo m
    ON  m.member_id = s.member_id
    AND m.shop_id   = s.MemberShopID
LEFT JOIN tblDefShopEmployees e
    ON  e.shop_employee_id = s.employee_id
WHERE s.shop_id = @shopId";

        var where = new StringBuilder(baseSql);
        var p = new DynamicParameters();
        p.Add("@shopId", shopId);

        if (!string.IsNullOrWhiteSpace(invoiceNo))
        {
            where.Append(" AND s.invoice_no = @invoiceNo");
            p.Add("@invoiceNo", invoiceNo);
        }
        else if (!string.IsNullOrWhiteSpace(q))
        {
            where.Append(" AND (m.member_name LIKE @q OR s.cust_name LIKE @q)");
            p.Add("@q", "%" + q + "%");
        }

        if (!string.IsNullOrEmpty(from) && DateTime.TryParse(from, out DateTime fromDt))
        {
            where.Append(" AND s.sale_date >= @fromDt");
            p.Add("@fromDt", fromDt.Date);
        }

        if (!string.IsNullOrEmpty(to) && DateTime.TryParse(to, out DateTime toDt))
        {
            where.Append(" AND s.sale_date < @toDt");
            p.Add("@toDt", toDt.Date.AddDays(1));
        }

        int offset = (page - 1) * pageSize;
        string finalSql = where.ToString()
            + " ORDER BY s.sale_id DESC"
            + $" OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY";

        string countSql = "SELECT COUNT(*) FROM (" + where.ToString() + ") AS cnt";

        int total = await db.ExecuteScalarAsync<int>(countSql, p, ct);
        var data = await db.QueryRowsAsync(finalSql, p, ct);

        return new SaleSearchResponse
        {
            Total = total,
            Page = page,
            PageSize = pageSize,
            Count = data.Count,
            Data = data,
        };
    }

    public async Task<SaleDetailResponse?> GetSaleAsync(int saleId, int shopId, CancellationToken ct)
    {
        const string hdrSql = @"
SELECT
    s.sale_id, s.invoice_no, s.sale_date,
    isnull(m.member_name, s.cust_name)           AS customer_name,
    m.member_id                                   AS customer_id,
    isnull(e.field_name, '')                      AS salesperson_name,
    s.GT_amount                                   AS gross_total,
    s.NT_amount                                   AS net_total,
    isnull(s.Mark_discount, 0)                    AS marketing_discount,
    isnull(s.vat, 0)                              AS vat,
    isnull(s.Cash_amt, 0)                         AS cash_amt,
    isnull(s.Card_amt, 0)                         AS card_amt,
    isnull(s.isCreditSale, 0)                     AS is_credit_sale,
    CASE WHEN isnull(s.SaleReturningNo, 0) > 0
              OR isnull(s.NT_amount, 0) < 0
         THEN 1 ELSE 0 END                        AS is_return,
    isnull(s.SaleReturningNo, 0)                  AS return_of_sale_id,
    isnull(s.IsVoided, 0)                         AS is_voided
FROM tblSales s
LEFT JOIN tblMemberInfo m
    ON  m.member_id = s.member_id AND m.shop_id = s.MemberShopID
LEFT JOIN tblDefShopEmployees e
    ON  e.shop_employee_id = s.employee_id
WHERE s.sale_id = @saleId AND s.shop_id = @shopId";

        var hdrRows = await db.QueryRowsAsync(hdrSql, new { saleId, shopId }, ct);
        if (hdrRows.Count == 0) return null;

        const string liSql = @"
SELECT
    sli.product_item_id,
    isnull(pd.item_name,    '') AS item_name,
    isnull(pd.product_code, '') AS product_code,
    sli.qty                                              AS quantity,
    sli.unit_price                                       AS unit_rate,
    sli.unit_price * abs(sli.qty)                        AS net_amount,
    isnull(sli.product_discount_amount, 0)               AS unit_discount,
    isnull(sli.pro_vat, 0)                               AS vat,
    isnull(sli.is_return_item, 0)                        AS is_return_item
FROM tblSalesLineItems sli
JOIN tblProductItem pi ON pi.Product_Item_ID = sli.product_item_id
JOIN tblDefProducts pd ON pd.product_id      = pi.product_id
WHERE sli.sale_id = @saleId
ORDER BY sli.sale_line_item_id";

        var items = await db.QueryRowsAsync(liSql, new { saleId }, ct);

        return new SaleDetailResponse
        {
            Data = hdrRows[0],
            Items = items,
        };
    }
}
