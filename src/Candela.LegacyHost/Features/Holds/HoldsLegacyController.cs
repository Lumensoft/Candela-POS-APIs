using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using DAL;
using Model;
using static Utility.Utility;
using CandelaPOS.Shared.Api;
using CandelaPOS.Shared.Errors;
using CandelaPOS.Features.Sales;   // SaleRequest / SaleLineItem

namespace CandelaPOS.Features.Holds
{
    /// <summary>
    /// The only way the .NET 10 API parks a cart. POST goes through
    /// SaleAndReturnDAL.AddToHold — the same call frmSaleAndReturn makes — so the
    /// holding-sale id is generated and the batch rows written exactly as the desktop
    /// does them.
    ///
    /// ParkSale and BuildHoldModel are copied verbatim from HoldsController; only the
    /// route prefix changed. GET and DELETE are NOT here: the .NET 10 side serves those
    /// directly from SQL (the old HoldsController already did them with plain SQL, not
    /// the DAL), so they have no reason to make this hop.
    ///
    /// The old api/holds HoldsController is left in place, unmapped, as instant rollback.
    /// </summary>
    [RoutePrefix("legacy/holds")]
    public class HoldsLegacyController : ApiController
    {
        // POST api/holds — park a cart
        [HttpPost, Route("")]
        public HttpResponseMessage ParkSale([FromBody] SaleRequest req)
        {
            if (req == null || req.Items == null || req.Items.Count == 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "items cannot be empty" });


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

                return Request.CreateResponse(HttpStatusCode.OK, new { hold_id = sale.HoldingSaleID });
            }
            catch (Exception ex)
            {
                return ApiError.Internal(Request, ex, "HoldsController.ParkSale");
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
