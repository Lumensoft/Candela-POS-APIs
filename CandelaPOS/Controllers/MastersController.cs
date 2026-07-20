using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using CandelaPOS.Infrastructure;

namespace CandelaPOS.Controllers
{
    [RoutePrefix("api/masters")]
    public class MastersController : ApiController
    {
        // ── /masters/products ─────────────────────────────────────────────────────
        // GET api/masters/products
        // GET api/masters/products?since=2026-01-01T00:00:00
        [HttpGet, Route("products")]
        public HttpResponseMessage GetProducts([FromUri] string since = null)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];
            try
            {
                var rows = QueryProducts(shopId, ParseSince(since));
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // GET api/masters/products/scan?q=X
        // Scan-bar lookup — mirrors Candela's uiCtrlProductCode lookup sequence:
        //   1. product_code (CI via SQL Server default collation)
        //   2. CustomerSKUCode 1-5 (alternate/nested barcodes)
        // Always returns TOP 1, product_code match wins over barcode match.
        [HttpGet, Route("products/scan")]
        public HttpResponseMessage ScanProduct([FromUri] string q = null)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];
            try
            {
                if (string.IsNullOrWhiteSpace(q))
                    return Request.CreateResponse(HttpStatusCode.BadRequest,
                        new { error = "q is required" });
                var rows = ScanProductByQuery(shopId, q.Trim());
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // GET api/masters/products/search?barcode=X  OR  ?code=X
        // Kept for backward compatibility; scan bar now uses /products/scan instead.
        [HttpGet, Route("products/search")]
        public HttpResponseMessage SearchProduct([FromUri] string barcode = null,
                                                 [FromUri] string code    = null)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];
            try
            {
                if (string.IsNullOrWhiteSpace(barcode) && string.IsNullOrWhiteSpace(code))
                    return Request.CreateResponse(HttpStatusCode.BadRequest,
                        new { error = "barcode or code is required" });

                var rows = SearchProductByBarcode(shopId, barcode, code);
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // ── /masters/customers ────────────────────────────────────────────────────
        // GET api/masters/customers                          — full list (initial IndexedDB load)
        // GET api/masters/customers?since=2026-01-01T00:00:00  — delta sync
        // GET api/masters/customers?q=Ahmed                  — live search fallback (when IndexedDB miss)
        [HttpGet, Route("customers")]
        public HttpResponseMessage GetCustomers([FromUri] string since = null, [FromUri] string q = null)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];
            try
            {
                var rows = QueryCustomers(shopId, ParseSince(since), q);
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // ── /masters/employees ────────────────────────────────────────────────────
        // GET api/masters/employees
        // GET api/masters/employees?since=2026-01-01T00:00:00
        [HttpGet, Route("employees")]
        public HttpResponseMessage GetEmployees([FromUri] string since = null)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];
            try
            {
                var rows = QueryEmployees(shopId, ParseSince(since));
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // ── /masters/credit-cards ─────────────────────────────────────────────────
        // GET api/masters/credit-cards
        // GET api/masters/credit-cards?since=2026-01-01T00:00:00
        [HttpGet, Route("credit-cards")]
        public HttpResponseMessage GetCreditCards([FromUri] string since = null)
        {
            CandelaBootstrap.PrepareRequest();
            try
            {
                var rows = QueryCreditCards(ParseSince(since));
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // ── /masters/member-types ─────────────────────────────────────────────────
        // GET api/masters/member-types
        // GET api/masters/member-types?since=2026-01-01T00:00:00
        [HttpGet, Route("member-types")]
        public HttpResponseMessage GetMemberTypes([FromUri] string since = null)
        {
            CandelaBootstrap.PrepareRequest();
            try
            {
                var rows = QueryMemberTypes(ParseSince(since));
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // ── /masters/payment-methods ──────────────────────────────────────────────
        // Returns enabled mobile payment providers from tblRCMSConfiguration.
        // No delta — config is returned in full. App hides mobile tab when list is empty.
        [HttpGet, Route("payment-methods")]
        public HttpResponseMessage GetPaymentMethods()
        {
            CandelaBootstrap.PrepareRequest();
            try
            {
                var rows = QueryPaymentMethods();
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // ── /masters/return-reasons ───────────────────────────────────────────────
        // GET api/masters/return-reasons
        // GET api/masters/return-reasons?since=2026-01-01T00:00:00
        [HttpGet, Route("return-reasons")]
        public HttpResponseMessage GetReturnReasons([FromUri] string since = null)
        {
            CandelaBootstrap.PrepareRequest();
            try
            {
                var rows = QueryReturnReasons(ParseSince(since));
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // ── /masters/config ───────────────────────────────────────────────────────
        // Returns tblShopConfiguration + tblRCMSConfiguration merged as key/value pairs.
        // shop_config keys take precedence over rcms_config when the same key appears in both.
        // Delta: checks config_updated_at when present; falls back to full return (config is small).
        // GET api/masters/config
        // GET api/masters/config?since=2026-01-01T00:00:00
        [HttpGet, Route("config")]
        public HttpResponseMessage GetConfig([FromUri] string since = null)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];
            try
            {
                var rows = QueryConfig(shopId, ParseSince(since));
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // ── /masters/line-items ───────────────────────────────────────────────────
        // Returns all product departments (tblDefLineItems) for category tabs.
        // since= accepted for API consistency; tblDefLineItems has no timestamp column
        // so always returns the full set (list is tiny and rarely changes).
        [HttpGet, Route("line-items")]
        public HttpResponseMessage GetLineItems([FromUri] string since = null)
        {
            CandelaBootstrap.PrepareRequest();
            try
            {
                var rows = QueryLineItems();
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // ── /masters/batches ──────────────────────────────────────────────────────
        // GET api/masters/batches?product_item_id=123
        // Returns all non-zero-quantity batch rows for a product, ordered by ExpiryDate (FEFO).
        // Mirrors the CommonDAL.vb:1653 query used when building the batch grid on sale entry.
        [HttpGet, Route("batches")]
        public HttpResponseMessage GetBatches([FromUri] int product_item_id = 0)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];

            if (product_item_id <= 0)
                return Request.CreateResponse(System.Net.HttpStatusCode.BadRequest,
                    new { error = "product_item_id is required" });

            try
            {
                // Sum all batch movements for this product; positive net = stock available.
                // Ordered by ExpiryDate ascending = FEFO (First Expired, First Out) order.
                const string sql = @"
SELECT
    a.BatchNo                              AS batch_no,
    a.ExpiryDate                           AS expiry_date,
    a.ProductItemID                        AS product_item_id,
    a.Quantity                             AS available_qty,
    CASE
        WHEN a.ExpiryDate IS NULL THEN 0
        WHEN a.ExpiryDate < GETDATE() THEN 1
        ELSE 0
    END                                    AS is_expired
FROM (
    SELECT
        sum(Quantity)  AS Quantity,
        BatchNo,
        ExpiryDate,
        ProductItemID
    FROM tblbatchdetail
    WHERE ProductItemID = @productItemId
    GROUP BY BatchNo, ExpiryDate, ProductItemID
) a
WHERE a.Quantity > 0
ORDER BY a.ExpiryDate ASC";

                var rows = Run(sql, p => p.AddWithValue("@productItemId", product_item_id));
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // ── /masters/assembly-items ───────────────────────────────────────────────
        // GET api/masters/assembly-items?product_item_id=123
        // Returns default child components from tblDefProductAssembly with current retail prices.
        // Mirrors SaleAndReturnDAL.vb:239-247 — the NEW-mode assembly load path.
        [HttpGet, Route("assembly-items")]
        public HttpResponseMessage GetAssemblyItems([FromUri] int product_item_id = 0)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];

            if (product_item_id <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "product_item_id is required" });

            try
            {
                var rows = QueryAssemblyItems(product_item_id, shopId);
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // ── /masters/blocked-products ─────────────────────────────────────────────
        // Full-replace sync — tblBlockPrdctsForSale has no timestamp column so always
        // returns the complete set. Table is tiny; full fetch is cheap.
        [HttpGet, Route("blocked-products")]
        public HttpResponseMessage GetBlockedProducts()
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];
            try
            {
                var rows = QueryBlockedProducts(shopId);
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // ── /masters/str/{no}/products ────────────────────────────────────────────
        // Loads products from a Stock Transfer Request by STR number.
        // Wraps SaleAndReturnDAL.funGetStrProducts — mirrors txtSTRNo_GetSTR
        // frmSaleAndReturn.vb:22651
        [HttpGet, Route("str/{strNo}/products")]
        public HttpResponseMessage GetStrProducts(string strNo)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];
            try
            {
                if (string.IsNullOrWhiteSpace(strNo))
                    return Request.CreateResponse(HttpStatusCode.BadRequest,
                        new { error = "strNo is required" });

                var rows = QueryStrProducts(strNo, shopId);
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // SQL implementations
        // ─────────────────────────────────────────────────────────────────────────

        private List<Dictionary<string, object>> QueryProducts(int shopId, DateTime? since)
        {
            const string sql = @"
SELECT
    pi.Product_Item_ID                           AS product_item_id,
    CASE WHEN li.isassortmentenabled = 1
         THEN pd.product_code + '-' + sz.field_code + '-' + cl.field_code
         ELSE pd.product_code END                AS product_code,
    pd.item_name,
    pd.line_item_id,
    isnull(li.field_name, '')                    AS line_item,
    isnull(pi.CustomerSKUCode,  '')              AS barcode,
    isnull(pi.CustomerSKUCode2, '')              AS barcode2,
    isnull(pi.CustomerSKUCode3, '')              AS barcode3,
    isnull(pi.CustomerSKUCode4, '')              AS barcode4,
    isnull(pi.CustomerSKUCode5, '')              AS barcode5,
    isnull(pp.product_price, 0)                 AS price,
    isnull(pd.vat, 0)                           AS vat,
    isnull(pd.vat_type, '')                     AS vat_type,
    isnull(pd.NotForDiscount, 0)                AS not_for_discount,
    isnull(pd.Tax_At_Retail_Price, 0)           AS tax_at_retail_price,
    isnull(pd.Sale_Tax, 0)                      AS sale_tax,
    isnull(pd.custom_discount, 0)               AS custom_discount,
    isnull(pd.Basic_Designed, 1)                AS basic_designed,
    isnull(inv.quantity, 0)                     AS stock_qty,
    CASE WHEN blk.Block_Product_Id IS NOT NULL THEN 1 ELSE 0 END AS is_blocked_for_sale,
    CASE WHEN isnull(pd.product_life_type, 0) > 0 THEN 1 ELSE 0 END AS is_control_drug,
    CASE WHEN EXISTS (
        SELECT 1 FROM tblDefProductAssembly pa
        WHERE pa.Product_Item_ID_Assembly = pi.Product_Item_ID
    ) THEN 1 ELSE 0 END                         AS is_assembly,
    pd.entereddate,
    pd.editeddate
FROM tblProductItem pi
JOIN tblDefProducts pd      ON pd.product_id       = pi.product_id
JOIN tblDefSizes sz         ON sz.size_id          = pi.size_id          AND sz.line_item_id  = pd.line_item_id
JOIN tblDefCombinitions cl  ON cl.combinition_id   = pi.combinition_id   AND cl.line_item_id  = pd.line_item_id
JOIN tblDefLineItems li     ON li.line_item_id     = pd.line_item_id
LEFT JOIN tblDefProductPrice pp
       ON pp.product_item_id = pi.Product_Item_ID
      AND ((pp.start_date < GETDATE() AND pp.end_date IS NULL)
        OR  (pp.start_date < GETDATE() AND pp.end_date > GETDATE()))
LEFT JOIN tblShopProductInventory inv
       ON inv.product_item_id = pi.Product_Item_ID AND inv.shop_id = @shopId
LEFT JOIN tblBlockPrdctsForSale blk
       ON blk.product_item_id = pi.Product_Item_ID AND blk.shopID = @shopId
WHERE isnull(pd.status, 1) = 1";

            const string delta = " AND (pd.entereddate >= @since OR pd.editeddate >= @since)";
            return Run(sql + (since.HasValue ? delta : ""),
                p => { p.AddWithValue("@shopId", shopId);
                       if (since.HasValue) p.AddWithValue("@since", since.Value); });
        }

        private List<Dictionary<string, object>> SearchProductByBarcode(int shopId, string barcode, string code)
        {
            // Search by CustomerSKUCode (barcode) OR product_code.
            const string sql = @"
SELECT TOP 10
    pi.Product_Item_ID AS product_item_id,
    pd.product_code,
    pd.item_name,
    isnull(pi.CustomerSKUCode,  '') AS barcode,
    isnull(pi.CustomerSKUCode2, '') AS barcode2,
    isnull(pp.product_price, 0)    AS price,
    isnull(pd.vat, 0)              AS vat,
    isnull(pd.vat_type, '')        AS vat_type,
    isnull(pd.NotForDiscount, 0)   AS not_for_discount,
    isnull(inv.quantity, 0)        AS stock_qty,
    CASE WHEN EXISTS (
        SELECT 1 FROM tblDefProductAssembly pa
        WHERE pa.Product_Item_ID_Assembly = pi.Product_Item_ID
    ) THEN 1 ELSE 0 END            AS is_assembly
FROM tblProductItem pi
JOIN tblDefProducts pd ON pd.product_id = pi.product_id
LEFT JOIN tblDefProductPrice pp
       ON pp.product_item_id = pi.Product_Item_ID
      AND ((pp.start_date < GETDATE() AND pp.end_date IS NULL)
        OR  (pp.start_date < GETDATE() AND pp.end_date > GETDATE()))
LEFT JOIN tblShopProductInventory inv
       ON inv.product_item_id = pi.Product_Item_ID AND inv.shop_id = @shopId
WHERE isnull(pd.status, 1) = 1
  AND (  (@barcode IS NOT NULL AND (pi.CustomerSKUCode  = @barcode
                                 OR pi.CustomerSKUCode2 = @barcode))
      OR (@code    IS NOT NULL AND pd.product_code = @code))";

            return Run(sql, p =>
            {
                p.AddWithValue("@shopId",  shopId);
                p.AddWithValue("@barcode", string.IsNullOrWhiteSpace(barcode) ? (object)DBNull.Value : barcode);
                p.AddWithValue("@code",    string.IsNullOrWhiteSpace(code)    ? (object)DBNull.Value : code);
            });
        }

        // Mirrors Candela's uiCtrlProductCode.SetSelectedProductInfo scan lookup:
        //   1st priority: product_code match (Candela DataView filter [Product Code] = @q, CI)
        //   2nd priority: CustomerSKUCode 1–5 match (Candela [Customer SKU Code] 1–5 filter)
        // SQL Server's default CI_AS collation makes both lookups case-insensitive, exactly
        // matching the VB.NET DataTable.CaseSensitive = false (default) behaviour in Candela.
        private List<Dictionary<string, object>> ScanProductByQuery(int shopId, string q)
        {
            const string sql = @"
SELECT TOP 1
    pi.Product_Item_ID                           AS product_item_id,
    CASE WHEN li.isassortmentenabled = 1
         THEN pd.product_code + '-' + sz.field_code + '-' + cl.field_code
         ELSE pd.product_code END                AS product_code,
    pd.item_name,
    pd.line_item_id,
    isnull(li.field_name, '')                    AS line_item,
    isnull(pi.CustomerSKUCode,  '')              AS barcode,
    isnull(pi.CustomerSKUCode2, '')              AS barcode2,
    isnull(pi.CustomerSKUCode3, '')              AS barcode3,
    isnull(pi.CustomerSKUCode4, '')              AS barcode4,
    isnull(pi.CustomerSKUCode5, '')              AS barcode5,
    isnull(pp.product_price, 0)                 AS price,
    isnull(pd.vat, 0)                           AS vat,
    isnull(pd.vat_type, '')                     AS vat_type,
    isnull(pd.NotForDiscount, 0)                AS not_for_discount,
    isnull(pd.Tax_At_Retail_Price, 0)           AS tax_at_retail_price,
    isnull(pd.Sale_Tax, 0)                      AS sale_tax,
    isnull(pd.custom_discount, 0)               AS custom_discount,
    isnull(pd.Basic_Designed, 1)                AS basic_designed,
    isnull(inv.quantity, 0)                     AS stock_qty,
    CASE WHEN blk.Block_Product_Id IS NOT NULL THEN 1 ELSE 0 END AS is_blocked_for_sale,
    CASE WHEN isnull(pd.product_life_type, 0) > 0 THEN 1 ELSE 0 END AS is_control_drug,
    CASE WHEN EXISTS (
        SELECT 1 FROM tblDefProductAssembly pa
        WHERE pa.Product_Item_ID_Assembly = pi.Product_Item_ID
    ) THEN 1 ELSE 0 END                         AS is_assembly,
    pd.entereddate,
    pd.editeddate
FROM tblProductItem pi
JOIN tblDefProducts pd      ON pd.product_id       = pi.product_id
JOIN tblDefSizes sz         ON sz.size_id          = pi.size_id          AND sz.line_item_id  = pd.line_item_id
JOIN tblDefCombinitions cl  ON cl.combinition_id   = pi.combinition_id   AND cl.line_item_id  = pd.line_item_id
JOIN tblDefLineItems li     ON li.line_item_id     = pd.line_item_id
LEFT JOIN tblDefProductPrice pp
       ON pp.product_item_id = pi.Product_Item_ID
      AND ((pp.start_date < GETDATE() AND pp.end_date IS NULL)
        OR  (pp.start_date < GETDATE() AND pp.end_date > GETDATE()))
LEFT JOIN tblShopProductInventory inv
       ON inv.product_item_id = pi.Product_Item_ID AND inv.shop_id = @shopId
LEFT JOIN tblBlockPrdctsForSale blk
       ON blk.product_item_id = pi.Product_Item_ID AND blk.shopID = @shopId
WHERE isnull(pd.status, 1) = 1
  AND (
    CASE WHEN li.isassortmentenabled = 1
         THEN pd.product_code + '-' + sz.field_code + '-' + cl.field_code
         ELSE pd.product_code END = @q
    OR pi.CustomerSKUCode  = @q
    OR pi.CustomerSKUCode2 = @q
    OR pi.CustomerSKUCode3 = @q
    OR pi.CustomerSKUCode4 = @q
    OR pi.CustomerSKUCode5 = @q
  )
ORDER BY
    CASE WHEN CASE WHEN li.isassortmentenabled = 1
                   THEN pd.product_code + '-' + sz.field_code + '-' + cl.field_code
                   ELSE pd.product_code END = @q THEN 0 ELSE 1 END";

            return Run(sql, p =>
            {
                p.AddWithValue("@shopId", shopId);
                p.AddWithValue("@q", q);
            });
        }

        private List<Dictionary<string, object>> QueryCustomers(int shopId, DateTime? since, string q = null)
        {
            // Returns customers registered at this shop.
            // ?since= for delta sync (IndexedDB initial/incremental load).
            // ?q=     for live search fallback when the app gets a miss in IndexedDB.
            //         Matches member_name, phone_no, or mobile_no — TOP 50 for UX speed.
            // Joins member type for discount_pct and customer_disc_type used in the sale screen.
            bool isSearch = !string.IsNullOrWhiteSpace(q);

            string sql = @"
SELECT" + (isSearch ? " TOP 50" : "") + @"
    m.member_id,
    m.member_name,
    isnull(m.phone_Res,    '')  AS phone,
    isnull(m.phone_Mobile, '')  AS mobile,
    isnull(m.email,        '')  AS email,
    isnull(m.credit_limit, 0)  AS credit_limit,
    isnull(m.allow_credit, 0)  AS allow_credit,
    isnull(m.member_type_id, 0) AS member_type_id,
    isnull(mt.discount_percentage, 0)  AS discount_pct,
    m.entereddate,
    m.editeddate,
    isnull((SELECT SUM(s.NT_amount) FROM tblSales s
            WHERE s.member_id = m.member_id AND s.isCreditSale = 1 AND s.shop_id = @shopId), 0)
    - isnull((SELECT SUM(r.amount) FROM tblMemberReceipts r
              WHERE r.member_id = m.member_id AND r.shop_id = @shopId), 0)
    AS credit_outstanding
FROM tblMemberInfo m
LEFT JOIN tblDefMemberTypes mt ON mt.member_type_id = m.member_type_id
WHERE m.shop_id = @shopId";

            if (since.HasValue)
                sql += " AND (m.entereddate >= @since OR m.editeddate >= @since)";

            if (isSearch)
                sql += @"
  AND (m.member_name  LIKE @q
    OR m.phone_Res    LIKE @q
    OR m.phone_Mobile LIKE @q)";

            return Run(sql, p =>
            {
                p.AddWithValue("@shopId", shopId);
                if (since.HasValue) p.AddWithValue("@since", since.Value);
                if (isSearch)       p.AddWithValue("@q", "%" + q.Trim() + "%");
            });
        }

        private List<Dictionary<string, object>> QueryEmployees(int shopId, DateTime? since)
        {
            // tblDefShopEmployees — salesperson list for the salesperson assign modal.
            // Actual column names: field_name (display name) and field_Code (code).
            // No isActive column — filter by IsSalesperson when set.
            // Delta columns: EnteredDate / EditedDate.
            const string sql = @"
SELECT
    e.shop_employee_id,
    isnull(e.field_name,  '') AS employee_name,
    isnull(e.field_Code,  '') AS employee_code,
    e.shop_id,
    e.EnteredDate,
    e.EditedDate
FROM tblDefShopEmployees e
WHERE e.shop_id = @shopId";

            const string delta = " AND (e.EnteredDate >= @since OR e.EditedDate >= @since)";
            return Run(sql + (since.HasValue ? delta : ""),
                p => { p.AddWithValue("@shopId", shopId);
                       if (since.HasValue) p.AddWithValue("@since", since.Value); });
        }

        private List<Dictionary<string, object>> QueryCreditCards(DateTime? since)
        {
            // tblDefCreditCards — global, not shop-scoped.
            // Real columns: credit_card_id, field_name, EnteredDate (no isActive, no editeddate).
            const string sql = @"
SELECT
    credit_card_id,
    isnull(field_name, '') AS credit_card_name,
    EnteredDate
FROM tblDefCreditCards";

            const string delta = " WHERE EnteredDate >= @since";
            return Run(sql + (since.HasValue ? delta : "") + " ORDER BY sort_order",
                p => { if (since.HasValue) p.AddWithValue("@since", since.Value); });
        }

        private List<Dictionary<string, object>> QueryMemberTypes(DateTime? since)
        {
            // tblDefMemberTypes — customer tier definitions.
            // customer_disc_type drives which discount branch applies in /quote.
            const string sql = @"
SELECT
    mt.member_type_id,
    isnull(mt.field_name, '')           AS type_name,
    isnull(mt.discount_percentage, 0)   AS discount_percentage,
    isnull(mt.IsEmployeeDiscOn, 0)      AS is_employee_disc_on,
    isnull(mt.QtyLimit, 0)             AS qty_limit,
    isnull(mt.DurationMonths, 1)       AS duration_months,
    mt.EnteredDate,
    mt.editeddate
FROM tblDefMemberTypes mt";

            const string delta = " WHERE (mt.entereddate >= @since OR mt.editeddate >= @since)";
            return Run(sql + (since.HasValue ? delta : ""),
                p => { if (since.HasValue) p.AddWithValue("@since", since.Value); });
        }

        private List<Dictionary<string, object>> QueryPaymentMethods()
        {
            // Mobile payment providers — read from tblRCMSConfiguration.
            // Returns one row per configured provider with its enabled flag.
            // App hides the Mobile tab when all providers are disabled.
            const string sql = @"
SELECT config_name AS provider, config_value AS value
FROM   tblRCMSConfiguration
WHERE  config_name IN ('FonePayEnabled', 'AlifPayEnabled',
                       'FonePayMerchantId', 'AlifPayMerchantId',
                       '543PayEnabled', '543PayMerchantId')";

            return Run(sql, _ => { });
        }

        private List<Dictionary<string, object>> QueryReturnReasons(DateTime? since)
        {
            // tblDefReturnReasons — reason dropdown on the Return screen.
            const string sql = @"
SELECT
    r.return_reason_id,
    isnull(r.return_reason, '') AS reason_name,
    r.entereddate,
    r.editeddate
FROM tblDefReturnReasons r
WHERE isnull(r.isActive, 1) = 1";

            const string delta = " AND (r.entereddate >= @since OR r.editeddate >= @since)";
            return Run(sql + (since.HasValue ? delta : ""),
                p => { if (since.HasValue) p.AddWithValue("@since", since.Value); });
        }

        private List<Dictionary<string, object>> QueryConfig(int shopId, DateTime? since)
        {
            // Merges tblShopConfiguration (shop-specific) and tblRCMSConfiguration (global).
            // Shop config overrides RCMS when the same key exists in both.
            // Both tables are small — always return all rows.
            // The ?since= param is accepted for API consistency but config has no reliable
            // timestamp column, so we always return the full set (acceptable — config is tiny).
            const string sql = @"
SELECT config_name AS config_key, config_value, 'shop' AS source
FROM   tblShopConfiguration
WHERE  shop_id = @shopId

UNION ALL

SELECT config_name AS config_key, config_value, 'rcms' AS source
FROM   tblRCMSConfiguration";

            return Run(sql, p => p.AddWithValue("@shopId", shopId));
        }

        private List<Dictionary<string, object>> QueryLineItems()
        {
            // Product departments / categories from tblDefLineItems.
            // Excludes service line items (non-product departments).
            // Ordered by sort_order so category tabs appear in the same order as Candela.
            const string sql = @"
SELECT
    li.line_item_id,
    isnull(li.field_name, '') AS field_name,
    isnull(li.field_code, '') AS field_code,
    isnull(li.sort_order, 0)  AS sort_order
FROM tblDefLineItems li
WHERE isnull(li.IsServiceLineItem, 0) = 0
ORDER BY li.sort_order, li.field_name";

            return Run(sql, _ => { });
        }

        private List<Dictionary<string, object>> QueryBlockedProducts(int shopId)
        {
            const string sql = @"
SELECT product_item_id
FROM   tblBlockPrdctsForSale
WHERE  shopID = @shopId";
            return Run(sql, p => p.AddWithValue("@shopId", shopId));
        }

        private List<Dictionary<string, object>> QueryStrProducts(string strNo, int shopId)
        {
            // Mirrors SaleAndReturnDAL.funGetStrProducts — frmSaleAndReturn.vb:22651
            // Loads product + quantity from a Stock Transfer Request by STR number.
            // Alternate product codes (CustomerSKUCode) are accepted same as Candela.
            const string sql = @"
SELECT
    sli.product_item_id,
    pd.item_name,
    pd.product_code,
    isnull(pi.CustomerSKUCode, '') AS barcode,
    sli.qty,
    isnull(pp.product_price, 0)   AS price
FROM tblSTR s
JOIN tblSTRLineItems sli ON sli.STR_id = s.STR_id
JOIN tblProductItem pi   ON pi.Product_Item_ID = sli.product_item_id
JOIN tblDefProducts pd   ON pd.product_id = pi.product_id
LEFT JOIN tblDefProductPrice pp
       ON pp.product_item_id = sli.product_item_id
      AND ((pp.start_date < GETDATE() AND pp.end_date IS NULL)
        OR  (pp.start_date < GETDATE() AND pp.end_date > GETDATE()))
WHERE s.STR_no = @strNo
  AND s.to_shop_id = @shopId";

            return Run(sql, p =>
            {
                p.AddWithValue("@strNo",  strNo);
                p.AddWithValue("@shopId", shopId);
            });
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Shared helpers
        // ─────────────────────────────────────────────────────────────────────────

        private List<Dictionary<string, object>> QueryAssemblyItems(int productItemId, int shopId)
        {
            const string sql = @"
SELECT
    pa.Product_Item_ID_Part              AS product_item_id,
    pd.Product_number                    AS product_code,
    pd.item_name,
    CAST(pa.Quantity AS FLOAT)           AS quantity,
    ISNULL(pp.product_price, 0)          AS retail_price,
    ISNULL(inv.quantity, 0)              AS stock_qty
FROM tblDefProductAssembly pa
INNER JOIN tblProductItem  pi ON pi.Product_Item_ID = pa.Product_Item_ID_Part
INNER JOIN tblDefProducts  pd ON pd.product_id      = pi.product_id
LEFT JOIN tblDefProductPrice pp
       ON pp.product_item_id = pa.Product_Item_ID_Part
      AND ((pp.start_date < GETDATE() AND pp.end_date IS NULL)
        OR  (pp.start_date < GETDATE() AND pp.end_date > GETDATE()))
LEFT JOIN tblShopProductInventory inv
       ON inv.product_item_id = pa.Product_Item_ID_Part
      AND inv.shop_id = @shopId
WHERE pa.Product_Item_ID_Assembly = @productItemId
ORDER BY pd.item_name";

            return Run(sql, p =>
            {
                p.AddWithValue("@productItemId", productItemId);
                p.AddWithValue("@shopId",        shopId);
            });
        }

        private static DateTime? ParseSince(string since)
        {
            if (!string.IsNullOrEmpty(since) && DateTime.TryParse(since, out DateTime d))
                return d;
            return null;
        }

        private List<Dictionary<string, object>> Run(string sql, Action<SqlParameterCollection> bind)
        {
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                bind(cmd.Parameters);

                var list = new List<Dictionary<string, object>>();
                using (var dt = new DataTable())
                {
                    new SqlDataAdapter(cmd).Fill(dt);
                    foreach (DataRow row in dt.Rows)
                    {
                        var dict = new Dictionary<string, object>();
                        foreach (DataColumn col in dt.Columns)
                            dict[col.ColumnName] = row[col] == DBNull.Value ? null : row[col];
                        list.Add(dict);
                    }
                }
                return list;
            }
        }

        // GET api/masters/shops
        // Returns all active shops — used to populate the shop dropdown in the cross-shop
        // return modal so the cashier can specify which shop's invoice they are returning.
        [HttpGet, Route("shops")]
        public HttpResponseMessage GetShops()
        {
            CandelaBootstrap.PrepareRequest();
            try
            {
                var list = new List<Dictionary<string, object>>();
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var cmd = new SqlCommand(
                        "SELECT shop_id, shop_name, isnull(shop_code,'') AS shop_code " +
                        "FROM tblDefShops " +
                        "ORDER BY shop_name", con);
                    using (var dt = new DataTable())
                    {
                        new SqlDataAdapter(cmd).Fill(dt);
                        foreach (DataRow row in dt.Rows)
                        {
                            var d = new Dictionary<string, object>();
                            foreach (DataColumn col in dt.Columns)
                                d[col.ColumnName] = row[col] == DBNull.Value ? null : row[col];
                            list.Add(d);
                        }
                    }
                }
                return Ok(list);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // GET api/masters/departments
        // Returns shop departments from tblDefShopDepartments — populates the dept dropdown
        // in the Patient / Prescription Info modal (mirrors frmCustomerEmployee dept combobox).
        [HttpGet, Route("departments")]
        public HttpResponseMessage GetDepartments()
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];
            try
            {
                var list = new List<Dictionary<string, object>>();
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var cmd = new SqlCommand(
                        "SELECT shop_department_id, field_name " +
                        "FROM tblDefShopDepartments " +
                        "WHERE shop_id = @shopId " +
                        "ORDER BY field_name", con);
                    cmd.Parameters.AddWithValue("@shopId", shopId);
                    using (var dt = new DataTable())
                    {
                        new SqlDataAdapter(cmd).Fill(dt);
                        foreach (DataRow row in dt.Rows)
                        {
                            var d = new Dictionary<string, object>();
                            foreach (DataColumn col in dt.Columns)
                                d[col.ColumnName] = row[col] == DBNull.Value ? null : row[col];
                            list.Add(d);
                        }
                    }
                }
                return Ok(list);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // GET /api/masters/adjustment-reasons
        // TblDefAdjustmentReason is global (no shop_id) — AdjustmentReasonDAL.vb:GetAll
        // Shown when ShowAdjustmentReason=True and a non-zero adjustment is entered
        // frmSaleAndReturn.vb:43826-43838 (EnterAdjustmentReason_Click)
        [HttpGet]
        [Route("adjustment-reasons")]
        public HttpResponseMessage GetAdjustmentReasons()
        {
            try
            {
                var list = new List<Dictionary<string, object>>();
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var cmd = new SqlCommand(
                        "SELECT ReasonID, ReasonDescription, ISNULL(sort_order,0) AS sort_order " +
                        "FROM TblDefAdjustmentReason " +
                        "ORDER BY ISNULL(sort_order,0), ReasonID", con);
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                            list.Add(new Dictionary<string, object>
                            {
                                ["reason_id"]          = rd["ReasonID"],
                                ["reason_description"] = rd["ReasonDescription"],
                                ["sort_order"]         = rd["sort_order"],
                            });
                    }
                }
                return Ok(list);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // GET /api/masters/return-reasons
        // TblDefReturnReasons is global (no shop_id) — SaleReasonDAL.vb:20-46
        // Shown per return line when EnforceSaleReturnReason=True
        // frmSaleAndReturn.vb:6522 (per-row reason gate before Save)
        [HttpGet]
        [Route("return-reasons")]
        public HttpResponseMessage GetReturnReasons()
        {
            try
            {
                var list = new List<Dictionary<string, object>>();
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var cmd = new SqlCommand(
                        "SELECT ReasonID, ReasonDescription, ISNULL(sort_order,0) AS sort_order " +
                        "FROM TblDefReturnReasons " +
                        "ORDER BY ISNULL(sort_order,0), ReasonID", con);
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                            list.Add(new Dictionary<string, object>
                            {
                                ["reason_id"]          = rd["ReasonID"],
                                ["reason_description"] = rd["ReasonDescription"],
                                ["sort_order"]         = rd["sort_order"],
                            });
                    }
                }
                return Ok(list);
            }
            catch (Exception ex) { return Err(ex); }
        }

        private HttpResponseMessage Ok(List<Dictionary<string, object>> rows) =>
            Request.CreateResponse(HttpStatusCode.OK,
                new { success = true, count = rows.Count, data = rows });

        private HttpResponseMessage Err(Exception ex) =>
            Request.CreateResponse(HttpStatusCode.InternalServerError,
                new { error = "An internal error occurred." });
    }
}
