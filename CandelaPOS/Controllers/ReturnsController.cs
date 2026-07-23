using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
    [RoutePrefix("api/returns")]
    public class ReturnsController : ApiController
    {
        // Per-invoice application lock: ensures ValidateReturnQuantities and dal.Add()
        // execute as one atomic unit, preventing two concurrent requests from both passing
        // the qty check for the same invoice and then both committing.
        private static readonly ConcurrentDictionary<int, object> _invoiceLocks =
            new ConcurrentDictionary<int, object>();

        // POST api/returns/validate
        // Check if an invoice is returnable and return its items
        [HttpPost, Route("validate")]
        public HttpResponseMessage Validate([FromBody] ValidateReturnRequest req)
        {
            if (req == null || req.InvoiceNo <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "invoice_no is required" });

            CandelaBootstrap.PrepareRequest();
            int shopId       = (int)Request.Properties["shop_id"];
            int sourceShopId = req.SourceShopId > 0 ? req.SourceShopId : shopId;

            try
            {
                var dal = new SaleAndReturnDAL();

                if (!dal.IsValidInvoiceForReturn(sourceShopId, req.InvoiceNo))
                    return Request.CreateResponse((HttpStatusCode)422,
                        new { error = "Invoice not found, already fully returned, or belongs to a different shop" });

                string customerCode = dal.getCustomerAgainstInvoice(sourceShopId, req.InvoiceNo);

                var sale   = QuerySaleHeader(sourceShopId, req.InvoiceNo);
                var items  = QuerySaleItems(sourceShopId, req.InvoiceNo);

                return Request.CreateResponse(HttpStatusCode.OK, new
                {
                    success       = true,
                    customer_code = customerCode ?? "",
                    sale,
                    items
                });
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // POST api/returns
        // Process a return — items must have negative quantities
        [HttpPost, Route("")]
        public HttpResponseMessage PostReturn([FromBody] ReturnRequest req)
        {
            if (req == null)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "Request body is required" });

            if (string.IsNullOrEmpty(req.ClientTxnGuid))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "client_txn_guid is required" });

            if (req.ReturningInvoiceNo < 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "returning_invoice_no must be 0 (free return) or a positive invoice number" });

            if (req.Items == null || req.Items.Count == 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "items cannot be empty" });

            CandelaBootstrap.PrepareRequest();

            int    userId       = (int)   Request.Properties["user_id"];
            int    shopId       = (int)   Request.Properties["shop_id"];
            string posCode      = (string)Request.Properties["pos_code"];
            string userName     = (string)Request.Properties["user_name"];
            int    sourceShopId = req.SourceShopId > 0 ? req.SourceShopId : shopId;

            SaleAndReturn sale = null;
            try
            {
                // Idempotency — atomically claim this GUID before doing any work.
                if (!TryClaimIdempotencySlot(req.ClientTxnGuid, shopId))
                {
                    System.Threading.Thread.Sleep(100);
                    int existing = GetExistingReturnId(req.ClientTxnGuid, shopId);
                    if (existing > 0)
                        return Request.CreateResponse(HttpStatusCode.OK,
                            ApiResponse<object>.Ok(new { sale_id = existing, idempotent = true }));

                    return Request.CreateResponse(HttpStatusCode.Conflict,
                        new { error = "Duplicate request in progress. Please retry." });
                }

                var dal = new SaleAndReturnDAL();

                // ── C-section validations ────────────────────────────────────────────────────
                var cCfg = CandelaBootstrap.GetRCMSConfig();

                // C1: EnforceSaleReturnReason — all non-exchange return lines need a reason.
                // Mirrors frmSaleAndReturn.vb:6522 (per-row reason check before Save).
                if (CfgIs(cCfg, "EnforceSaleReturnReason", "True") && req.Items != null)
                {
                    var noReason = req.Items
                        .Where(i => !i.IsExchangeItem
                                    && (i.ReturnReasonId == null || i.ReturnReasonId == 0))
                        .ToList();
                    if (noReason.Count > 0)
                    {
                        DeleteIdempotencySlot(req.ClientTxnGuid, shopId);
                        return Request.CreateResponse((HttpStatusCode)422,
                            new { error = "A return reason is required for all returned items." });
                    }
                }

                // C-credit-1: Block credit return without a customer.
                // Mirrors frmSaleAndReturn.vb:6344-6356 — credit has no account to post to
                // without a member_id. Candela grays out the option at the UI level.
                if ((req.PaymentType ?? "").ToLower() == "credit" && req.CustomerId == 0)
                {
                    DeleteIdempotencySlot(req.ClientTxnGuid, shopId);
                    return Request.CreateResponse((HttpStatusCode)422,
                        new { error = "A customer must be selected to process a credit return." });
                }

                // C-credit-2: Validate allow_credit flag when customer is present.
                // Mirrors SalesController.ValidateCreditSale / frmSaleAndReturn.vb:19518.
                if ((req.PaymentType ?? "").ToLower() == "credit" && req.CustomerId > 0)
                {
                    using (var creditCon = new SqlConnection(CandelaBootstrap.ConnectionString))
                    {
                        creditCon.Open();
                        var creditCmd = new SqlCommand(
                            "SELECT TOP 1 isnull(allow_credit,0) FROM tblMemberInfo " +
                            "WHERE shop_id = @sid AND member_id = @mid", creditCon);
                        creditCmd.Parameters.AddWithValue("@sid", shopId);
                        creditCmd.Parameters.AddWithValue("@mid", req.CustomerId);
                        var result = creditCmd.ExecuteScalar();
                        bool allowCredit = result != null && result != DBNull.Value && Convert.ToBoolean(result);
                        if (!allowCredit)
                        {
                            DeleteIdempotencySlot(req.ClientTxnGuid, shopId);
                            return Request.CreateResponse((HttpStatusCode)422,
                                new { error = "This customer is not enabled for credit transactions." });
                        }
                    }
                }
                // ── end C-section ────────────────────────────────────────────────────────────

                // Free return (no invoice): skip invoice existence and qty-cap checks.
                // Invoice-backed return: validate the invoice and guard against over-return.
                if (req.ReturningInvoiceNo > 0)
                {
                    if (!dal.IsValidInvoiceForReturn(sourceShopId, req.ReturningInvoiceNo))
                        return Request.CreateResponse((HttpStatusCode)422,
                            new { error = "Invoice is not valid for return" });

                    // ── C2/C3/C5: invoice-backed validations (before lock) ────────────────────
                    // Placed before the lock to avoid holding it during extra DB queries.
                    {
                        // C2: EnterReturnDays — block returns beyond the allowed window.
                        // Formula: EnterReturnDays + DATEDIFF(DAY, GETDATE(), sale_date) < 0 → blocked.
                        // Mirrors SaleAndReturnDAL.vb:18187 and frmSaleAndReturn.vb:8229-8238.
                        string retDaysStr;
                        int retDays = 0;
                        if (cCfg.TryGetValue("EnterReturnDays", out retDaysStr) && !string.IsNullOrWhiteSpace(retDaysStr))
                            int.TryParse(retDaysStr, out retDays);

                        if (retDays != 0)
                        {
                            using (var daysCon = new SqlConnection(CandelaBootstrap.ConnectionString))
                            {
                                daysCon.Open();
                                var daysCmd = new SqlCommand(
                                    "SELECT DATEDIFF(DAY, GETDATE(), sale_date) " +
                                    "FROM tblSales WHERE sale_id = @sid AND shop_id = @shid", daysCon);
                                daysCmd.Parameters.AddWithValue("@sid",  req.ReturningInvoiceNo);
                                daysCmd.Parameters.AddWithValue("@shid", sourceShopId);
                                var daysObj = daysCmd.ExecuteScalar();
                                if (daysObj != null && daysObj != DBNull.Value)
                                {
                                    int daysDiff = Convert.ToInt32(daysObj);
                                    if (retDays + daysDiff < 0)
                                    {
                                        DeleteIdempotencySlot(req.ClientTxnGuid, shopId);
                                        return Request.CreateResponse((HttpStatusCode)422,
                                            new { error = "The return period for this invoice has expired." });
                                    }
                                }
                            }
                        }

                        // C3: Apply_Disc_on_Return — original invoice's customer must match.
                        // Mirrors SaleAndReturnDAL.vb:13027 and frmSaleAndReturn.vb:6849-6898.
                        if (CfgIs(cCfg, "Apply_Disc_on_Return", "True"))
                        {
                            using (var custCon = new SqlConnection(CandelaBootstrap.ConnectionString))
                            {
                                custCon.Open();
                                var custCmd = new SqlCommand(
                                    "SELECT isnull(member_id,0), isnull(membershopID,0) " +
                                    "FROM tblSales WHERE sale_id = @sid AND shop_id = @shid", custCon);
                                custCmd.Parameters.AddWithValue("@sid",  req.ReturningInvoiceNo);
                                custCmd.Parameters.AddWithValue("@shid", sourceShopId);
                                using (var rd = custCmd.ExecuteReader())
                                {
                                    if (rd.Read())
                                    {
                                        int origMemberId     = Convert.ToInt32(rd[0]);
                                        int origMemberShopId = Convert.ToInt32(rd[1]);
                                        if (origMemberId > 0 && origMemberShopId > 0)
                                        {
                                            if (req.CustomerId == 0)
                                            {
                                                DeleteIdempotencySlot(req.ClientTxnGuid, shopId);
                                                return Request.CreateResponse((HttpStatusCode)422,
                                                    new { error = "The original invoice was sold to a customer. Please select the same customer to process this return." });
                                            }
                                            if (req.CustomerId != origMemberId)
                                            {
                                                DeleteIdempotencySlot(req.ClientTxnGuid, shopId);
                                                return Request.CreateResponse((HttpStatusCode)422,
                                                    new { error = "Customer mismatch. The return must be processed for the same customer as the original invoice." });
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // C5: IgnoreReturnInvoicePaymentMode — refund tender must match original.
                        // credit_sale=1 → must refund via credit; cash (card_id=0) → must refund cash.
                        // Card sale (card_id>0) → any tender accepted.
                        // Mirrors SaleAndReturnDAL.vb:13042-43 and frmSaleAndReturn.vb:6948-7056.
                        if (!CfgIs(cCfg, "IgnoreReturnInvoicePaymentMode", "True"))
                        {
                            using (var pmCon = new SqlConnection(CandelaBootstrap.ConnectionString))
                            {
                                pmCon.Open();
                                var pmCmd = new SqlCommand(
                                    "SELECT isnull(iscreditsale,0), isnull(credit_card_id,0) " +
                                    "FROM tblSales WHERE sale_id = @sid AND shop_id = @shid", pmCon);
                                pmCmd.Parameters.AddWithValue("@sid",  req.ReturningInvoiceNo);
                                pmCmd.Parameters.AddWithValue("@shid", sourceShopId);
                                using (var rd = pmCmd.ExecuteReader())
                                {
                                    if (rd.Read())
                                    {
                                        bool isCreditSale = Convert.ToBoolean(rd[0]);
                                        int  creditCardId = Convert.ToInt32(rd[1]);
                                        if (isCreditSale)
                                        {
                                            if (req.CreditAmount <= 0)
                                            {
                                                DeleteIdempotencySlot(req.ClientTxnGuid, shopId);
                                                return Request.CreateResponse((HttpStatusCode)422,
                                                    new { error = "The original invoice was a credit sale. The return must be processed as credit." });
                                            }
                                        }
                                        else if (creditCardId == 0)
                                        {
                                            if (req.CashAmount <= 0)
                                            {
                                                DeleteIdempotencySlot(req.ClientTxnGuid, shopId);
                                                return Request.CreateResponse((HttpStatusCode)422,
                                                    new { error = "The original invoice was a cash sale. The return must be refunded as cash." });
                                            }
                                        }
                                        // Card sale: any tender is accepted.
                                    }
                                }
                            }
                        }
                    }
                    // ── end C2/C3/C5 ─────────────────────────────────────────────────────────

                    // Per-invoice lock: ensures ValidateReturnQuantities and dal.Add() are
                    // atomic so two concurrent requests for the same invoice cannot both pass
                    // the qty check and then both commit an over-return.
                    var invoiceLock = _invoiceLocks.GetOrAdd(req.ReturningInvoiceNo, _ => new object());
                    lock (invoiceLock)
                    {
                        string overReturnError = ValidateReturnQuantities(sourceShopId, req.ReturningInvoiceNo, req.Items);
                        if (overReturnError != null)
                            return Request.CreateResponse((HttpStatusCode)422, new { error = overReturnError });

                        sale = BuildReturnModel(req, userId, shopId, posCode, userName);

                        DataTable creditNoteTable = null;
                        if (req.CreditNoteGiftCardId > 0)
                            creditNoteTable = BuildCreditNoteTable(req, shopId, posCode);

                        string auditMsg = "";
                        bool ok = dal.Add(sale, EnumActions.Save, ref auditMsg, "", creditNoteTable);

                        if (!ok)
                            return Request.CreateResponse(HttpStatusCode.InternalServerError,
                                new { error = "SaleAndReturnDAL.Add() returned false. " + auditMsg });

                        UpdateIdempotencySlot(req.ClientTxnGuid, sale.SaleID, shopId);

                        return Request.CreateResponse(HttpStatusCode.OK,
                            ApiResponse<object>.Ok(new { sale_id = sale.SaleID }));
                    }
                }
                else
                {
                    // Free return/exchange (Show_popup_on_return = FALSE) — no invoice to lock against.
                    sale = BuildReturnModel(req, userId, shopId, posCode, userName);

                    DataTable creditNoteTable = null;
                    if (req.CreditNoteGiftCardId > 0)
                        creditNoteTable = BuildCreditNoteTable(req, shopId, posCode);

                    string auditMsg = "";
                    bool ok = dal.Add(sale, EnumActions.Save, ref auditMsg, "", creditNoteTable);

                    if (!ok)
                        return Request.CreateResponse(HttpStatusCode.InternalServerError,
                            new { error = "SaleAndReturnDAL.Add() returned false. " + auditMsg });

                    UpdateIdempotencySlot(req.ClientTxnGuid, sale.SaleID, shopId);

                    return Request.CreateResponse(HttpStatusCode.OK,
                        ApiResponse<object>.Ok(new { sale_id = sale.SaleID }));
                }
            }
            catch (Exception)
            {
                // dal.Add() can throw a secondary error (e.g., BuildSQLLog truncation or loyalty
                // reversal) even after tblSales/tblSalesLineItems already committed. If SaleID
                // was assigned the return was saved — record the idempotency key and return success.
                if (sale != null && sale.SaleID > 0)
                {
                    UpdateIdempotencySlot(req.ClientTxnGuid, sale.SaleID, shopId);
                    return Request.CreateResponse(HttpStatusCode.OK,
                        ApiResponse<object>.Ok(new { sale_id = sale.SaleID }));
                }
                DeleteIdempotencySlot(req.ClientTxnGuid, shopId);
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An error occurred processing the return." });
            }
        }

        // Validates that no return line exceeds the returnable quantity for its product.
        // returnable = original_qty - already_returned_qty.
        // Returns an error message on the first violation, null if all items are valid.
        // sourceShopId = the shop that made the original sale (may differ from cashier's shop
        // on cross-shop returns). Used to query the original line items; prior returns are
        // searched across all shops (any shop may have processed an earlier partial return).
        private string ValidateReturnQuantities(int sourceShopId, int invoiceNo, List<SaleLineItem> items)
        {
            var originalQty = new Dictionary<int, double>();
            var returnedQty = new Dictionary<int, double>();

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();

                // Original sale line quantities — scoped to the source shop's invoice.
                var origCmd = new SqlCommand(
                    "SELECT Product_Item_ID, SUM(ABS(qty)) AS qty " +
                    "FROM tblSalesLineItems " +
                    "WHERE sale_id = @invoiceNo AND shop_id = @sourceShopId " +
                    "  AND ISNULL(is_return_item, 0) = 0 " +
                    "GROUP BY Product_Item_ID", con);
                origCmd.Parameters.AddWithValue("@invoiceNo",     invoiceNo);
                origCmd.Parameters.AddWithValue("@sourceShopId",  sourceShopId);
                using (var rdr = origCmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        int pid = Convert.ToInt32(rdr["Product_Item_ID"]);
                        originalQty[pid] = Convert.ToDouble(rdr["qty"]);
                    }
                }

                // Already-returned quantities across ALL shops — a cross-shop return processed
                // at Shop B would create return lines with shop_id = B, not the source shop.
                var retCmd = new SqlCommand(
                    "SELECT li.Product_Item_ID, SUM(ABS(li.qty)) AS qty " +
                    "FROM tblSalesLineItems li " +
                    "JOIN tblSales s ON s.sale_id = li.sale_id " +
                    "WHERE s.SaleReturningNo = @invoiceNo " +
                    "  AND ISNULL(li.is_return_item, 0) = 1 " +
                    "GROUP BY li.Product_Item_ID", con);
                retCmd.Parameters.AddWithValue("@invoiceNo", invoiceNo);
                using (var rdr = retCmd.ExecuteReader())
                {
                    while (rdr.Read())
                    {
                        int pid = Convert.ToInt32(rdr["Product_Item_ID"]);
                        returnedQty[pid] = Convert.ToDouble(rdr["qty"]);
                    }
                }
            }

            foreach (var item in items)
            {
                if (item.IsExchangeItem) continue;
                double requestedQty = Math.Abs(item.Quantity);
                if (requestedQty <= 0) continue;

                double original  = originalQty.ContainsKey(item.ProductItemId)
                    ? originalQty[item.ProductItemId] : 0;
                double alreadyRet = returnedQty.ContainsKey(item.ProductItemId)
                    ? returnedQty[item.ProductItemId] : 0;
                double available = original - alreadyRet;

                if (requestedQty > available)
                    return $"Item {item.ProductItemId}: cannot return {requestedQty} unit(s). " +
                           $"Original: {original}, already returned: {alreadyRet}, " +
                           $"available to return: {available}.";
            }

            return null;
        }

        private SaleAndReturn BuildReturnModel(ReturnRequest req, int userId, int shopId, string posCode, string userName)
        {
            var sale = new SaleAndReturn();

            sale.Shop.ShopID      = shopId;
            sale.UserInfo.UserID  = userId;
            sale.UserInfo.POSCode = posCode;
            sale.SaleDateTime     = DateTime.Now;

            // Returns use negative totals
            var pt = (req.PaymentType ?? "").ToLower();
            if      (pt == "card")   sale.TransactionType = EnumSaleTransactionType.CreditCard;
            else if (pt == "credit") sale.TransactionType = EnumSaleTransactionType.Credit;
            else if (pt == "split")  sale.TransactionType = EnumSaleTransactionType.Mixed;
            else                     sale.TransactionType = EnumSaleTransactionType.Cash;

            sale.GrossTotal        = req.GrossTotal;      // negative
            sale.NetTotal          = req.NetTotal;         // negative
            sale.CustomerDiscount  = req.CustomerDiscount;
            sale.MarketingDiscount = req.MarketingDiscount;
            sale.VATAmount         = req.VatAmount;
            sale.AdjustmentAmount  = req.AdjustmentAmount;
            sale.CashAmount        = req.CashAmount;
            sale.CreditCardAmount  = req.CardAmount;
            sale.CreditAmount      = (decimal)req.CreditAmount;
            sale.BalanceAmount     = 0;
            sale.Comments          = req.Comments ?? "";

            // Link back to the original invoice and its source shop.
            // Mirrors frmSaleAndReturn.vb:8792-8801 (FillModel SaleReturningShopId assignment).
            int srcShop = req.SourceShopId > 0 ? req.SourceShopId : shopId;
            sale.SaleReturningNo      = req.ReturningInvoiceNo;
            sale.SaleReturningShopId  = req.ReturningInvoiceNo > 0 ? srcShop : 0;

            if (req.CustomerId > 0)
                sale.Customer.MemberID = req.CustomerId;
            sale.Customer.MemberName = "";

            sale.CreditCard.CreditCardID = req.CreditCardId;
            // Same logic as SalesController: IsMultiplePyaments gates _CashAmount derivation
            // in the DAL (line 3776). True whenever more than one tender carries a non-zero amount.
            int retTenderCount = (req.CashAmount   > 0 ? 1 : 0)
                               + (req.CardAmount   > 0 ? 1 : 0)
                               + (req.CreditAmount > 0 ? 1 : 0)
                               + (req.CreditNoteGiftCardId > 0 ? 1 : 0);
            sale.IsMultiplePyaments      = (req.PaymentType ?? "").ToLower() == "split" || retTenderCount > 1;
            sale.HoldingSaleID           = 0;

            sale.CustomerEmployee.EmployeeName     = "";
            sale.CustomerEmployee.RegisterationNo  = "";
            sale.CustomerEmployee.Department.ShopDepartmentID   = 0;
            sale.CustomerEmployee.Department.ShopDepartmentName = "";

            sale.ActivityLog.LogGroup    = "POS API";
            sale.ActivityLog.ScreenTitle = "Return";
            sale.ActivityLog.UserID      = userId;
            sale.ActivityLog.ShopID      = shopId;

            sale.MemberPoints = new MemberEarnedPoints();

            sale.ListOfSaleItems = new List<SaleAndReturnItems>();
            foreach (var item in req.Items)
            {
                // Exchange items (is_exchange_item=true) keep a positive qty — inventory out, no return.
                // Return items are negated: DAL records them as is_return_item=1 with negative qty.
                double qty = item.IsExchangeItem
                    ?  Math.Abs(item.Quantity)
                    : (item.Quantity > 0 ? -item.Quantity : item.Quantity);

                var line = new SaleAndReturnItems(0, item.ProductItemId, qty,
                                                  item.UnitRate, item.TaggedPrice);
                line.ProductBatchNo             = item.BatchNo ?? "";
                // Written to tblSalesLineItems.ReasonID / ReturnReason (SaleAndReturnDAL.vb:4885-4886)
                line.ReasonID                   = item.ReturnReasonId         ?? 0;
                line.ReasonDescription          = item.ReturnReasonDescription ?? "";
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
                line.DiscountFromTagPrice       = item.DiscountFromTagPrice;
                line.LoyalityEarnedPoints       = 0;
                line.NestedItemId               = item.NestedItemId;
                line.PackSize                   = item.PackSize;
                // Con_Factor=0 means unit sale; keep 1.0 so DAL inventory math is correct
                line.Con_Factor                 = item.ConFactor > 0 ? item.ConFactor : 1.0;
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

        // Builds the DataTable expected by SaleAndReturnDAL.Add() for credit-note returns.
        // Matches the schema built in frmSaleAndReturn.vb:38811-38854.
        private DataTable BuildCreditNoteTable(ReturnRequest req, int shopId, string posCode)
        {
            // Look up expiry days from the card type linked to this gift card.
            // frmSaleAndReturn.vb:38809
            int expDays = 0;
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(
                    "SELECT isnull(ExpireAfterDays,0) FROM tbldefCardType " +
                    "WHERE id = (SELECT card_type_id FROM tbldefCards WHERE id = @giftCardId)", con);
                cmd.Parameters.AddWithValue("@giftCardId", req.CreditNoteGiftCardId);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    expDays = Convert.ToInt32(result);
            }

            var dt = new DataTable();
            dt.Columns.Add("GiftCardId",  typeof(int));
            dt.Columns.Add("GiftCardno",  typeof(int));
            dt.Columns.Add("topup",       typeof(string));
            dt.Columns.Add("cashAmt",     typeof(decimal));
            dt.Columns.Add("cardAmtD",    typeof(decimal));
            dt.Columns.Add("ShopIdD",     typeof(int));
            dt.Columns.Add("posCodeD",    typeof(string));
            dt.Columns.Add("expDays",     typeof(int));
            dt.Columns.Add("MemberName",  typeof(string));
            dt.Columns.Add("PhoneMobile", typeof(string));

            // topup = absolute value of the negative net total (return amount loaded to card).
            // frmSaleAndReturn.vb:38776: Math.Round(txtNetTotal * -1, gintAmountRound)
            decimal topUpAmt = (decimal)Math.Abs(req.NetTotal);

            // cashAmt/cardAmtD are the tender amounts paid by the customer at time of refund.
            // For credit-note refunds these are typically 0 (value goes to card, not paid out).
            dt.Rows.Add(
                req.CreditNoteGiftCardId,
                req.CreditNoteGiftCardNo,
                topUpAmt.ToString(),
                0m,
                0m,
                shopId,
                posCode,
                expDays,
                req.CreditNoteMemberName ?? "",
                req.CreditNotePhone      ?? ""
            );
            return dt;
        }

        private Dictionary<string, object> QuerySaleHeader(int shopId, int invoiceNo)
        {
            const string sql = @"
SELECT
    s.sale_id, s.shop_id, s.sale_date,
    isnull(s.member_id, 0)    AS customer_id,
    isnull(s.GT_amount, 0)    AS gross_total,
    isnull(s.NT_amount, 0)    AS net_total,
    isnull(s.Mark_discount,0) AS marketing_discount,
    isnull(s.vat, 0)          AS vat_amount,
    isnull(s.adjustment_amount, 0) AS adjustment_amount,
    isnull(s.Adjustment_comments,'') AS comments,
    isnull(s.invoice_type,'') AS invoice_type,
    isnull(s.iscreditsale, 0)  AS iscreditsale,
    isnull(s.credit_card_id, 0) AS credit_card_id
FROM tblSales s
WHERE s.sale_id = @invoiceNo AND s.shop_id = @shopId";

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@invoiceNo", invoiceNo);
                cmd.Parameters.AddWithValue("@shopId",    shopId);

                using (var dt = new DataTable())
                {
                    new SqlDataAdapter(cmd).Fill(dt);
                    if (dt.Rows.Count == 0) return null;

                    var d = new Dictionary<string, object>();
                    foreach (DataColumn col in dt.Columns)
                        d[col.ColumnName] = dt.Rows[0][col] == DBNull.Value ? null : dt.Rows[0][col];
                    return d;
                }
            }
        }

        private List<Dictionary<string, object>> QuerySaleItems(int shopId, int invoiceNo)
        {
            const string sql = @"
SELECT
    li.sale_line_item_id,
    li.Product_Item_ID          AS product_item_id,
    pd.item_name,
    li.qty                      AS quantity,
    li.Unit_price,
    isnull(li.product_discount_amount, 0) AS unit_discount,
    isnull(li.mem_discount_amount, 0)     AS customer_discount_per_unit,
    isnull(li.pro_vat, 0)        AS vat_value,
    isnull(li.VatFactor, 0)      AS vat_factor,
    isnull(li.Vat_Type, '')      AS vat_type,
    isnull(li.PriceIncludeVat,0) AS price_include_vat,
    isnull(li.additional_tax_percent,0) AS additional_tax_percent,
    isnull(li.additional_tax, 0) AS additional_tax,
    isnull(li.Taged_Price, 0)    AS tagged_price,
    isnull(li.PriceAfterDiscount,0) AS price_after_discount,
    isnull(li.DiscountCategory,'')  AS disc_category,
    isnull(li.discount_ID, 0)       AS discount_id,
    isnull(li.Loyality_CashDiscount,0) AS loyalty_cash_discount,
    isnull(li.CustomerDiscount, 0)  AS customer_discount
FROM tblSalesLineItems li
JOIN tblProductItem pi   ON pi.Product_Item_ID = li.Product_Item_ID
JOIN tblDefProducts pd   ON pd.product_id = pi.product_id
WHERE li.sale_id = @invoiceNo
  AND li.shop_id = @shopId
  AND isnull(li.is_return_item, 0) = 0";

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@invoiceNo", invoiceNo);
                cmd.Parameters.AddWithValue("@shopId",    shopId);

                var list = new List<Dictionary<string, object>>();
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
                return list;
            }
        }

        private int GetExistingReturnId(string clientGuid, int shopId)
        {
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(
                    "SELECT TOP 1 sale_id FROM tblPOSIdempotency " +
                    "WHERE client_txn_guid = @guid AND shop_id = @sid AND sale_id > 0", con);
                cmd.Parameters.AddWithValue("@guid", clientGuid);
                cmd.Parameters.AddWithValue("@sid",  shopId);
                var result = cmd.ExecuteScalar();
                return result != null ? Convert.ToInt32(result) : 0;
            }
        }

        private bool TryClaimIdempotencySlot(string clientGuid, int shopId)
        {
            try
            {
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var cmd = new SqlCommand(
                        "INSERT INTO tblPOSIdempotency (client_txn_guid, sale_id, shop_id, created_at) " +
                        "SELECT @guid, 0, @sid, GETDATE() " +
                        "WHERE NOT EXISTS (SELECT 1 FROM tblPOSIdempotency " +
                        "                  WHERE client_txn_guid = @guid AND shop_id = @sid)", con);
                    cmd.Parameters.AddWithValue("@guid", clientGuid);
                    cmd.Parameters.AddWithValue("@sid",  shopId);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
            {
                return false;
            }
            catch
            {
                return true;
            }
        }

        private void UpdateIdempotencySlot(string clientGuid, int saleId, int shopId)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                    {
                        con.Open();
                        var upd = new SqlCommand(
                            "UPDATE tblPOSIdempotency SET sale_id = @saleId " +
                            "WHERE client_txn_guid = @guid AND shop_id = @sid", con);
                        upd.Parameters.AddWithValue("@saleId", saleId);
                        upd.Parameters.AddWithValue("@guid",   clientGuid);
                        upd.Parameters.AddWithValue("@sid",    shopId);
                        if (upd.ExecuteNonQuery() == 0)
                        {
                            var ins = new SqlCommand(
                                "INSERT INTO tblPOSIdempotency (client_txn_guid, sale_id, shop_id, created_at) " +
                                "VALUES (@guid, @saleId, @sid, GETDATE())", con);
                            ins.Parameters.AddWithValue("@guid",   clientGuid);
                            ins.Parameters.AddWithValue("@saleId", saleId);
                            ins.Parameters.AddWithValue("@sid",    shopId);
                            ins.ExecuteNonQuery();
                        }
                    }
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError(
                        "UpdateIdempotencySlot attempt {0}/3 failed for guid={1} saleId={2}: {3}",
                        attempt, clientGuid, saleId, ex);
                    if (attempt < 3)
                        System.Threading.Thread.Sleep(50 * attempt);
                }
            }
        }

        private void DeleteIdempotencySlot(string clientGuid, int shopId)
        {
            try
            {
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var cmd = new SqlCommand(
                        "DELETE FROM tblPOSIdempotency " +
                        "WHERE client_txn_guid = @guid AND shop_id = @sid AND sale_id = 0", con);
                    cmd.Parameters.AddWithValue("@guid", clientGuid);
                    cmd.Parameters.AddWithValue("@sid",  shopId);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError(
                    "DeleteIdempotencySlot failed for guid={0}: {1}", clientGuid, ex);
            }
        }

        // Mirrors SalesController.CfgIs — case-insensitive config value check.
        private static bool CfgIs(Dictionary<string, string> cfg, string key, string expected)
        {
            string val;
            return cfg.TryGetValue(key, out val)
                && string.Equals(val?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
