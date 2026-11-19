using System;
using System.Data.SqlClient;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using DAL;
using Model;
using Newtonsoft.Json;
using CandelaPOS.Shared.Data;
using CandelaPOS.Shared.Errors;

namespace CandelaPOS.Features.MissedSales
{
    /// <summary>
    /// Records a missed sale — a product a customer asked for and the till could not sell (CR 7901).
    ///
    ///   POST /legacy/missed-sales   { product_item_id, missed_qty, remarks }
    ///
    /// frmSaleAndReturn.txtProductCode_GetMissedSaleProduct (vb:18217-18245): when EnableMissedSaleRecording is TRUE, one
    /// row goes into tblMissedSale — Product_Item_ID, Missed_Qty, Entered_Date (the server's time), Shop_id, User_id,
    /// Sale_id NULL, Remarks and MissedSaleId, the next number for that shop (max + 1) — and the same INSERT is written
    /// to the SQL log (UtilityDAL.BuildSQLLog) so it replicates between the shop and head office like every other write.
    /// Values go in as parameters here (Candela builds the string by hand); the SQL-log text escapes quotes the way
    /// Candela does. shop_id / user_id come from the X-Ctx-* headers, never from the body.
    /// </summary>
    [RoutePrefix("legacy/missed-sales")]
    public class MissedSalesLegacyController : ApiController
    {
        [HttpPost, Route("")]
        public HttpResponseMessage Record([FromBody] MissedSaleBody req)
        {
            if (req == null || req.ProductItemId <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "product_item_id is required" });
            if (req.MissedQty <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "missed_qty must be greater than zero" });

            string remarks = req.Remarks ?? string.Empty;
            if (remarks.Length > 150)
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "Maximum length of remarks exceeded." });

            // The same switch the desktop checks before it records anything.
            var cfg = CandelaBootstrap.GetRCMSConfig();
            if (!cfg.TryGetValue("EnableMissedSaleRecording", out var enabled)
                || !string.Equals((enabled ?? "").Trim(), "TRUE", StringComparison.OrdinalIgnoreCase))
                return Request.CreateResponse((HttpStatusCode)422, new { error = "Missed sale recording is not enabled." });

            int userId = (int)Request.Properties["user_id"];
            int shopId = (int)Request.Properties["shop_id"];

            try
            {
                var inv = CultureInfo.InvariantCulture;
                var now = DateTime.Now;
                int missedSaleId;

                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var trans = con.BeginTransaction();
                    try
                    {
                        const string sql =
                            "DECLARE @id INT; " +
                            "SELECT @id = (ISNULL(MAX(MissedSaleId), 0) + 1) FROM tblMissedSale WHERE shop_id = @sid; " +
                            "INSERT INTO tblMissedSale (Product_Item_ID, Missed_Qty, Entered_Date, Shop_id, User_id, Sale_id, Remarks, MissedSaleId) " +
                            "VALUES (@pid, @qty, @date, @sid, @uid, NULL, @remarks, @id); " +
                            "SELECT @id;";

                        using (var cmd = new SqlCommand(sql, con, trans))
                        {
                            cmd.Parameters.AddWithValue("@pid",     req.ProductItemId);
                            cmd.Parameters.AddWithValue("@qty",     req.MissedQty);
                            cmd.Parameters.AddWithValue("@date",    now);
                            cmd.Parameters.AddWithValue("@sid",     shopId);
                            cmd.Parameters.AddWithValue("@uid",     userId);
                            cmd.Parameters.AddWithValue("@remarks", remarks);
                            missedSaleId = Convert.ToInt32(cmd.ExecuteScalar());
                        }

                        string logSql =
                            " Insert into tblmissedsale (Product_Item_ID, Missed_Qty, Entered_Date, Shop_id, User_id, Sale_id, Remarks, MissedSaleId) VALUES (" +
                            req.ProductItemId + ", " + req.MissedQty.ToString(inv) + ", '" + now.ToString("yyyy/MM/dd hh:mm:ss tt", inv) + "', " +
                            shopId + ", " + userId + ", NULL, '" + remarks.Replace("'", "''") + "', " + missedSaleId + " )";

                        var log = new ActivityLog
                        {
                            SQLType     = "Insert",
                            TableName   = "tblmissedsale",
                            SQL         = logSql,
                            ShopID      = shopId,
                            UserID      = userId,
                            ScreenTitle = "Missed Sale"
                        };
                        UtilityDAL.BuildSQLLog(log, trans);

                        trans.Commit();
                    }
                    catch
                    {
                        trans.Rollback();
                        throw;
                    }
                }

                return Request.CreateResponse(HttpStatusCode.OK, new { success = true, missed_sale_id = missedSaleId });
            }
            catch (Exception ex)
            {
                return ApiError.Internal(Request, ex, "MissedSalesController.Record");
            }
        }
    }

    public class MissedSaleBody
    {
        [JsonProperty("product_item_id")] public int    ProductItemId { get; set; }
        [JsonProperty("missed_qty")]      public double MissedQty     { get; set; }
        [JsonProperty("remarks")]         public string Remarks       { get; set; }
    }
}
