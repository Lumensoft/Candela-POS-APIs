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
        // GET api/masters/products
        // GET api/masters/products?since=2026-01-01T00:00:00
        [HttpGet, Route("products")]
        public HttpResponseMessage GetProducts([FromUri] string since = null)
        {
            CandelaBootstrap.PrepareRequest();

            int shopId = (int)Request.Properties["shop_id"];

            try
            {
                DateTime? sinceDate = null;
                if (!string.IsNullOrEmpty(since) && DateTime.TryParse(since, out DateTime parsed))
                    sinceDate = parsed;

                var rows = QueryProducts(shopId, sinceDate);
                return Request.CreateResponse(HttpStatusCode.OK,
                    new { success = true, count = rows.Count, data = rows });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }
        }

        private List<Dictionary<string, object>> QueryProducts(int shopId, DateTime? since)
        {
            const string sql = @"
SELECT
    pi.Product_Item_ID,
    CASE WHEN li.isassortmentenabled = 1
         THEN pd.product_code + '-' + sz.field_code + '-' + cl.field_code
         ELSE pd.product_code END AS product_code,
    pd.item_name,
    isnull(pi.CustomerSKUCode,  '') AS barcode,
    isnull(pi.CustomerSKUCode2, '') AS barcode2,
    isnull(pp.product_price, 0) AS price,
    isnull(pd.vat, 0)           AS vat,
    isnull(pd.vat_type, '')     AS vat_type,
    isnull(pd.NotForDiscount, 0)    AS not_for_discount,
    isnull(pd.Tax_At_Retail_Price, 0) AS tax_at_retail_price,
    isnull(pd.Sale_Tax, 0)      AS sale_tax,
    isnull(pd.custom_discount, 0)   AS custom_discount,
    isnull(pd.Basic_Designed, 1)    AS basic_designed,
    isnull(inv.quantity, 0)         AS stock_qty,
    pd.entereddate,
    pd.editeddate
FROM tblProductItem pi
JOIN tblDefProducts pd   ON pd.product_id = pi.product_id
JOIN tblDefSizes sz       ON sz.size_id = pi.size_id         AND sz.line_item_id = pd.line_item_id
JOIN tblDefCombinitions cl ON cl.combinition_id = pi.combinition_id AND cl.line_item_id = pd.line_item_id
JOIN tblDefLineItems li   ON li.line_item_id = pd.line_item_id
LEFT JOIN tblDefProductPrice pp ON pp.product_item_id = pi.Product_Item_ID
    AND ((pp.start_date < GETDATE() AND pp.end_date IS NULL)
      OR  (pp.start_date < GETDATE() AND pp.end_date > GETDATE()))
LEFT JOIN tblShopProductInventory inv ON inv.product_item_id = pi.Product_Item_ID AND inv.shop_id = @shopId
WHERE isnull(pd.status, 1) = 1";

            const string deltaClause = " AND (pd.entereddate >= @since OR pd.editeddate >= @since)";

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql + (since.HasValue ? deltaClause : ""), con);
                cmd.Parameters.AddWithValue("@shopId", shopId);
                if (since.HasValue)
                    cmd.Parameters.AddWithValue("@since", since.Value);

                var list = new List<Dictionary<string, object>>();
                using (var dt = new DataTable())
                {
                    using (var adapter = new SqlDataAdapter(cmd))
                        adapter.Fill(dt);

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
    }
}
