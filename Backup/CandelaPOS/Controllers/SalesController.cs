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

            int userId   = (int)Request.Properties["user_id"];
            int shopId   = (int)Request.Properties["shop_id"];
            string userName = (string)Request.Properties["user_name"];

            try
            {
                // Idempotency — return existing sale_id if this guid was already saved
                int existingSaleId = GetExistingSaleId(req.ClientTxnGuid, shopId);
                if (existingSaleId > 0)
                    return Request.CreateResponse(HttpStatusCode.OK,
                        ApiResponse<object>.Ok(new { sale_id = existingSaleId, idempotent = true }));

                // Build SaleAndReturn model from the DTO
                var sale = BuildModel(req, userId, shopId, userName);

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

        private SaleAndReturn BuildModel(SaleRequest req, int userId, int shopId, string userName)
        {
            var sale = new SaleAndReturn();

            // Header
            sale.Shop.ShopID         = shopId;
            sale.UserInfo.UserID     = userId;
            sale.UserInfo.POSCode    = "POS-TABLET";
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
            sale.BalanceAmount       = 0;

            // Customer
            if (req.CustomerId > 0)
                sale.Customer.MemberID = req.CustomerId;
            sale.Customer.MemberName = "";   // mandatory null-fix: INSERT calls .Replace() on this

            // Card
            sale.CreditCard.CreditCardID = req.CreditCardId;

            // Misc
            sale.IsMultiplePyaments  = false;
            sale.SaleReturningNo     = 0;
            sale.HoldingSaleID       = 0;
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
                line.ProductBatchNo              = "";   // mandatory null-fix: INSERT calls .Trim.Replace()
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
                line.DiscountFromTagPrice        = false;
                line.LoyalityEarnedPoints        = 0;
                line.NestedItemId                = 0;
                line.PackSize                    = 0;
                line.Con_Factor                  = 1.0;
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
