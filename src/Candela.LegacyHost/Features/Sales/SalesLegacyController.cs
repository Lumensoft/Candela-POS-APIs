using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using DAL;
using Model;
using static Utility.Utility;
using CandelaPOS.Shared.Data;
using CandelaPOS.Shared.Api;
using CandelaPOS.Shared.Errors;

namespace CandelaPOS.Features.Sales
{
    // The four SalesController writes that go through SaleAndReturnDAL, moved behind
    // /legacy/sales so Candela.Api can run them on .NET Framework. PostSale, UpdateSale,
    // VoidSale, HardDeleteSale and every helper (BuildModel, ValidateCreditSale,
    // idempotency, InsertSalesCashTender, HandleDalException ...) are copied verbatim from
    // SalesController; only the route prefix changed. GetSales / GetSale are NOT here —
    // Candela.Api serves those from SQL. The old api/sales SalesController stays in place,
    // unmapped, as instant rollback. shop_id / user_id / pos_code / user_name come from
    // Request.Properties, filled by LegacyContextHandler from the X-Ctx-* headers.
    [RoutePrefix("legacy/sales")]
    public class SalesLegacyController : ApiController
    {
        // DELETE api/sales/{id}
        // Soft-voids the invoice: zeroes all amounts, sets IsVoided=1, reverses inventory.
        // The tblSales row is KEPT (IsVoided=1). Mirrors SaleAndReturnDAL.VoidSale() at
        // line 15240, which is triggered by the Void button in Candela's sale screen.
        [HttpDelete, Route("{id:int}")]
        public HttpResponseMessage VoidSale(int id)
        {

            int    userId  = (int)   Request.Properties["user_id"];
            int    shopId  = (int)   Request.Properties["shop_id"];
            string posCode = (string)Request.Properties["pos_code"];

            try
            {
                using (var chkCon = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    chkCon.Open();
                    var chkCmd = new SqlCommand(
                        "SELECT isnull(IsVoided,0) FROM tblSales WHERE sale_id = @sid AND shop_id = @shid",
                        chkCon);
                    chkCmd.Parameters.AddWithValue("@sid",  id);
                    chkCmd.Parameters.AddWithValue("@shid", shopId);
                    var chk = chkCmd.ExecuteScalar();
                    if (chk == null)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Sale not found." });
                    if (Convert.ToBoolean(chk))
                        return Request.CreateResponse((HttpStatusCode)409, new { error = "Sale is already voided." });
                }

                var model = BuildVoidModel(id, shopId, userId, posCode, "Void");

                // Candela's form runs this before voiding (frmSaleAndReturn.vb:38814): the invoice date must be
                // after the last POS closing and the last shop closing (CommonDAL.checkTransactionAllowedOrNot).
                new SaleAndReturnDAL().IsValidateForDelete(model);

                // VoidSale() requires a pre-opened transaction (SaleAndReturnDAL.vb:15240).
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    using (var trans = con.BeginTransaction())
                    {
                        string auditMsg = "";
                        var dal = new SaleAndReturnDAL();
                        bool ok = dal.VoidSale(model, EnumActions.Delete, ref auditMsg, trans);
                        if (!ok) { trans.Rollback(); return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = "VoidSale() returned false." }); }
                        trans.Commit();
                        return Request.CreateResponse(HttpStatusCode.OK,
                            ApiResponse<object>.Ok(new { voided = true, sale_id = id, message = string.IsNullOrWhiteSpace(auditMsg) ? null : auditMsg }));
                    }
                }
            }
            catch (Exception ex) { return HandleDalException(ex); }
        }

        // DELETE api/sales/{id}/hard
        // Hard-deletes the invoice: writes audit trail to tblSalesHistory then physically
        // removes rows from tblSales/tblSalesLineItems/tblAccountTransactions and reverses
        // inventory. Mirrors the Delete() path in frmSaleAndReturn.vb:12165 which calls
        // SaleAndReturnDAL.Add(model, EnumActions.Delete) (SaleAndReturnDAL.vb:4582-5160).
        [HttpDelete, Route("{id:int}/hard")]
        public HttpResponseMessage HardDeleteSale(int id)
        {

            int    userId  = (int)   Request.Properties["user_id"];
            int    shopId  = (int)   Request.Properties["shop_id"];
            string posCode = (string)Request.Properties["pos_code"];

            try
            {
                using (var chkCon = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    chkCon.Open();
                    var chkCmd = new SqlCommand(
                        "SELECT COUNT(1) FROM tblSales WHERE sale_id = @sid AND shop_id = @shid",
                        chkCon);
                    chkCmd.Parameters.AddWithValue("@sid",  id);
                    chkCmd.Parameters.AddWithValue("@shid", shopId);
                    if (Convert.ToInt32(chkCmd.ExecuteScalar()) == 0)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Sale not found." });
                }

                var model = BuildVoidModel(id, shopId, userId, posCode, "Delete");

                // Same pre-check Candela's form runs before a delete/void (SaleAndReturnDAL.IsValidateForDelete).
                new SaleAndReturnDAL().IsValidateForDelete(model);

                // Add() overload at SaleAndReturnDAL.vb:3518 opens its own connection + transaction.
                string auditMsg = "";
                bool ok = new SaleAndReturnDAL().Add(model, EnumActions.Delete, ref auditMsg);
                if (!ok)
                    return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = "DAL.Add(Delete) returned false. " + auditMsg });

                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<object>.Ok(new { deleted = true, sale_id = id, message = string.IsNullOrWhiteSpace(auditMsg) ? null : auditMsg }));
            }
            catch (Exception ex) { return HandleDalException(ex); }
        }

        // PUT api/sales/{id}
        // Updates an existing sale: replaces line items, re-applies inventory, updates
        // accounting. Mirrors frmSaleAndReturn.vb:Update() → SaleAndReturnDAL.Add(EnumActions.Update).
        [HttpPut, Route("{id:int}")]
        public HttpResponseMessage UpdateSale(int id, [FromBody] SaleRequest req)
        {
            if (req == null)
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "Request body is required." });
            if (req.Items == null || req.Items.Count == 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "items cannot be empty." });


            int    userId   = (int)   Request.Properties["user_id"];
            int    shopId   = (int)   Request.Properties["shop_id"];
            string posCode  = (string)Request.Properties["pos_code"];
            string userName = (string)Request.Properties["user_name"];

            try
            {
                // Verify the sale exists and belongs to this shop
                using (var chkCon = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    chkCon.Open();
                    var chkCmd = new SqlCommand(
                        "SELECT COUNT(1) FROM tblSales WHERE sale_id = @sid AND shop_id = @shid AND isnull(IsVoided,0)=0",
                        chkCon);
                    chkCmd.Parameters.AddWithValue("@sid",  id);
                    chkCmd.Parameters.AddWithValue("@shid", shopId);
                    if (Convert.ToInt32(chkCmd.ExecuteScalar()) == 0)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Sale not found or already voided." });

                    // This path rebuilds the invoice as a SALE: it would reset SaleReturningNo / SaleReturnShopID (the link to
                    // the original invoice) and rewrite the lines in the sale mapping. A saved return is edited through
                    // PUT /api/returns/{id}, which keeps the link — never through here.
                    var retCmd = new SqlCommand(
                        "SELECT COUNT(1) FROM tblSales WHERE sale_id = @sid AND shop_id = @shid " +
                        "AND (isnull(SaleReturningNo,0) > 0 OR isnull(NT_amount,0) < 0)", chkCon);
                    retCmd.Parameters.AddWithValue("@sid",  id);
                    retCmd.Parameters.AddWithValue("@shid", shopId);
                    if (Convert.ToInt32(retCmd.ExecuteScalar()) > 0)
                        return Request.CreateResponse((HttpStatusCode)422,
                            new { error = "A return invoice has to be edited as a return." });
                }

                // Build the full model just like a new sale, then set SaleID so the DAL
                // uses Update path (SaleAndReturnDAL.vb:4634 — delta line items, re-inventory).
                // Line items have SaleDetailID=0, so the DAL deletes ALL existing items and
                // re-inserts the new set (strSaleItemIDs="0" → DELETE NOT IN (0) removes all).
                var sale = BuildModel(req, userId, shopId, posCode, userName);
                sale.SaleID = id;

                // Candela's form runs IsValidateForUpdate before an edit (frmSaleAndReturn.vb:7778): both the
                // saved invoice date and the new one must be after the last POS / shop closing.
                new SaleAndReturnDAL().IsValidateForUpdate(sale);

                // Candela's IsValidate runs these on an edit just as on a new invoice (credit facility / limit, Enforce_Mobile,
                // zero retail price, below cost, adjustment limit). The invoice's own credit amount is left out of the
                // customer's outstanding.
                var ruleError = ValidateSaleRules(req, sale, userId, shopId, id);
                if (ruleError != null) return ruleError;

                sale.ActivityLog.ScreenTitle = "Update";

                string auditMsg = "";
                bool ok = new SaleAndReturnDAL().Add(sale, EnumActions.Update, ref auditMsg);

                if (!ok)
                    return Request.CreateResponse(HttpStatusCode.InternalServerError,
                        new { error = "DAL.Add(Update) returned false. " + auditMsg });

                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<object>.Ok(new { sale_id = id, updated = true, message = string.IsNullOrWhiteSpace(auditMsg) ? null : auditMsg }));
            }
            catch (Exception ex) { return HandleDalException(ex); }
        }

        private SaleAndReturn BuildVoidModel(int saleId, int shopId, int userId, string posCode, string screenTitle)
        {
            var model = new SaleAndReturn();
            model.SaleID                  = saleId;
            model.SaleDateTime            = DateTime.Now;
            model.Shop.ShopID             = shopId;
            model.UserInfo.UserID         = userId;
            model.UserInfo.POSCode        = posCode;
            model.Customer.MemberName     = "";
            model.ListOfSaleItems         = new List<SaleAndReturnItems>();
            model.ActivityLog.LogGroup    = "POS API";
            model.ActivityLog.ScreenTitle = screenTitle;
            model.ActivityLog.UserID      = userId;
            model.ActivityLog.ShopID      = shopId;
            return model;
        }

        // tblShopConfiguration key lookup — mirrors ShopDAL.GetShopConfigurationValue().
        private string GetShopConfig(int shopId, string key)
        {
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(
                    "SELECT isnull(config_value, '') FROM tblShopConfiguration " +
                    "WHERE shop_id = @shopId AND config_name = @key", con);
                cmd.Parameters.AddWithValue("@shopId", shopId);
                cmd.Parameters.AddWithValue("@key",    key);
                var result = cmd.ExecuteScalar();
                return result?.ToString() ?? "";
            }
        }

        // tblDefShopEmployees.commissionpercentage — the employee's default commission rate.
        // Used to populate ListOfSalesPersonCommission before calling Add().
        // Try-catch guards against schema versions where the column does not yet exist.
        private double GetSalespersonCommissionPct(int shopId, int employeeId)
        {
            try
            {
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var cmd = new SqlCommand(
                        "SELECT isnull(commissionpercentage, 0) FROM tblDefShopEmployees " +
                        "WHERE shop_id = @shopId AND shop_employee_id = @empId", con);
                    cmd.Parameters.AddWithValue("@shopId", shopId);
                    cmd.Parameters.AddWithValue("@empId",  employeeId);
                    var result = cmd.ExecuteScalar();
                    return result != null && result != DBNull.Value
                        ? Convert.ToDouble(result) : 0;
                }
            }
            catch (System.Data.SqlClient.SqlException)
            {
                return 0;
            }
        }

        private HttpResponseMessage HandleDalException(Exception ex)
        {
            string msg = ex.Message ?? "";
            if (msg.IndexOf("physical audit",   StringComparison.OrdinalIgnoreCase) >= 0
             || msg.IndexOf("not allowed",       StringComparison.OrdinalIgnoreCase) >= 0
             || msg.IndexOf("customer closing",  StringComparison.OrdinalIgnoreCase) >= 0
             || msg.IndexOf("period",            StringComparison.OrdinalIgnoreCase) >= 0
             || msg.IndexOf("closed before",     StringComparison.OrdinalIgnoreCase) >= 0
             || msg.IndexOf("last closing date", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string userMsg = msg;
                int idx = msg.IndexOf("Exception Msg ", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) userMsg = msg.Substring(idx + "Exception Msg ".Length).Trim();
                return Request.CreateResponse((HttpStatusCode)422, new { error = userMsg });
            }
            return Request.CreateResponse(HttpStatusCode.InternalServerError,
                new { error = "An internal error occurred.", detail = msg });
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


            int    userId   = (int)   Request.Properties["user_id"];
            int    shopId   = (int)   Request.Properties["shop_id"];
            string posCode  = (string)Request.Properties["pos_code"];
            string userName = (string)Request.Properties["user_name"];

            SaleAndReturn sale = null;
            try
            {
                // Idempotency — atomically claim this GUID before doing any work.
                // INSERT WHERE NOT EXISTS: if 0 rows affected, another request already owns it.
                if (!TryClaimIdempotencySlot(req.ClientTxnGuid, shopId))
                {
                    // Another request owns this GUID. Give it one brief window to commit.
                    System.Threading.Thread.Sleep(100);
                    int existing = GetExistingSaleId(req.ClientTxnGuid, shopId);
                    if (existing > 0)
                        return Request.CreateResponse(HttpStatusCode.OK,
                            ApiResponse<object>.Ok(new { sale_id = existing, idempotent = true }));

                    return Request.CreateResponse(HttpStatusCode.Conflict,
                        new { error = "Duplicate request in progress. Please retry." });
                }

                // Build SaleAndReturn model from the DTO
                sale = BuildModel(req, userId, shopId, posCode, userName);

                // Gift card redemption: populate ListOfGftcardPaymentDetails so
                // SaleAndReturnDAL.Add() writes the negative tblGiftCardLedger row.
                // The model sets GiftCardAmount/GiftCardNo for the sale header, but the
                // DAL iterates this list (VB:4559) to actually deduct the balance — an
                // empty list means no ledger row and the balance is never decremented.
                if (req.GiftCardAmount > 0)
                {
                    // Candela's payment screen (frmPaymetOptions.vb:1587) refuses a gift card amount above the balance
                    // of the cards actually loaded, with gmsgInvalidGftCardAmt. The screen is a client-side step, so
                    // it is repeated here: an amount with no card number, an unknown card, or a balance that doesn't
                    // cover it never reaches the ledger (where it would otherwise be counted as paid / go negative).
                    const string invalidGiftCardAmount = "Invalid Gift Card amount.";
                    if (string.IsNullOrWhiteSpace(req.GiftCardNo))
                    {
                        DeleteIdempotencySlot(req.ClientTxnGuid, shopId);
                        return Request.CreateResponse((HttpStatusCode)422, new { error = invalidGiftCardAmount });
                    }

                    string gcRaw = req.GiftCardNo.Trim();
                    string gcNumeric = gcRaw;
                    var gcParts = gcRaw.Split('-');
                    if (gcParts.Length == 3 && gcParts[1].Length == 6 &&
                        int.TryParse(gcParts[1], out int gcParsed))
                        gcNumeric = gcParsed.ToString();

                    using (var gcCon = new SqlConnection(CandelaBootstrap.ConnectionString))
                    {
                        gcCon.Open();
                        var gcCmd = new SqlCommand(
                            "SELECT TOP 1 id, Card_no FROM tbldefCards " +
                            "WHERE Alternate_card_no = @cn OR CAST(Card_no AS varchar) = @cnNum",
                            gcCon);
                        gcCmd.Parameters.AddWithValue("@cn",    gcRaw);
                        gcCmd.Parameters.AddWithValue("@cnNum", gcNumeric);
                        int gcId = 0, gcNo = 0;
                        using (var gcRdr = gcCmd.ExecuteReader())
                        {
                            if (gcRdr.Read())
                            {
                                gcId = Convert.ToInt32(gcRdr["id"]);
                                gcNo = Convert.ToInt32(gcRdr["Card_no"]);
                            }
                        }

                        // Same balance rule as GET gift-cards/{q}/balance and POST gift-cards/validate: top-ups minus
                        // redemptions, ignoring ledger rows whose expiry has passed.
                        double gcBalance = 0;
                        if (gcId > 0)
                        {
                            var gcBalCmd = new SqlCommand(
                                "SELECT isnull(SUM(l.Top_up_Amt), 0) FROM tblGiftCardLedger l " +
                                "WHERE l.cardid = @cid AND isnull(l.CardExpiryDate, GETDATE()) >= GETDATE()", gcCon);
                            gcBalCmd.Parameters.AddWithValue("@cid", gcId);
                            gcBalance = Convert.ToDouble(gcBalCmd.ExecuteScalar());
                        }

                        if (gcId == 0 || req.GiftCardAmount > gcBalance + 0.005)
                        {
                            DeleteIdempotencySlot(req.ClientTxnGuid, shopId);
                            return Request.CreateResponse((HttpStatusCode)422, new { error = invalidGiftCardAmount });
                        }

                        sale.ListOfGftcardPaymentDetails.Add(new gftCardPaymentDetail
                        {
                            gftCardID  = gcId,
                            GftCardNo  = gcNo,
                            gftCardAmt = (decimal)req.GiftCardAmount,
                        });
                    }
                }

                // Credit facility / limit, Enforce_Mobile, zero retail price, below cost and the adjustment limit —
                // Candela's IsValidate rules, shared with UpdateSale (see ValidateSaleRules).
                var ruleError = ValidateSaleRules(req, sale, userId, shopId, 0);
                if (ruleError != null) return ruleError;

                // Gap 6: mark coupon Used before finalising the sale.
                // CheckCouponStatus() opens its own transaction and sets Status='Used'.
                // Returns false when coupon is not ACTIVE (already used or not found).
                // frmSaleAndReturn.vb:10623, SaleAndReturnDAL.vb:14715
                // Like Candela (frmSaleAndReturn.vb:10076, CR #7693) a coupon only counts when the invoice really carries
                // a coupon discount: coupon lines are tagged disc_category "C". A code that was typed but not applied
                // (or applied to nothing) is not marked Used.
                bool couponDiscountOnInvoice = req.Items != null && req.Items.Any(i =>
                    string.Equals(i.DiscCategory, "C", StringComparison.OrdinalIgnoreCase) && i.UnitDiscount > 0);
                if (!string.IsNullOrWhiteSpace(req.CouponNo) && couponDiscountOnInvoice)
                {
                    bool couponOk = new SaleAndReturnDAL().CheckCouponStatus(req.CouponNo, shopId);
                    if (!couponOk)
                        return Request.CreateResponse(HttpStatusCode.Conflict,
                            new { error = $"Coupon '{req.CouponNo}' is no longer active." });
                }

                // ── A-section config validations (frmSaleAndReturn.vb IsValidate) ──────────
                {
                    var rcmsCfg = CandelaBootstrap.GetRCMSConfig();

                    // A1: EnforceCustomerInfo — customer required when net total exceeds threshold
                    // frmSaleAndReturn.vb:758, 6344+
                    if (double.TryParse(
                            rcmsCfg.TryGetValue("EnforceCustomerInfo", out var eciStr) ? eciStr : "0",
                            out double enforceCI) && enforceCI > 0
                        && req.CustomerId == 0 && req.NetTotal > enforceCI)
                    {
                        return Request.CreateResponse((HttpStatusCode)422,
                            new { error = $"A customer is required for sales above {enforceCI:F2}." });
                    }

                    // Enforce_Mobile and BlockSalesHavingZeroRetailPrice: see ValidateSaleRules above.
                }
                // ── end A-section validations ─────────────────────────────────────────────

                // ── B-section config validations (Pricing & Discount Controls) ───────────
                {
                    var bCfg = CandelaBootstrap.GetRCMSConfig();

                    // Below cost (BelowCostSales right) and the adjustment limit: see ValidateSaleRules above.

                    // E5: AutoRounding — bypass AdjustmentLimit and ShowAdjustmentReason when AutoRounding is
                    // configured (Candela auto-fills txtAdjustment without supervisor approval — vb:13374).
                    bCfg.TryGetValue("AutoRounding", out var autoRndVal);
                    bool autoRoundingActive = !string.IsNullOrWhiteSpace(autoRndVal)
                        && !autoRndVal.Equals("None", StringComparison.OrdinalIgnoreCase);

                    // B4: ShowAdjustmentReason — frmSaleAndReturn.vb:7662/7774
                    // When adjustment is entered and ShowAdjustmentReason=True, a reason ID is mandatory
                    if (req.AdjustmentAmount != 0
                        && !autoRoundingActive
                        && CfgIs(bCfg, "ShowAdjustmentReason", "True")
                        && req.AdjustmentReasonId == null)
                    {
                        return Request.CreateResponse((HttpStatusCode)422,
                            new { error = "An adjustment reason is required." });
                    }
                }
                // ── end B-section validations ─────────────────────────────────────────────

                // FonePay: reject if this transaction ID was already processed
                // Mirrors frmMobilePayment.vb manual-mode validation against tblSales.transactionid
                if (!string.IsNullOrEmpty(req.TransactionId))
                {
                    using (var chkCon = new SqlConnection(CandelaBootstrap.ConnectionString))
                    {
                        chkCon.Open();
                        var chkCmd = new SqlCommand(
                            "SELECT COUNT(1) FROM tblSales WHERE transactionid = @txid AND shop_id = @sid",
                            chkCon);
                        chkCmd.Parameters.AddWithValue("@txid", req.TransactionId);
                        chkCmd.Parameters.AddWithValue("@sid",  shopId);
                        if (Convert.ToInt32(chkCmd.ExecuteScalar()) > 0)
                        {
                            DeleteIdempotencySlot(req.ClientTxnGuid, shopId);
                            return Request.CreateResponse((HttpStatusCode)409,
                                new { error = $"FonePay Transaction ID '{req.TransactionId}' has already been used." });
                        }
                    }
                }

                // Call Candela DAL — same path as the desktop
                var dal = new SaleAndReturnDAL();
                string auditMsg = "";
                bool ok = dal.Add(sale, EnumActions.Save, ref auditMsg);

                if (!ok)
                    return Request.CreateResponse(HttpStatusCode.InternalServerError,
                        new { error = "SaleAndReturnDAL.Add() returned false. " + auditMsg });

                // SaleAndReturnDAL.Add() writes Card_amt/credit_card_id onto tblSales itself
                // but never inserts the matching row into tblsalesCashTenders. spPOSCashTenders
                // (the POS Cash Management "Tender Details" report section) reads card-type
                // totals exclusively from tblsalesCashTenders via an inner join to
                // tbldefCreditCards - without this row, card payments silently vanish from
                // that report even though the sale itself saved correctly.
                if (req.CardAmount > 0 && req.CreditCardId > 0)
                    InsertSalesCashTender(sale.SaleID, shopId, req.CreditCardId, req.CardAmount);

                UpdateIdempotencySlot(req.ClientTxnGuid, sale.SaleID, shopId);

                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<object>.Ok(new { sale_id = sale.SaleID }));
            }
            catch (Exception ex)
            {
                // dal.Add() can throw a secondary error after tblSales/tblSalesLineItems
                // already committed. If SaleID was assigned the sale was saved successfully.
                if (sale != null && sale.SaleID > 0)
                {
                    UpdateIdempotencySlot(req.ClientTxnGuid, sale.SaleID, shopId);
                    return Request.CreateResponse(HttpStatusCode.OK,
                        ApiResponse<object>.Ok(new { sale_id = sale.SaleID }));
                }
                DeleteIdempotencySlot(req.ClientTxnGuid, shopId);
                // Candela's own business-rule stops (SaleAndReturnDAL.SalesValidations: "Operation is not allowed because
                // physical audit exists after the sale date ...", i.e. a closed stock count on/after the sale date) must
                // reach the cashier as that message — not as a generic 500 / network error.
                if ((ex.Message ?? "").IndexOf("physical audit", StringComparison.OrdinalIgnoreCase) >= 0)
                    return HandleDalException(ex);
                return ApiError.Internal(Request, ex, "SalesController.PostSale");
            }
        }

        // The validations Candela's IsValidate runs for BOTH a new invoice and an edited one — the `Mode = New Or Mode = Edit`
        // block of frmSaleAndReturn.vb:5761 (credit facility / credit limit :6372-6463, zero retail price :5546, below cost
        // :6151, adjustment limit isValidAdjustment :6622) and the Enforce_Mobile check at :8393, which has no mode condition.
        // PostSale and UpdateSale share this so an edit can't skip what a new sale enforces. editingSaleId is 0 for a new
        // sale. Returns null when everything passes, otherwise the 422 to send back. Messages are Candela's own
        // (tblLanguageBasedControls) except where Candela has no equivalent.
        private HttpResponseMessage ValidateSaleRules(SaleRequest req, SaleAndReturn sale, int userId, int shopId, int editingSaleId)
        {
            var cfg = CandelaBootstrap.GetRCMSConfig();

            // Credit facility + credit limit. An edited invoice's own credit amount is not counted against the headroom.
            if (req.CustomerId > 0 && (req.PaymentType ?? "").ToLower() == "credit")
            {
                string creditError = ValidateCreditSale(req.CustomerId, shopId,
                    req.CreditAmount > 0 ? req.CreditAmount : req.NetTotal, editingSaleId);
                if (creditError != null)
                    return Request.CreateResponse((HttpStatusCode)422, new { error = creditError });
            }

            // Enforce_Mobile — a walk-in invoice needs a mobile number (gMsgEnforceMobile). When editing, the number already
            // saved on the invoice counts (Candela reloads it from the saved invoice).
            if (req.CustomerId == 0
                && CfgIs(cfg, "Enforce_Mobile", "True")
                && string.IsNullOrWhiteSpace(req.WalkInPhone)
                && !SavedSaleHasMobile(editingSaleId, shopId))
            {
                return Request.CreateResponse((HttpStatusCode)422,
                    new { error = "Please enter customer's mobile no." });
            }

            // BlockSalesHavingZeroRetailPrice (gMsgZeroRetailPrice)
            if (CfgIs(cfg, "BlockSalesHavingZeroRetailPrice", "True")
                && req.Items != null && req.Items.Any(i => i.UnitRate == 0))
            {
                return Request.CreateResponse((HttpStatusCode)422,
                    new { error = "Retail price of some products is zero. Kindly enter the correct retail price and then try" });
            }

            // Below cost (frmSaleAndReturn.vb:5404-5560). The check runs when the group holds the BelowCostSales right OR the
            // system setting RestrictBelowCostSales is on.
            //   With the right: a line sold below its average cost is refused (gMsgRestrictCostSingle) - unless
            //     EnablePrdWiseBelowCost is on and that product has Allow_Below_Cost; with RestrictBelowCostSales also on, an
            //     invoice whose total cost is more than its net total is refused too (gMsgRestrictCostTotal, skipped when
            //     EnablePrdWiseBelowCost is on).
            //   Without the right (setting on): the same lines / total only ask for a confirmation, answered by sending the
            //     sale again with bypass_below_cost_warning (409 warn_below_cost).
            // Candela's "rate" for a line is Rate - Unit Discount; return lines (negative quantity) are not checked, they
            // are netted off the total instead.
            bool hasBelowCostRight = Request.Properties.ContainsKey("below_cost_right")
                && (bool)Request.Properties["below_cost_right"];
            bool restrictBelowCostCfg = CfgIs(cfg, "RestrictBelowCostSales", "True");

            if ((hasBelowCostRight || restrictBelowCostCfg) && req.Items != null && req.Items.Count > 0)
            {
                bool prdWise   = CfgIs(cfg, "EnablePrdWiseBelowCost", "True");
                var itemIds    = req.Items.Select(i => i.ProductItemId).Distinct().ToList();
                var avgCosts   = new Dictionary<int, double>();
                var allowBelow = new Dictionary<int, bool>();

                var paramNames = string.Join(",", Enumerable.Range(0, itemIds.Count).Select(i => "@cid" + i));
                using (var costCon = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    costCon.Open();
                    var costCmd = new SqlCommand(
                        "SELECT pi.Product_Item_ID, ISNULL(p.Average_cost,0), ISNULL(p.allow_below_cost,0) " +
                        "FROM tblProductItem pi JOIN tblDefProducts p ON p.Product_ID = pi.Product_ID " +
                        "WHERE pi.Product_Item_ID IN (" + paramNames + ")", costCon);
                    for (int i = 0; i < itemIds.Count; i++)
                        costCmd.Parameters.AddWithValue("@cid" + i, itemIds[i]);
                    using (var rd = costCmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            int pid = Convert.ToInt32(rd[0]);
                            avgCosts[pid]   = Convert.ToDouble(rd[1]);
                            allowBelow[pid] = Convert.ToBoolean(rd[2]);
                        }
                    }
                }

                // Backfill AvgCost before the gate - needed for inventory recording whether or not a line blocks the sale.
                if (sale?.ListOfSaleItems != null)
                {
                    foreach (var ln in sale.ListOfSaleItems)
                    {
                        double avgC;
                        if (avgCosts.TryGetValue(ln.ProductItemID, out avgC))
                            ln.AvgCost = avgC;
                    }
                }

                double totalCost = 0, returnTotal = 0;
                foreach (var item in req.Items)
                {
                    double avg;
                    if (!avgCosts.TryGetValue(item.ProductItemId, out avg)) continue;

                    if (item.Quantity < 0)
                    {
                        // vb:5431-5447 - what a return line gives back: its total, less its customer discount and its share of
                        // the marketing discount, plus its VAT when the price does not already include it.
                        double qtyAbs  = Math.Abs(item.Quantity);
                        double lineTot = Math.Abs((item.UnitRate - item.UnitDiscount) * qtyAbs);
                        double rowVal  = lineTot - Math.Abs(item.CustomerDiscountPerUnit * qtyAbs);
                        if (req.GrossTotal > 0) rowVal -= (req.MarketingDiscount / req.GrossTotal) * lineTot;
                        if (!item.PriceIncludeVat) rowVal += Math.Abs(item.VatValue * qtyAbs);
                        returnTotal += rowVal;
                        continue;
                    }

                    if (avg > 0 && (item.UnitRate - item.UnitDiscount) < avg)
                    {
                        if (hasBelowCostRight)
                        {
                            if (!(prdWise && allowBelow.TryGetValue(item.ProductItemId, out bool ab) && ab))
                                return Request.CreateResponse((HttpStatusCode)422,
                                    new { error = "You are not allowed to sell below cost. Please check selected product" });   // gMsgRestrictCostSingle
                        }
                        else if (!req.BypassBelowCostWarning)
                        {
                            return Request.CreateResponse((HttpStatusCode)409, new
                            {
                                error           = "{ " + item.ProductCode + " } Product is selling below cost, do you want to proceed?",   // gMsgWarnCostSingle
                                warn_below_cost = true
                            });
                        }
                    }

                    totalCost += avg * Math.Abs(item.Quantity);
                }

                if (restrictBelowCostCfg)
                {
                    int amountRound = 0;
                    if (cfg.TryGetValue("isEnableDecimalPoint", out var amtRoundStr)) int.TryParse(amtRoundStr, out amountRound);

                    // vb:5521-5528 - an automatic adjustment (0.25, 0.65 ...) is added to the return side so both sides compare equal.
                    if (returnTotal > 0) returnTotal += Math.Abs(req.AdjustmentAmount);

                    if (Math.Round(totalCost, amountRound, MidpointRounding.AwayFromZero)
                        > Math.Round(req.NetTotal + Math.Round(returnTotal, amountRound, MidpointRounding.AwayFromZero),
                                     amountRound, MidpointRounding.AwayFromZero))
                    {
                        if (hasBelowCostRight)
                        {
                            if (!prdWise)
                                return Request.CreateResponse((HttpStatusCode)422,
                                    new { error = "You are not allowed to sell below cost. The cost of this invoice is more than its total." });   // gMsgRestrictCostTotal
                        }
                        else if (!req.BypassBelowCostWarning)
                        {
                            return Request.CreateResponse((HttpStatusCode)409, new
                            {
                                error           = "The invoice is selling below cost, do you want to proceed?",   // gMsgWarnCostTotal
                                warn_below_cost = true
                            });
                        }
                    }
                }
            }

            // AdjustmentLimit / AdjustmentLimitType (isValidAdjustment). AutoRounding bypasses it — Candela fills the
            // adjustment itself without supervisor approval (vb:13374).
            cfg.TryGetValue("AutoRounding", out var autoRndVal);
            bool autoRoundingActive = !string.IsNullOrWhiteSpace(autoRndVal)
                && !autoRndVal.Equals("None", StringComparison.OrdinalIgnoreCase);

            if (req.AdjustmentAmount != 0 && !autoRoundingActive)
            {
                double adjLimit = 0;
                if (cfg.TryGetValue("AdjustmentLimit", out string adjLimitStr))
                    double.TryParse(adjLimitStr, out adjLimit);

                if (adjLimit > 0)
                {
                    // ApplyAdjustment / ApplyOpenAdjustment are bit columns on TblSecurityUser (per-user flags) — vb:3126
                    bool hasOpenAdj = false, hasAdj = false;
                    using (var rightsCon = new SqlConnection(CandelaBootstrap.ConnectionString))
                    {
                        rightsCon.Open();
                        var adjRightCmd = new SqlCommand(
                            "SELECT isnull(ApplyAdjustment, 0) AS ApplyAdjustment," +
                            "       isnull(ApplyOpenAdjustment, 0) AS ApplyOpenAdjustment" +
                            " FROM TblSecurityUser WHERE user_id = @uid", rightsCon);
                        adjRightCmd.Parameters.AddWithValue("@uid", userId);
                        using (var rdr = adjRightCmd.ExecuteReader())
                        {
                            if (rdr.Read())
                            {
                                hasAdj     = Convert.ToBoolean(rdr["ApplyAdjustment"]);
                                hasOpenAdj = Convert.ToBoolean(rdr["ApplyOpenAdjustment"]);
                            }
                        }
                    }

                    if (!hasOpenAdj)
                    {
                        if (!hasAdj)
                            return Request.CreateResponse((HttpStatusCode)422,
                                new { error = "You do not have permission to apply an invoice adjustment." });

                        if (!cfg.TryGetValue("AdjustmentLimitType", out string adjLimitType))
                            adjLimitType = "VALUE";
                        double absAdj = Math.Abs(req.AdjustmentAmount);
                        bool exceeded = adjLimitType.Equals("PERCENTAGE", StringComparison.OrdinalIgnoreCase)
                            ? req.NetTotal > 0 && (absAdj / req.NetTotal * 100) > adjLimit
                            : absAdj > adjLimit;
                        if (exceeded)
                            return Request.CreateResponse((HttpStatusCode)422,
                                new { error = "Current user is allowed for limited adjustment." });
                    }
                }
            }

            return null;
        }

        // True when the saved invoice already carries a mobile number (tblSales.MobileNumber).
        private bool SavedSaleHasMobile(int saleId, int shopId)
        {
            if (saleId <= 0) return false;
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var cmd = new SqlCommand(
                    "SELECT isnull(MobileNumber, '') FROM tblSales WHERE sale_id = @sid AND shop_id = @shid", con);
                cmd.Parameters.AddWithValue("@sid",  saleId);
                cmd.Parameters.AddWithValue("@shid", shopId);
                return !string.IsNullOrWhiteSpace(Convert.ToString(cmd.ExecuteScalar()));
            }
        }

        private int GetExistingSaleId(string clientGuid, int shopId)
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

        // Atomically inserts a placeholder row (sale_id=0) for this GUID.
        // Returns true if this request owns the slot; false if another request already claimed it.
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
                // UNIQUE violation — concurrent request won the race; this request is the duplicate
                return false;
            }
            catch
            {
                return true; // fail open so the sale can proceed on DB error
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
                        // UPDATE first; if no row exists (slot was cleaned up), upsert via INSERT
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

        // Records the card-type tender breakdown for a sale so spPOSCashTenders (the POS
        // Cash Management "Tender Details" report) can find it via its join to
        // tbldefCreditCards. Best-effort: the sale itself already saved successfully by the
        // time this runs, so a failure here is logged, not surfaced to the caller.
        private void InsertSalesCashTender(int saleId, int shopId, int creditCardId, double cardAmount)
        {
            try
            {
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var cmd = new SqlCommand(
                        "INSERT INTO tblsalesCashTenders (SaleID, ShopID, TenderID, TenderAmount) " +
                        "VALUES (@saleId, @sid, @tenderId, @amount)", con);
                    cmd.Parameters.AddWithValue("@saleId",   saleId);
                    cmd.Parameters.AddWithValue("@sid",      shopId);
                    cmd.Parameters.AddWithValue("@tenderId", creditCardId);
                    cmd.Parameters.AddWithValue("@amount",   (decimal)cardAmount);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError(
                    "InsertSalesCashTender failed for saleId={0}: {1}", saleId, ex);
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

        // Returns an error message if the credit sale should be rejected, null if OK.
        // Mirrors frmSaleAndReturn.vb:6344-6356 and 14365-14370.
        // outstanding = total credit sales billed - total receipts received
        // excludeSaleId: the invoice being edited — its own credit amount is already in the customer's outstanding, so
        // it is left out (Candela's AvailableCreditLimit for BackEndUpdateValidation, frmSaleAndReturn.vb:6447-6463).
        private string ValidateCreditSale(int customerId, int shopId, double creditAmount, int excludeSaleId = 0)
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
                        return "Customer is not allowed for credit sale.";   // gstrCustomernotallowedcreditsale
                    }
                }

                if (!allowCredit)
                    return "Customer is not allowed for credit sale.";       // gstrCustomernotallowedcreditsale

                // Outstanding balance = credit sales billed - receipts received
                // (CustomerReceiptDAL.vb:754 confirms tblMemberReceipts.amount column)
                // is_return_item does not exist on tblSales (it's on tblSalesLineItems).
                // Returns are credit sales with negative NT_amount, so SUM already nets them out.
                var balCmd = new SqlCommand(
                    "SELECT " +
                    "  ISNULL((SELECT SUM(NT_amount) FROM tblSales " +
                    "           WHERE member_id = @mid AND isCreditSale = 1 AND shop_id = @sid AND sale_id <> @ex), 0) " +
                    "- ISNULL((SELECT SUM(amount) FROM tblMemberReceipts " +
                    "           WHERE member_id = @mid AND shop_id = @sid), 0) " +
                    "AS outstanding", con);
                balCmd.Parameters.AddWithValue("@mid", customerId);
                balCmd.Parameters.AddWithValue("@sid", shopId);
                balCmd.Parameters.AddWithValue("@ex",  excludeSaleId);

                decimal outstanding = Convert.ToDecimal(balCmd.ExecuteScalar());
                decimal newTotal    = outstanding + (decimal)creditAmount;

                if (newTotal > creditLimit)
                    return "You are not allowed to sell more than the credit limit of customer";   // gMsgStrCreditLimitNotAllowed
            }
            return null;
        }

        // tblShopConfiguration bit-backed flags aren't consistently 'True'/'False' text — some
        // are stored as '1'/'0'. When checking a boolean flag (expectedValue="True"), accept
        // '1' too, so a real shop config value doesn't silently read as false.
        private static bool CfgIs(Dictionary<string, string> cfg, string key, string expectedValue)
        {
            if (!cfg.TryGetValue(key, out var v)) return false;
            if (string.Equals(expectedValue, "True", StringComparison.OrdinalIgnoreCase))
                return string.Equals(v, "True", StringComparison.OrdinalIgnoreCase) || v?.Trim() == "1";
            return string.Equals(v, expectedValue, StringComparison.OrdinalIgnoreCase);
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
            sale.AdjustmentReasonId  = req.AdjustmentReasonId;
            sale.CashAmount          = req.CashAmount;
            sale.CreditCardAmount    = req.CardAmount;
            sale.CreditAmount        = (decimal)req.CreditAmount;
            sale.GiftCardAmount      = (decimal)req.GiftCardAmount;
            sale.GiftCardNo          = req.GiftCardNo ?? "";
            // Change due back to the customer - mirrors frmSaleAndReturn.vb's invariant
            // NetTotal = Cash - Balance (line ~9094), i.e. Balance = Cash - NetTotal. Clamped
            // to >= 0 since card/credit/split tenders never carry an overpayment here (those
            // branches always send CashAmount <= NetTotal).
            // Candela's change is everything tendered minus the bill — cash + credit + card + GIFT CARD − bill
            // (frmPaymetOptions TextBox1/TextBox8, vb:1274-1281). Counting only cash showed no change when a gift card
            // covered part of the bill and the rest was paid with more cash than needed (1,200 bill, 1,000 card + 1,000 cash).
            sale.BalanceAmount       = Math.Max(0, req.CashAmount + req.CardAmount + req.CreditAmount + req.GiftCardAmount - req.NetTotal);

            // Customer
            if (req.CustomerId > 0)
            {
                sale.Customer.MemberID = req.CustomerId;
                sale.CustomerShopID    = req.CustomerShopId;   // tblSales.MemberShopID: the shop the customer is registered at
            }
            // MemberName stored in tblSales.Cust_name (DAL line 3846); null-fix: INSERT calls .Replace()
            sale.Customer.MemberName = req.WalkInName ?? "";

            // Salesperson — tblSales.employee_id (DAL line 8024)
            sale.Employee.ShopEmployeeID = req.SalespersonId;

            // Commission — frmSaleAndReturn.vb:10293-10302 (FillModel).
            // AddSalesPersonCommissionDetails (SaleAndReturnDAL.vb:11830) writes ONE row per
            // unique salesperson into tblSalespersonCommission (not per line item).
            bool commEnabled = GetShopConfig(shopId, "EnableSalespersonCommissiononNetSale")
                .Equals("true", StringComparison.OrdinalIgnoreCase);
            bool itemWiseSP = GetShopConfig(shopId, "ItemWiseSalesPersonOnSales")
                .Equals("true", StringComparison.OrdinalIgnoreCase);

            if (commEnabled)
            {
                if (itemWiseSP && req.Items != null && req.Items.Count > 0)
                {
                    // Aggregate distinct salesperson IDs across all line items.
                    // Items with SalespersonId=0 inherit the header salesperson (already applied
                    // per-line above); exclude them here so the header gets its own entry only
                    // if the header SP is also explicitly referenced by at least one line.
                    var distinctSpIds = req.Items
                        .Select(i => i.SalespersonId > 0 ? i.SalespersonId : req.SalespersonId)
                        .Where(id => id > 0)
                        .Distinct();

                    var commissions = new List<SalesPersonCommissionOnNetSale>();
                    foreach (int spId in distinctSpIds)
                    {
                        commissions.Add(new SalesPersonCommissionOnNetSale
                        {
                            EmployeeID          = spId,
                            CommisionPercentage = GetSalespersonCommissionPct(shopId, spId)
                        });
                    }
                    if (commissions.Count > 0)
                        sale.ListOfSalesPersonCommission = commissions;
                }
                else if (req.SalespersonId > 0)
                {
                    sale.ListOfSalesPersonCommission = new List<SalesPersonCommissionOnNetSale>
                    {
                        new SalesPersonCommissionOnNetSale
                        {
                            EmployeeID          = req.SalespersonId,
                            CommisionPercentage = GetSalespersonCommissionPct(shopId, req.SalespersonId)
                        }
                    };
                }
            }

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
            sale.Comments            = req.Comments            ?? "";
            sale.AdditionalComments  = req.AdditionalComments  ?? ""; // F1: ShowAdditionalComments — vb:8885

            // Mobile payment fields — frmSaleAndReturn.vb:37057 (AddMobiePayments)
            // FonePay: TransactionId + Vendor + IsManual
            sale.TransactionId = req.TransactionId ?? "";
            sale.Vendor        = req.Vendor        ?? "";
            sale.IsManual      = req.IsManual;
            // 543Pay: PaymentID + RespCode + RespMessage + ReferenceNum; mobile# → Comments
            sale.PaymentID    = req.PaymentId    ?? "";
            sale.RespCode     = req.RespCode     ?? "";
            sale.RespMessage  = req.RespMessage  ?? "";
            sale.ReferenceNum = req.ReferenceNum ?? "";
            if (!string.IsNullOrEmpty(req.MobileNum))
                sale.Comments = req.MobileNum;

            // Loyalty earned points — triggers MemberEarnedPointsDAL.Add() → tblMemberPointsEarnings
            // when EarnedPoints != 0 (SaleAndReturnDAL.vb:5377).
            sale.MemberPoints = new MemberEarnedPoints();
            if (req.EarnedPoints > 0 && req.CustomerId > 0)
            {
                sale.MemberPoints.MemberID        = req.CustomerId;
                sale.MemberPoints.CustomerShopID  = shopId;
                sale.MemberPoints.EarnedPoints    = req.EarnedPoints;
                sale.MemberPoints.EarningDateTime = DateTime.Now;
                sale.MemberPoints.Shop.ShopID     = shopId;
                sale.MemberPoints.ActivityLog.ShopID      = shopId;
                sale.MemberPoints.ActivityLog.LogGroup    = "POS API";
                sale.MemberPoints.ActivityLog.ScreenTitle = "Sale";
                sale.MemberPoints.ActivityLog.UserID      = userId;
            }

            // Loyalty points redemption — mirrors FillModel (frmSaleAndReturn.vb:10086-10116).
            // PointsRedemptionDAL.Add() is called inside SaleAndReturnDAL.Add() when RedeemedPoints > 0.
            if (req.RedeemedPoints > 0 && req.CustomerId > 0)
            {
                sale.MemberPointsRedeemed = new PointsRedemption
                {
                    Member_Id          = req.CustomerId,
                    Member_Shop_Id     = shopId,
                    Shop_Id            = shopId,
                    RedeemedPoints     = req.RedeemedPoints,
                    RedeemedValue      = (decimal)req.RedeemedValue,
                    BirthdayPoint      = req.BirthdayPoints,
                    One_Point_Value    = (decimal)req.OnePointValue,
                    RedemptionDateTime = DateTime.Now,
                };
            }

            // D1: BlockPointOnRedemption — frmSaleAndReturn.vb:10118-10127 (CR#7585)
            // When True and redemption occurred on this sale, zero all earned-point fields so
            // MemberEarnedPointsDAL.Add() is skipped (SaleAndReturnDAL.vb:5377 checks EarnedPoints != 0).
            // Candela condition: BlockPointOnRedemption=TRUE AND txtMarketingDiscount.Text > 0
            // txtMarketingDiscount carries the loyalty cash redemption — req.RedeemedPoints > 0 is equivalent.
            if (req.RedeemedPoints > 0
                && sale.MemberPoints.EarnedPoints != 0
                && CfgIs(CandelaBootstrap.GetRCMSConfig(), "BlockPointOnRedemption", "True"))
            {
                sale.MemberPoints.EarnedPoints           = 0;
                sale.MemberPoints.EarnedPointsValue      = 0;
                sale.MemberPoints.EarnedBonusPoints      = 0;
                sale.MemberPoints.EarnedBonusPointsValue = 0;
                sale.MemberPoints.EarnedPromoPoints      = 0;
                sale.MemberPoints.EarnedPromoPointsValue = 0;
            }

            // Employee / pharmacy (CR#6563 — mirrors frmCustomerEmployee / Ctrl+Q)
            sale.CustomerEmployee.EmployeeName              = req.EmployeeName   ?? "";
            sale.CustomerEmployee.RegisterationNo           = req.RegistrationNo ?? "";
            sale.CustomerEmployee.Department.ShopDepartmentID   = 0;
            sale.CustomerEmployee.Department.ShopDepartmentName = req.DepartmentName ?? "";
            sale.CustomerEmployee.DMNO                      = req.Dmno ?? "";
            sale.CustomerEmployee.DNO                       = req.Dno  ?? "";
            sale.CustomerEmployee.IsScanned                 = req.IsScanned;

            // Audit log
            sale.ActivityLog.LogGroup    = "POS API";
            sale.ActivityLog.ScreenTitle = "Sale";
            sale.ActivityLog.UserID      = userId;
            sale.ActivityLog.ShopID      = shopId;

            // Line items
            sale.ListOfSaleItems    = new List<SaleAndReturnItems>();
            sale.ListOfAssemblyItems = new List<SalesProductAssembly>();

            // Invoice adjustment spread over the lines the way Candela does it (frmSaleAndReturn.vb:7759, 7929):
            // each line takes |line total| ÷ Σ|line totals| of it, stored as a LINE TOTAL in product_adj_Discount.
            // RestrictAdjDistribution switches that off, leaving the column 0.
            bool   restrictAdj = CfgIs(CandelaBootstrap.GetRCMSConfig(), "RestrictAdjDistribution", "True");
            double adjBase     = req.Items.Sum(i => Math.Abs((i.UnitRate - i.UnitDiscount) * i.Quantity));

            foreach (var item in req.Items)
            {
                var line = new SaleAndReturnItems(0, item.ProductItemId, item.Quantity,
                                                  item.UnitRate, item.TaggedPrice);
                line.ProductCode                 = item.ProductCode ?? "";
                line.ProductBatchNo              = item.BatchNo ?? "";  // FIFO/FEFO batch tracking (CR #8125)
                // ── Candela's line-column convention (verified against rows the desktop app writes) ──────────
                // The request is in SINGLE units (quantity = pack qty × con_factor, rates per single). Candela
                // prices a Pack line per PACK, so every per-unit money column is scaled by f; qty, unit_price,
                // taged_price, product_discount_amount and avg_cost stay per single.
                bool   isPackLine = string.Equals(item.ConUnit, "Pack", StringComparison.OrdinalIgnoreCase)
                                    && item.ConFactor > 0;
                // Scaling applies only to lines the quote described in Candela's convention (price_after_discount
                // supplied); price-includes-VAT lines and older clients keep per-single values throughout.
                double f          = (isPackLine && item.PriceAfterDiscount.HasValue) ? item.ConFactor : 1.0;
                double lineTotal  = Math.Abs((item.UnitRate - item.UnitDiscount) * item.Quantity);

                line.VATValue                    = item.VatValue * f;             // pro_vat: tax per selling unit
                line.VatFactor                   = item.VatFactor;
                line.VatType                     = item.VatType ?? "";
                line.PriceIncludeVat             = item.PriceIncludeVat;
                line.ProductUnitDiscount         = item.UnitDiscount;             // per single (vb:7951)
                line.ProductDiscountID           = item.DiscountId;
                line.CustomerDiscountPerUnit     = item.CustomerDiscountPerUnit * f;  // mem_discount_amount: per selling unit
                line.CustomerDiscount            = item.CustomerDiscountPerUnit;      // CustomerDiscount: per single (vb:7890-7897)
                // product_mkt_Discount is a LINE TOTAL in Candela (weight × invoice discount, vb:7926); the quote
                // gives it per single.
                line.MarketingDiscountOnProduct  = item.MarketingDiscount * item.Quantity;
                line.AdjustmentOnProduct         = (restrictAdj || adjBase == 0)
                    ? 0.0
                    : Math.Round(req.AdjustmentAmount * lineTotal / adjBase, 4, MidpointRounding.AwayFromZero);
                line.LoyalityCashDiscount        = item.LoyaltyCashDiscount * f;
                line.AdditionalTaxpercent        = item.AdditionalTaxPercent;
                line.AdditionalTax               = item.AdditionalTax * f;
                line.DiscCategory                = item.DiscCategory ?? "";
                line.DiscountFromTagPrice        = item.DiscountFromTagPrice;
                line.LoyalityEarnedPoints        = 0;
                line.NestedItemId                = item.NestedItemId;
                // Candela writes the product's conversion factor on every line (vb:7936), Single or Pack.
                line.PackSize                    = item.ConFactor > 0 ? item.ConFactor : 0.0;
                // pro_weight: the pack factor on Pack lines, 0 on Single lines (vb:7939-7947). Candela's own
                // reads and reports key off it (Unit = Pack when pro_weight <> 0).
                line.Con_Factor                  = isPackLine ? item.ConFactor : 0.0;
                line.Con_Unit                    = item.ConUnit ?? "Single";
                line.AvgCost                     = item.AvgCost;
                line.PriceForDiscount            = item.UnitRate * f;
                if (item.PriceAfterDiscount.HasValue)
                {
                    // PriceAfterDiscount is without tax; the tax sits in pro_vat / VatChargedPerUnit (vb:12732-12738).
                    line.PriceAfterDiscount      = item.PriceAfterDiscount.Value * f;
                    line.VatChargedPerUnit       = item.VatValue * f;
                    line.VatOnRetailPrice        = item.VatOnRetailPrice ?? 0.0;
                }
                else
                {
                    // Price-includes-VAT lines (and older clients) keep the previous mapping.
                    line.PriceAfterDiscount      = item.NetAmount / (item.Quantity == 0 ? 1 : item.Quantity);
                    line.VatChargedPerUnit       = 0.0;
                    line.VatOnRetailPrice        = 0.0;
                }
                line.Employee.Shop.ShopID        = shopId;
                // frmSaleAndReturn.vb:9432-9435 — per-line SP when ItemWiseSalesPersonOnSales=TRUE;
                // falls back to header salesperson when the item carries no override.
                line.Employee.ShopEmployeeID     = item.SalespersonId > 0 ? item.SalespersonId : req.SalespersonId;
                sale.ListOfSaleItems.Add(line);

                // Assembly component substitutions — present only when cashier modified the
                // bundle contents via the Assembly tab.
                // DAL.ProcessAssemblyProductsAndSaveToDatabase (SaleAndReturnDAL.vb:14810)
                // iterates ListOfAssemblyItems and INSERTs rows where RowState = "Add".
                if (item.AssemblyItems != null && item.AssemblyItems.Count > 0)
                {
                    foreach (var a in item.AssemblyItems)
                    {
                        var asm = new SalesProductAssembly();
                        asm.AssemblyID    = item.ProductItemId;  // parent product (Product_Item_ID_Assembly)
                        asm.ProductIDPart = a.ProductItemId;     // child product  (Product_Item_ID_Part)
                        asm.Quantity      = a.Quantity;
                        asm.ProductPrice  = a.RetailPrice;
                        asm.RowState      = "Add";               // DAL: INSERT this row
                        // SaleID / ShopID are 0 here; DAL sets them inside ProcessAssembly…
                        sale.ListOfAssemblyItems.Add(asm);
                    }
                }
            }

            // Multi-batch deduction: build dtBatchDetails when any item carries batch_allocations.
            // SaleAndReturnDAL.Add() calls CommonDAL.InsertBatch() when ManageBatch=True AND
            // dtBatchDetails.Rows.Count > 0.  Columns must match CommonDAL.GetBatchQty output —
            // the inner Add() (line 4820) sets dr("TransactionDetailID") and line 4930 filters on
            // NestedItemId; both columns must exist or DataRow throws ArgumentException.
            bool hasAllocations = req.Items.Any(i => i.BatchAllocations?.Count > 0);
            if (hasAllocations)
            {
                var bdt = new DataTable();
                bdt.Columns.Add("ProductItemID",      typeof(int));
                bdt.Columns.Add("BatchNo",            typeof(string));
                bdt.Columns.Add("ExpiryDate",         typeof(DateTime));
                bdt.Columns.Add("Quantity",           typeof(double));
                bdt.Columns.Add("TransactionDetailID",typeof(int));
                bdt.Columns.Add("NestedItemId",       typeof(int));

                foreach (var item in req.Items)
                {
                    if (item.BatchAllocations == null) continue;
                    foreach (var alloc in item.BatchAllocations)
                    {
                        if (alloc.Qty <= 0) continue;
                        DateTime expiry;
                        if (!DateTime.TryParse(alloc.ExpiryDate, out expiry))
                            expiry = new DateTime(2099, 12, 31);
                        bdt.Rows.Add(item.ProductItemId, alloc.BatchNo ?? "", expiry, alloc.Qty, 0, 0);
                    }
                }
                if (bdt.Rows.Count > 0)
                    sale.dtBatchDetails = bdt;
            }

            return sale;
        }
    }
}
