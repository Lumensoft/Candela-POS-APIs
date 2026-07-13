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
    [RoutePrefix("api/products")]
    public class ProductsController : ApiController
    {
        // GET api/products/{id}/alternates
        // Returns substitute items when a product has alternates configured.
        // Auto-opens the Alternate Item modal on scan.
        // tblDefProductAlternates — design doc line 119.
        [HttpGet, Route("{id:int}/alternates")]
        public HttpResponseMessage GetAlternates(int id)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];
            try
            {
                var rows = QueryAlternates(id, shopId);
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // GET api/products/{id}/batches
        // Returns available batches and expiry dates for a product.
        // Shown in the Batch/Expiry modal on long-press of a cart line.
        // design doc line 121.
        [HttpGet, Route("{id:int}/batches")]
        public HttpResponseMessage GetBatches(int id)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];
            try
            {
                var rows = QueryBatches(id, shopId);
                return Ok(rows);
            }
            catch (Exception ex) { return Err(ex); }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // SQL implementations
        // ─────────────────────────────────────────────────────────────────────────

        private List<Dictionary<string, object>> QueryAlternates(int productItemId, int shopId)
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

            return Run(sql, p =>
            {
                p.AddWithValue("@id",     productItemId);
                p.AddWithValue("@shopId", shopId);
            });
        }

        private List<Dictionary<string, object>> QueryBatches(int productItemId, int shopId)
        {
            // Batch/expiry list for a product at this shop.
            // Shown on long-press → Batch modal so cashier can pick a specific batch.
            // Only returns batches with qty > 0 (no point showing empty/expired stock).
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

            return Run(sql, p =>
            {
                p.AddWithValue("@id",     productItemId);
                p.AddWithValue("@shopId", shopId);
            });
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Shared helpers
        // ─────────────────────────────────────────────────────────────────────────

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

        private HttpResponseMessage Ok(List<Dictionary<string, object>> rows) =>
            Request.CreateResponse(HttpStatusCode.OK,
                new { success = true, count = rows.Count, data = rows });

        private HttpResponseMessage Err(Exception ex) =>
            Request.CreateResponse(HttpStatusCode.InternalServerError,
                new { error = "An internal error occurred." });
    }
}
