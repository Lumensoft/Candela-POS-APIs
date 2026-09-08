using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using CandelaPOS.Infrastructure;
using CandelaPOS.Models;
using DAL;
using Model;
using static Utility.Utility;

namespace CandelaPOS.Controllers
{
    [RoutePrefix("api/holds")]
    public class HoldsController : ApiController
    {
        // POST api/holds — park a cart
        [HttpPost, Route("")]
        public HttpResponseMessage ParkSale([FromBody] SaleRequest req)
        {
            if (req == null || req.Items == null || req.Items.Count == 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "items cannot be empty" });

            CandelaBootstrap.PrepareRequest();

            int    userId   = (int)   Request.Properties["user_id"];
            int    shopId   = (int)   Request.Properties["shop_id"];
            string posCode  = (string)Request.Properties["pos_code"];
            string userName = (string)Request.Properties["user_name"];

            try
            {
                var sale = BuildHoldModel(req, userId, shopId, posCode, userName);

                var dal = new SaleAndReturnDAL();
                bool ok = dal.AddToHold(sale, EnumActions.Save);

                if (!ok)
                    return Request.CreateResponse(HttpStatusCode.InternalServerError,
                        new { error = "AddToHold() returned false" });

                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<object>.Ok(new { hold_id = sale.HoldingSaleID }));
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // GET api/holds — list all parked carts for this shop
        [HttpGet, Route("")]
        public HttpResponseMessage GetHolds()
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];

            try
            {
                var holds = QueryHolds(shopId);
                return Request.CreateResponse(HttpStatusCode.OK,
                    new { success = true, count = holds.Count, data = holds });
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // DELETE api/holds/{id} — discard a parked cart
        [HttpDelete, Route("{id:int}")]
        public HttpResponseMessage DeleteHold(int id)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];

