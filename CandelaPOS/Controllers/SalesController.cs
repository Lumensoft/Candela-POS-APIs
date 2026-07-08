using System;
using System.Collections.Generic;
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
