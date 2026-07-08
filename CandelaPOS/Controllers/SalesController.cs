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
    [RoutePrefix("api/sales")]
    public class SalesController : ApiController
    {
        // GET api/sales?page=1&page_size=20&q=ali&from=2026-01-01&to=2026-12-31&invoice_no=1234
        // Paginated invoice search for the invoice search modal.
        // Returns summary rows from tblSales joined to customer/employee info.
        // Mirrors the query from SaleAndReturnDAL.vb:6802 (simplified for POS use).
        [HttpGet, Route("")]
        public HttpResponseMessage GetSales(
            [FromUri] int    page      = 1,
            [FromUri] int    page_size = 20,
            [FromUri] string q         = null,
            [FromUri] string from      = null,
            [FromUri] string to        = null,
            [FromUri] string invoice_no = null)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];

            if (page      < 1) page      = 1;
            if (page_size < 1 || page_size > 100) page_size = 20;

            try
            {
                const string baseSql = @"
SELECT
    s.sale_id,
    s.invoice_no,
    s.sale_date,
    isnull(m.member_name, s.cust_name)  AS customer_name,
    m.member_id                          AS customer_id,
    isnull(m.phone_mobile, '')           AS customer_phone,
    isnull(e.field_name, '')             AS salesperson_name,
    s.GT_amount                          AS gross_total,
    s.NT_amount                          AS net_total,
    s.Mark_Discount                      AS marketing_discount,
    s.vat,
    s.Cash_amt,
    s.Card_amt,
    isnull(s.isCreditSale, 0)           AS is_credit_sale,
    isnull(s.isMixSale, 0)              AS is_mix_sale,
    isnull(s.is_return_item, 0)         AS is_return,
    s.SaleReturningNo                    AS return_of_sale_id,
    isnull(s.invoice_Type, '')           AS invoice_type,
    isnull(s.cust_bal, 0)               AS balance
FROM tblSales s
LEFT JOIN tblMemberInfo m
    ON  m.member_id = s.member_id
    AND m.shop_id   = s.memberShopID
LEFT JOIN tblDefShopEmployees e
    ON  e.shop_employee_id = s.employee_id
WHERE s.shop_id = @shopId";

                var where  = new System.Text.StringBuilder(baseSql);
                var cmd    = new SqlCommand();
                cmd.Parameters.AddWithValue("@shopId", shopId);

                if (!string.IsNullOrWhiteSpace(invoice_no))
                {
                    where.Append(" AND s.invoice_no = @invoiceNo");
                    cmd.Parameters.AddWithValue("@invoiceNo", invoice_no);
                }
                else if (!string.IsNullOrWhiteSpace(q))
                {
                    where.Append(" AND (m.member_name LIKE @q OR s.cust_name LIKE @q)");
                    cmd.Parameters.AddWithValue("@q", "%" + q + "%");
                }

                if (!string.IsNullOrEmpty(from) && DateTime.TryParse(from, out DateTime fromDt))
                {
                    where.Append(" AND s.sale_date >= @fromDt");
                    cmd.Parameters.AddWithValue("@fromDt", fromDt.Date);
                }

                if (!string.IsNullOrEmpty(to) && DateTime.TryParse(to, out DateTime toDt))
                {
                    where.Append(" AND s.sale_date < @toDt");
                    cmd.Parameters.AddWithValue("@toDt", toDt.Date.AddDays(1));
                }

                int offset = (page - 1) * page_size;
                string finalSql = where.ToString()
                    + " ORDER BY s.sale_id DESC"
                    + $" OFFSET {offset} ROWS FETCH NEXT {page_size} ROWS ONLY";

                // Count query (same filters, no pagination)
                string countSql = "SELECT COUNT(*) FROM (" + where.ToString() + ") AS cnt";

                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    cmd.Connection = con;

                    // Total count
                    var countCmd = cmd.Clone() as SqlCommand;
                    countCmd.CommandText = countSql;
                    int total = Convert.ToInt32(countCmd.ExecuteScalar());

                    // Page data
                    cmd.CommandText = finalSql;
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

                    return Request.CreateResponse(HttpStatusCode.OK, new
                    {
                        success    = true,
                        total,
                        page,
                        page_size,
                        count      = list.Count,
                        data       = list
                    });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }
        }

        // DELETE api/sales/{id}
        // Void an invoice — zeroes amounts, sets IsVoided=1, reverses inventory.
        // Supervisor-only in Candela. Wraps SaleAndReturnDAL.VoidSale() at line 15240.
        [HttpDelete, Route("{id:int}")]
        public HttpResponseMessage VoidSale(int id)
        {
            CandelaBootstrap.PrepareRequest();

            int    userId  = (int)   Request.Properties["user_id"];
            int    shopId  = (int)   Request.Properties["shop_id"];
            string posCode = (string)Request.Properties["pos_code"];

            try
            {
                // Verify sale exists and belongs to this shop
                using (var chkCon = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    chkCon.Open();
                    var chkCmd = new SqlCommand(
                        "SELECT isnull(IsVoided, 0) FROM tblSales " +
                        "WHERE sale_id = @sid AND shop_id = @shid", chkCon);
                    chkCmd.Parameters.AddWithValue("@sid",  id);
                    chkCmd.Parameters.AddWithValue("@shid", shopId);
                    var chkResult = chkCmd.ExecuteScalar();

                    if (chkResult == null)
                        return Request.CreateResponse(HttpStatusCode.NotFound,
                            new { error = "Sale not found" });

                    if (Convert.ToBoolean(chkResult))
                        return Request.CreateResponse((HttpStatusCode)409,
                            new { error = "Sale is already voided" });
                }

                // Build minimal model for VoidSale — only SaleID, Shop, and audit required
                var model = new SaleAndReturn();
                model.SaleID          = id;
                model.Shop.ShopID     = shopId;
                model.UserInfo.UserID = userId;
                model.UserInfo.POSCode = posCode;
                model.Customer.MemberName = "";
                model.ActivityLog.LogGroup    = "POS API";
                model.ActivityLog.ScreenTitle = "Void";
                model.ActivityLog.UserID      = userId;
                model.ActivityLog.ShopID      = shopId;

                // VoidSale() receives an already-open transaction (it does NOT open its own).
                // SaleAndReturnDAL.vb:15240
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    using (var trans = con.BeginTransaction())
                    {
                        try
                        {
                            string physAuditMsg = "";
                            var dal = new SaleAndReturnDAL();
                            bool ok = dal.VoidSale(model, EnumActions.Delete, ref physAuditMsg, trans);

                            if (!ok)
                            {
                                trans.Rollback();
                                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                                    new { error = "VoidSale() returned false" });
                            }

                            trans.Commit();

                            return Request.CreateResponse(HttpStatusCode.OK,
                                ApiResponse<object>.Ok(new
                                {
                                    voided  = true,
                                    sale_id = id,
                                    message = string.IsNullOrWhiteSpace(physAuditMsg) ? null : physAuditMsg
                                }));
                        }
                        catch
                        {
                            trans.Rollback();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }
        }

        // POST api/sales
        [HttpPost, Route("")]
        public HttpResponseMessage PostSale([FromBody] SaleRequest req)
        {
            if (req == null)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "Request body is required" });

            if (string.IsNullOrEmpty(req.ClientTxnGuid))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "client_txn_guid is required" });

            if (req.Items == null || req.Items.Count == 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "items cannot be empty" });

            CandelaBootstrap.PrepareRequest();

            int    userId   = (int)   Request.Properties["user_id"];
            int    shopId   = (int)   Request.Properties["shop_id"];
            string posCode  = (string)Request.Properties["pos_code"];
            string userName = (string)Request.Properties["user_name"];

            SaleAndReturn sale = null;
            try
            {
                // Idempotency — return existing sale_id if this guid was already saved
                int existingSaleId = GetExistingSaleId(req.ClientTxnGuid, shopId);
                if (existingSaleId > 0)
                    return Request.CreateResponse(HttpStatusCode.OK,
                        ApiResponse<object>.Ok(new { sale_id = existingSaleId, idempotent = true }));

                // Build SaleAndReturn model from the DTO
                sale = BuildModel(req, userId, shopId, posCode, userName);

                // Credit sale validation: check allow_credit flag and credit limit headroom.
                // In Candela this is a UI check in IsValidate() before dal.Add().
                // frmSaleAndReturn.vb:14365-14370, 6344-6356
                if (req.CustomerId > 0 && (req.PaymentType ?? "").ToLower() == "credit")
                {
                    string creditError = ValidateCreditSale(req.CustomerId, shopId,
                        req.CreditAmount > 0 ? req.CreditAmount : req.NetTotal);
                    if (creditError != null)
                        return Request.CreateResponse((HttpStatusCode)422, new { error = creditError });
                }

                // Gap 6: mark coupon Used before finalising the sale.
                // CheckCouponStatus() opens its own transaction and sets Status='Used'.
                // Returns false when coupon is not ACTIVE (already used or not found).
                // frmSaleAndReturn.vb:10623, SaleAndReturnDAL.vb:14715
                if (!string.IsNullOrWhiteSpace(req.CouponNo))
                {
                    bool couponOk = new SaleAndReturnDAL().CheckCouponStatus(req.CouponNo, shopId);
                    if (!couponOk)
                        return Request.CreateResponse(HttpStatusCode.Conflict,
                            new { error = $"Coupon '{req.CouponNo}' is no longer active." });
                }

                // Call Candela DAL — same path as the desktop
                var dal = new SaleAndReturnDAL();
                string auditMsg = "";
                bool ok = dal.Add(sale, EnumActions.Save, ref auditMsg);

                if (!ok)
                    return Request.CreateResponse(HttpStatusCode.InternalServerError,
                        new { error = "SaleAndReturnDAL.Add() returned false. " + auditMsg });

                RecordIdempotencyKey(req.ClientTxnGuid, sale.SaleID, shopId);

                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<object>.Ok(new { sale_id = sale.SaleID }));
            }
            catch (Exception ex)
            {
                // dal.Add() can throw a secondary error after tblSales/tblSalesLineItems
                // already committed. If SaleID was assigned the sale was saved successfully.
                if (sale != null && sale.SaleID > 0)
                {
                    RecordIdempotencyKey(req.ClientTxnGuid, sale.SaleID, shopId);
                    return Request.CreateResponse(HttpStatusCode.OK,
                        ApiResponse<object>.Ok(new { sale_id = sale.SaleID }));
                }
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }
        }

        private int GetExistingSaleId(string clientGuid, int shopId)
        {
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(
                    "SELECT TOP 1 sale_id FROM tblPOSIdempotency " +
                    "WHERE client_txn_guid = @guid AND shop_id = @sid", con);
                cmd.Parameters.AddWithValue("@guid", clientGuid);
                cmd.Parameters.AddWithValue("@sid",  shopId);
                var result = cmd.ExecuteScalar();
                return result != null ? Convert.ToInt32(result) : 0;
            }
        }

        private void RecordIdempotencyKey(string clientGuid, int saleId, int shopId)
        {
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(
                    "INSERT INTO tblPOSIdempotency (client_txn_guid, sale_id, shop_id, created_at) " +
                    "VALUES (@guid, @sid_sale, @sid_shop, GETDATE())", con);
                cmd.Parameters.AddWithValue("@guid",     clientGuid);
                cmd.Parameters.AddWithValue("@sid_sale", saleId);
                cmd.Parameters.AddWithValue("@sid_shop", shopId);
                cmd.ExecuteNonQuery();
            }
        }

        // Returns an error message if the credit sale should be rejected, null if OK.
        // Mirrors frmSaleAndReturn.vb:6344-6356 and 14365-14370.
        // outstanding = total credit sales billed - total receipts received
        private string ValidateCreditSale(int customerId, int shopId, double creditAmount)
        {
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();

                // Check allow_credit flag (frmSaleAndReturn.vb:19518)
                var flagCmd = new SqlCommand(
                    "SELECT TOP 1 allow_credit, credit_limit " +
                    "FROM tblMemberInfo " +
                    "WHERE shop_id = @sid AND member_id = @mid", con);
                flagCmd.Parameters.AddWithValue("@sid", shopId);
                flagCmd.Parameters.AddWithValue("@mid", customerId);

                bool allowCredit = false;
                decimal creditLimit = 0m;
                using (var rdr = flagCmd.ExecuteReader())
                {
                    if (rdr.Read())
                    {
                        allowCredit  = rdr["allow_credit"] != DBNull.Value && Convert.ToBoolean(rdr["allow_credit"]);
                        creditLimit  = rdr["credit_limit"] != DBNull.Value ? Convert.ToDecimal(rdr["credit_limit"]) : 0m;
                    }
                    else
                    {
                        // Customer not found in member info — treat as credit not allowed
                        return "Customer does not have credit facility.";
                    }
                }

                if (!allowCredit)
                    return "Customer does not have credit facility.";

                // Outstanding balance = credit sales billed - receipts received
                // (CustomerReceiptDAL.vb:754 confirms tblMemberReceipts.amount column)
                var balCmd = new SqlCommand(
                    "SELECT " +
                    "  ISNULL((SELECT SUM(NT_amount) FROM tblSales " +
                    "           WHERE member_id = @mid AND isCreditSale = 1 AND shop_id = @sid " +
                    "             AND is_return_item = 0), 0) " +
                    "- ISNULL((SELECT SUM(amount) FROM tblMemberReceipts " +
                    "           WHERE member_id = @mid AND shop_id = @sid), 0) " +
                    "AS outstanding", con);
                balCmd.Parameters.AddWithValue("@mid", customerId);
                balCmd.Parameters.AddWithValue("@sid", shopId);

                decimal outstanding = Convert.ToDecimal(balCmd.ExecuteScalar());
                decimal newTotal    = outstanding + (decimal)creditAmount;

                if (newTotal > creditLimit)
                    return $"Credit limit exceeded. Limit: {creditLimit:F2}, " +
                           $"Outstanding: {outstanding:F2}, " +
                           $"This sale: {creditAmount:F2}, " +
                           $"Would total: {newTotal:F2}.";
            }
            return null;
        }

        private SaleAndReturn BuildModel(SaleRequest req, int userId, int shopId, string posCode, string userName)
        {
            var sale = new SaleAndReturn();

            // Header
            sale.Shop.ShopID         = shopId;
            sale.UserInfo.UserID     = userId;
            sale.UserInfo.POSCode    = posCode;
            sale.SaleDateTime        = req.SaleDate == default ? DateTime.Now : req.SaleDate;

            // Payment type
            var pt = (req.PaymentType ?? "").ToLower();
            if      (pt == "card")     sale.TransactionType = EnumSaleTransactionType.CreditCard;
            else if (pt == "credit")   sale.TransactionType = EnumSaleTransactionType.Credit;
            else if (pt == "split")    sale.TransactionType = EnumSaleTransactionType.Mixed;
            else                       sale.TransactionType = EnumSaleTransactionType.Cash;

            // Totals
            sale.GrossTotal          = req.GrossTotal;
            sale.NetTotal            = req.NetTotal;
            sale.CustomerDiscount    = req.CustomerDiscount;
            sale.MarketingDiscount   = req.MarketingDiscount;
            sale.VATAmount           = req.VatAmount;
            sale.AdjustmentAmount    = req.AdjustmentAmount;
            sale.CashAmount          = req.CashAmount;
            sale.CreditCardAmount    = req.CardAmount;
            sale.CreditAmount        = (decimal)req.CreditAmount;
            sale.GiftCardAmount      = (decimal)req.GiftCardAmount;
            sale.GiftCardNo          = req.GiftCardNo ?? "";
            sale.BalanceAmount       = 0;

            // Customer
            if (req.CustomerId > 0)
                sale.Customer.MemberID = req.CustomerId;
            // MemberName stored in tblSales.Cust_name (DAL line 3846); null-fix: INSERT calls .Replace()
            sale.Customer.MemberName = req.WalkInName ?? "";

            // Salesperson — tblSales.SalesPerson via tblDefShopEmployees (DAL line 8024)
            sale.Employee.ShopEmployeeID = req.SalespersonId;

            // Card
            sale.CreditCard.CreditCardID = req.CreditCardId;

            // Misc
            // DAL line 3776: IsMultiplePyaments gates _CashAmount derivation and isMixSale flag.
            // Must be true whenever more than one tender type carries a non-zero amount.
            int tenderCount = (req.CashAmount    > 0 ? 1 : 0)
                            + (req.CardAmount    > 0 ? 1 : 0)
                            + (req.CreditAmount  > 0 ? 1 : 0)
                            + (req.GiftCardAmount > 0 ? 1 : 0);
            sale.IsMultiplePyaments  = pt == "split" || tenderCount > 1;
            sale.SaleReturningNo     = 0;
            sale.HoldingSaleID       = req.HoldingSaleId; // 0 = new sale; >0 = finalize a parked hold (DAL deletes the hold row)
            sale.Comments            = req.Comments ?? "";
            sale.MemberPoints        = new MemberEarnedPoints();

            // Employee (required sub-objects)
            sale.CustomerEmployee.EmployeeName     = "";
            sale.CustomerEmployee.RegisterationNo  = "";
            sale.CustomerEmployee.Department.ShopDepartmentID   = 0;
            sale.CustomerEmployee.Department.ShopDepartmentName = "";

            // Audit log
            sale.ActivityLog.LogGroup    = "POS API";
            sale.ActivityLog.ScreenTitle = "Sale";
            sale.ActivityLog.UserID      = userId;
            sale.ActivityLog.ShopID      = shopId;

            // Line items
            sale.ListOfSaleItems = new List<SaleAndReturnItems>();
            foreach (var item in req.Items)
            {
                var line = new SaleAndReturnItems(0, item.ProductItemId, item.Quantity,
                                                  item.UnitRate, item.TaggedPrice);
                line.ProductBatchNo              = item.BatchNo ?? "";  // FIFO/FEFO batch tracking (CR #8125)
                line.VATValue                    = item.VatValue;
                line.VatFactor                   = item.VatFactor;
                line.VatType                     = item.VatType ?? "";
                line.PriceIncludeVat             = item.PriceIncludeVat;
                line.ProductUnitDiscount         = item.UnitDiscount;
                line.ProductDiscountID           = item.DiscountId;
                line.CustomerDiscountPerUnit      = item.CustomerDiscountPerUnit;
                line.MarketingDiscountOnProduct   = item.MarketingDiscount;
                line.LoyalityCashDiscount        = item.LoyaltyCashDiscount;
                line.AdditionalTaxpercent        = item.AdditionalTaxPercent;
                line.AdditionalTax               = item.AdditionalTax;
                line.DiscCategory                = item.DiscCategory ?? "";
                line.DiscountFromTagPrice        = item.DiscountFromTagPrice;
                line.LoyalityEarnedPoints        = 0;
                line.NestedItemId                = item.NestedItemId;
                line.PackSize                    = item.PackSize;
                // Con_Factor=0 means unit sale; keep 1.0 so DAL inventory math is correct
                line.Con_Factor                  = item.ConFactor > 0 ? item.ConFactor : 1.0;
                line.Con_Unit                    = "";
                line.AvgCost                     = 0.0;
                line.VatChargedPerUnit           = 0.0;
                line.VatOnRetailPrice            = 0.0;
                line.PriceForDiscount            = item.UnitRate;
                line.PriceAfterDiscount          = item.NetAmount / (item.Quantity == 0 ? 1 : item.Quantity);
                line.Employee.Shop.ShopID        = shopId;
                sale.ListOfSaleItems.Add(line);
            }

            return sale;
        }
    }
}