            try
            {
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    using (var tx = con.BeginTransaction())
                    {
                        var delLines = new SqlCommand(
                            "DELETE FROM tblSalesLineItemsHolding WHERE sale_id = @id AND shop_id = @sid",
                            con, tx);
                        delLines.Parameters.AddWithValue("@id",  id);
                        delLines.Parameters.AddWithValue("@sid", shopId);
                        delLines.ExecuteNonQuery();

                        var delHold = new SqlCommand(
                            "DELETE FROM tblSalesHolding WHERE Sale_id = @id AND shop_id = @sid",
                            con, tx);
                        delHold.Parameters.AddWithValue("@id",  id);
                        delHold.Parameters.AddWithValue("@sid", shopId);
                        int rows = delHold.ExecuteNonQuery();

                        if (rows == 0)
                        {
                            tx.Rollback();
                            return Request.CreateResponse(HttpStatusCode.NotFound,
                                new { error = "Hold not found" });
                        }

                        tx.Commit();
                    }
                }

                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<object>.Ok(new { deleted = true }));
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        private List<object> QueryHolds(int shopId)
        {
            const string headerSql = @"
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

            const string linesSql = @"
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

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();

                var headers = new List<Dictionary<string, object>>();
                var hCmd = new SqlCommand(headerSql, con);
                hCmd.Parameters.AddWithValue("@shopId", shopId);
                using (var dt = new DataTable())
                {
                    new SqlDataAdapter(hCmd).Fill(dt);
                    foreach (DataRow row in dt.Rows)
                    {
                        var d = new Dictionary<string, object>();
                        foreach (DataColumn col in dt.Columns)
                            d[col.ColumnName] = row[col] == DBNull.Value ? null : row[col];
                        headers.Add(d);
                    }
                }

                var lineMap = new Dictionary<int, List<Dictionary<string, object>>>();
                var lCmd = new SqlCommand(linesSql, con);
                lCmd.Parameters.AddWithValue("@shopId", shopId);
                using (var dt = new DataTable())
                {
                    new SqlDataAdapter(lCmd).Fill(dt);
                    foreach (DataRow row in dt.Rows)
                    {
                        int hId = Convert.ToInt32(row["sale_id"]);
                        if (!lineMap.ContainsKey(hId))
                            lineMap[hId] = new List<Dictionary<string, object>>();
                        var d = new Dictionary<string, object>();
                        foreach (DataColumn col in dt.Columns)
                            d[col.ColumnName] = row[col] == DBNull.Value ? null : row[col];
                        lineMap[hId].Add(d);
                    }
                }

                var result = new List<object>();
                foreach (var h in headers)
                {
                    int hId = Convert.ToInt32(h["hold_id"]);
                    h["items"] = lineMap.ContainsKey(hId)
                        ? lineMap[hId]
                        : new List<Dictionary<string, object>>();
                    result.Add(h);
                }
                return result;
            }
        }

        private SaleAndReturn BuildHoldModel(SaleRequest req, int userId, int shopId, string posCode, string userName)
        {
            var sale = new SaleAndReturn();

            sale.Shop.ShopID      = shopId;
            sale.UserInfo.UserID  = userId;
            sale.UserInfo.POSCode = posCode;
            sale.SaleDateTime     = req.SaleDate == default ? DateTime.Now : req.SaleDate;

            var pt = (req.PaymentType ?? "").ToLower();
            if      (pt == "card")   sale.TransactionType = EnumSaleTransactionType.CreditCard;
            else if (pt == "credit") sale.TransactionType = EnumSaleTransactionType.Credit;
            else if (pt == "split")  sale.TransactionType = EnumSaleTransactionType.Mixed;
            else                     sale.TransactionType = EnumSaleTransactionType.Cash;

            sale.GrossTotal        = req.GrossTotal;
            sale.NetTotal          = req.NetTotal;
            sale.CustomerDiscount  = req.CustomerDiscount;
            sale.MarketingDiscount = req.MarketingDiscount;
            sale.VATAmount         = req.VatAmount;
            sale.AdjustmentAmount  = req.AdjustmentAmount;
            sale.CashAmount        = req.CashAmount;
            sale.CreditCardAmount  = req.CardAmount;
            sale.CreditAmount      = (decimal)req.CreditAmount;
            sale.BalanceAmount     = 0;
            sale.Comments          = req.Comments ?? "";

            if (req.CustomerId > 0)
                sale.Customer.MemberID = req.CustomerId;
            sale.Customer.MemberName = "";

            sale.CreditCard.CreditCardID = req.CreditCardId;
            sale.IsMultiplePyaments      = false;
            sale.SaleReturningNo         = 0;
            sale.HoldingSaleID           = req.HoldingSaleId;

            sale.CustomerEmployee.EmployeeName     = "";
            sale.CustomerEmployee.RegisterationNo  = "";
            sale.CustomerEmployee.Department.ShopDepartmentID   = 0;
            sale.CustomerEmployee.Department.ShopDepartmentName = "";

            sale.ActivityLog.LogGroup    = "POS API";
            sale.ActivityLog.ScreenTitle = "Hold";
            sale.ActivityLog.UserID      = userId;
            sale.ActivityLog.ShopID      = shopId;

            sale.ListOfSaleItems = new List<SaleAndReturnItems>();
            foreach (var item in req.Items)
            {
                var line = new SaleAndReturnItems(0, item.ProductItemId, item.Quantity,
                                                  item.UnitRate, item.TaggedPrice);
                line.ProductBatchNo             = "";
                line.VATValue                   = item.VatValue;
                line.VatFactor                  = item.VatFactor;
                line.VatType                    = item.VatType ?? "";
                line.PriceIncludeVat            = item.PriceIncludeVat;
                line.ProductUnitDiscount        = item.UnitDiscount;
                line.ProductDiscountID          = item.DiscountId;
                line.CustomerDiscountPerUnit     = item.CustomerDiscountPerUnit;
                line.MarketingDiscountOnProduct  = item.MarketingDiscount;
                line.LoyalityCashDiscount       = item.LoyaltyCashDiscount;
                line.AdditionalTaxpercent       = item.AdditionalTaxPercent;
                line.AdditionalTax              = item.AdditionalTax;
                line.DiscCategory               = item.DiscCategory ?? "";
                line.DiscountFromTagPrice       = false;
                line.LoyalityEarnedPoints       = 0;
                line.NestedItemId               = 0;
                line.PackSize                   = 0;
                line.Con_Factor                 = 1.0;
                line.Con_Unit                   = "";
                line.AvgCost                    = 0.0;
                line.VatChargedPerUnit          = 0.0;
                line.VatOnRetailPrice           = 0.0;
                line.PriceForDiscount           = item.UnitRate;
                line.PriceAfterDiscount         = item.NetAmount / (item.Quantity == 0 ? 1 : item.Quantity);
                line.Employee.Shop.ShopID       = shopId;
                sale.ListOfSaleItems.Add(line);
            }

            return sale;
        }
    }
}
