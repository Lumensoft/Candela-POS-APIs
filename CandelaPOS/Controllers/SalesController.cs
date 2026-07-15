using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
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
    isnull(m.member_name, s.cust_name)           AS customer_name,
    m.member_id                                   AS customer_id,
    isnull(m.phone_Mobile, '')                    AS customer_phone,
    isnull(e.field_name, '')                      AS salesperson_name,
    s.GT_amount                                   AS gross_total,
    s.NT_amount                                   AS net_total,
    isnull(s.Mark_discount, 0)                    AS marketing_discount,
    isnull(s.vat, 0)                              AS vat,
    isnull(s.Cash_amt, 0)                         AS cash_amt,
    isnull(s.Card_amt, 0)                         AS card_amt,
    isnull(s.isCreditSale, 0)                     AS is_credit_sale,
    isnull(s.isMixSale, 0)                        AS is_mix_sale,
    CASE WHEN isnull(s.SaleReturningNo, 0) > 0
         THEN 1 ELSE 0 END                        AS is_return,
    isnull(s.SaleReturningNo, 0)                  AS return_of_sale_id,
    isnull(s.invoice_type, '')                    AS invoice_type,
    isnull(s.cust_bal, 0)                         AS balance
FROM tblSales s
LEFT JOIN tblMemberInfo m
    ON  m.member_id = s.member_id
    AND m.shop_id   = s.MemberShopID
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
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // GET api/sales/{id}
        // Returns sale header + line items for the invoice detail panel.
        [HttpGet, Route("{id:int}")]
        public HttpResponseMessage GetSale(int id)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];

            try
            {
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();

                    var hdrCmd = new SqlCommand(@"
SELECT
    s.sale_id, s.invoice_no, s.sale_date,
    isnull(m.member_name, s.cust_name)           AS customer_name,
    m.member_id                                   AS customer_id,
    isnull(e.field_name, '')                      AS salesperson_name,
    s.GT_amount                                   AS gross_total,
    s.NT_amount                                   AS net_total,
    isnull(s.Mark_discount, 0)                    AS marketing_discount,
    isnull(s.vat, 0)                              AS vat,
    isnull(s.Cash_amt, 0)                         AS cash_amt,
    isnull(s.Card_amt, 0)                         AS card_amt,
    isnull(s.isCreditSale, 0)                     AS is_credit_sale,
    CASE WHEN isnull(s.SaleReturningNo, 0) > 0
         THEN 1 ELSE 0 END                        AS is_return,
    isnull(s.SaleReturningNo, 0)                  AS return_of_sale_id,
    isnull(s.IsVoided, 0)                         AS is_voided
FROM tblSales s
LEFT JOIN tblMemberInfo m
    ON  m.member_id = s.member_id AND m.shop_id = s.MemberShopID
LEFT JOIN tblDefShopEmployees e
    ON  e.shop_employee_id = s.employee_id
WHERE s.sale_id = @saleId AND s.shop_id = @shopId", con);
                    hdrCmd.Parameters.AddWithValue("@saleId", id);
                    hdrCmd.Parameters.AddWithValue("@shopId", shopId);

                    Dictionary<string, object> header = null;
                    using (var dt = new DataTable())
                    {
                        new SqlDataAdapter(hdrCmd).Fill(dt);
                        if (dt.Rows.Count == 0)
                            return Request.CreateResponse(HttpStatusCode.NotFound,
                                new { error = "Sale not found" });
                        header = new Dictionary<string, object>();
                        foreach (DataColumn col in dt.Columns)
                            header[col.ColumnName] = dt.Rows[0][col] == DBNull.Value ? null : dt.Rows[0][col];
                    }

                    var liCmd = new SqlCommand(@"
SELECT
    sli.product_item_id,
    isnull(pd.item_name,    '') AS item_name,
    isnull(pd.product_code, '') AS product_code,
    sli.qty                                              AS quantity,
    sli.unit_price                                       AS unit_rate,
    sli.unit_price * abs(sli.qty)                        AS net_amount,
    isnull(sli.product_discount_amount, 0)               AS unit_discount,
    isnull(sli.pro_vat, 0)                               AS vat,
    isnull(sli.is_return_item, 0)                        AS is_return_item
FROM tblSalesLineItems sli
JOIN tblProductItem pi ON pi.Product_Item_ID = sli.product_item_id
JOIN tblDefProducts pd ON pd.product_id      = pi.product_id
WHERE sli.sale_id = @saleId
ORDER BY sli.sale_line_item_id", con);
                    liCmd.Parameters.AddWithValue("@saleId", id);

                    var items = new List<Dictionary<string, object>>();
                    using (var dt = new DataTable())
                    {
                        new SqlDataAdapter(liCmd).Fill(dt);
                        foreach (DataRow row in dt.Rows)
                        {
                            var dict = new Dictionary<string, object>();
                            foreach (DataColumn col in dt.Columns)
                                dict[col.ColumnName] = row[col] == DBNull.Value ? null : row[col];
                            items.Add(dict);
                        }
                    }

                    return Request.CreateResponse(HttpStatusCode.OK,
                        new { success = true, data = header, items });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred.", detail = ex.Message });
            }
        }

        // DELETE api/sales/{id}
        // Soft-voids the invoice: zeroes all amounts, sets IsVoided=1, reverses inventory.
        // The tblSales row is KEPT (IsVoided=1). Mirrors SaleAndReturnDAL.VoidSale() at
        // line 15240, which is triggered by the Void button in Candela's sale screen.
        [HttpDelete, Route("{id:int}")]
        public HttpResponseMessage VoidSale(int id)
        {
            CandelaBootstrap.PrepareRequest();

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
            CandelaBootstrap.PrepareRequest();

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

            CandelaBootstrap.PrepareRequest();

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
                }

                // Build the full model just like a new sale, then set SaleID so the DAL
                // uses Update path (SaleAndReturnDAL.vb:4634 — delta line items, re-inventory).
                // Line items have SaleDetailID=0, so the DAL deletes ALL existing items and
                // re-inserts the new set (strSaleItemIDs="0" → DELETE NOT IN (0) removes all).
                var sale = BuildModel(req, userId, shopId, posCode, userName);
                sale.SaleID = id;
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
             || msg.IndexOf("period",            StringComparison.OrdinalIgnoreCase) >= 0)
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

            CandelaBootstrap.PrepareRequest();

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
                if (req.GiftCardAmount > 0 && !string.IsNullOrWhiteSpace(req.GiftCardNo))
                {
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
                        using (var gcRdr = gcCmd.ExecuteReader())
                        {
                            if (gcRdr.Read())
                            {
                                sale.ListOfGftcardPaymentDetails.Add(new gftCardPaymentDetail
                                {
                                    gftCardID  = Convert.ToInt32(gcRdr["id"]),
                                    GftCardNo  = Convert.ToInt32(gcRdr["Card_no"]),
                                    gftCardAmt = (decimal)req.GiftCardAmount,
                                });
                            }
                        }
                    }
                }

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
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message, detail = ex.InnerException?.Message });
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
                // is_return_item does not exist on tblSales (it's on tblSalesLineItems).
                // Returns are credit sales with negative NT_amount, so SUM already nets them out.
                var balCmd = new SqlCommand(
                    "SELECT " +
                    "  ISNULL((SELECT SUM(NT_amount) FROM tblSales " +
                    "           WHERE member_id = @mid AND isCreditSale = 1 AND shop_id = @sid), 0) " +
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
            sale.Comments            = req.Comments ?? "";

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
            sale.ListOfSaleItems    = new List<SaleAndReturnItems>();
            sale.ListOfAssemblyItems = new List<SalesProductAssembly>();

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

            return sale;
        }
    }
}
