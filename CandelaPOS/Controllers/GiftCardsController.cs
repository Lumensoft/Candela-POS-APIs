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
    [RoutePrefix("api/gift-cards")]
    public class GiftCardsController : ApiController
    {
        // GET api/gift-cards/{no}/balance
        // Looks up a gift card by Alternate_card_no (display number) or Card_no (numeric).
        // Balance = SUM(Top_up_Amt): top-ups are positive, redemptions are stored as negative.
        // frmSaleAndReturn.vb:13839 — source of balance SQL pattern.
        [HttpGet, Route("{cardNo}/balance")]
        public HttpResponseMessage GetBalance(string cardNo)
        {
            CandelaBootstrap.PrepareRequest();
            try
            {
                const string sql = @"
SELECT
    c.id                                  AS card_id,
    c.Card_no                             AS card_no,
    isnull(c.Alternate_card_no, '')       AS display_card_no,
    isnull(c.amount, 0)                   AS original_amount,
    isnull(c.card_status, '')             AS card_status,
    isnull(c.CardExpiryDate, NULL)        AS expiry_date,
    isnull(c.MemberName, '')              AS member_name,
    isnull(c.PhoneMobile, '')             AS phone_mobile,
    isnull(SUM(l.Top_up_Amt), 0)         AS available_balance
FROM tbldefCards c
LEFT JOIN tblGiftCardLedger l ON l.cardid = c.id
WHERE c.Alternate_card_no = @cardNo
   OR CAST(c.Card_no AS varchar) = @cardNo
GROUP BY c.id, c.Card_no, c.Alternate_card_no, c.amount,
         c.card_status, c.CardExpiryDate, c.MemberName, c.PhoneMobile";

                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var cmd = new SqlCommand(sql, con);
                    cmd.Parameters.AddWithValue("@cardNo", cardNo);

                    using (var dt = new DataTable())
                    {
                        new SqlDataAdapter(cmd).Fill(dt);

                        if (dt.Rows.Count == 0)
                            return Request.CreateResponse(HttpStatusCode.NotFound,
                                new { error = $"Gift card '{cardNo}' not found." });

                        var row  = dt.Rows[0];
                        var data = new Dictionary<string, object>();
                        foreach (DataColumn col in dt.Columns)
                            data[col.ColumnName] = row[col] == DBNull.Value ? null : row[col];

                        return Request.CreateResponse(HttpStatusCode.OK,
                            new { success = true, data });
                    }
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }
        }

        // POST api/gift-cards/topup
        // Tops up one gift card: inserts a positive tblGiftCardLedger row, marks card Sold,
        // and posts a cash debit to tblAccountTransactions.
        // Mirrors GiftCardGenerationDAL.SaleGiftCardMultiple() logic (line 728).
        // Body: { card_no, topup_amount, cash_amount, card_amount, exp_days?,
        //         member_name?, phone_mobile?, credit_card_id? }
        [HttpPost, Route("topup")]
        public HttpResponseMessage Topup([FromBody] GiftCardTopupRequest req)
        {
            if (req == null)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "Request body is required" });

            if (string.IsNullOrWhiteSpace(req.CardNo))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "card_no is required" });

            if (req.TopupAmount <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "topup_amount must be > 0" });

            CandelaBootstrap.PrepareRequest();

            int    userId  = (int)   Request.Properties["user_id"];
            int    shopId  = (int)   Request.Properties["shop_id"];
            string posCode = (string)Request.Properties["pos_code"];

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var trans = con.BeginTransaction();
                try
                {
                    // 1. Resolve card
                    var lookupCmd = new SqlCommand(
                        "SELECT TOP 1 id, Card_no FROM tbldefCards " +
                        "WHERE Alternate_card_no = @cn OR CAST(Card_no AS varchar) = @cn",
                        con, trans);
                    lookupCmd.Parameters.AddWithValue("@cn", req.CardNo);

                    int cardId  = 0;
                    int cardNo  = 0;
                    using (var rdr = lookupCmd.ExecuteReader())
                    {
                        if (!rdr.Read())
                            return Request.CreateResponse(HttpStatusCode.NotFound,
                                new { error = $"Gift card '{req.CardNo}' not found." });
                        cardId = Convert.ToInt32(rdr["id"]);
                        cardNo = Convert.ToInt32(rdr["Card_no"]);
                    }

                    // 2. Generate ledger row ID (per-shop sequential, same as DAL line 758)
                    var idCmd = new SqlCommand(
                        "SELECT ISNULL(MAX(id),0)+1 FROM tblGiftCardLedger WHERE sale_shop_id = @sid",
                        con, trans);
                    idCmd.Parameters.AddWithValue("@sid", shopId);
                    int ledgerId = Convert.ToInt32(idCmd.ExecuteScalar());

                    // 3. Insert ledger row
                    string insertLedger;
                    if (req.ExpDays > 0)
                    {
                        string expDate = DateTime.Now.AddDays(req.ExpDays).ToString("yyyy/MM/dd") + " 23:59:59";
                        insertLedger =
                            $"INSERT INTO tblGiftCardLedger" +
                            "(id,cardid,cardNo,Sale_shop_id,POS_code,sale_Date,Top_Up_Amt,Cash_amount,Card_amt,CardExpiryDate,SyncDate,EnteredBy,EnteredDate) " +
                            $"VALUES ({ledgerId},{cardId},{cardNo},{shopId},'{posCode}',GETDATE()," +
                            $"{req.TopupAmount},{req.CashAmount},{req.CardAmount},'{expDate}',GETDATE(),{userId},GETDATE())";
                    }
                    else
                    {
                        insertLedger =
                            $"INSERT INTO tblGiftCardLedger" +
                            "(id,cardid,cardNo,Sale_shop_id,POS_code,sale_Date,Top_Up_Amt,Cash_amount,Card_amt,SyncDate,EnteredBy,EnteredDate) " +
                            $"VALUES ({ledgerId},{cardId},{cardNo},{shopId},'{posCode}',GETDATE()," +
                            $"{req.TopupAmount},{req.CashAmount},{req.CardAmount},GETDATE(),{userId},GETDATE())";
                    }
                    new SqlCommand(insertLedger, con, trans).ExecuteNonQuery();

                    // 4. Mark card Sold + optional member info (DAL line 791)
                    var updCard = new SqlCommand(
                        "UPDATE tbldefCards SET card_status='Sold', " +
                        "MemberName=@mn, PhoneMobile=@pm WHERE id=@cid",
                        con, trans);
                    updCard.Parameters.AddWithValue("@mn",  req.MemberName  ?? "");
                    updCard.Parameters.AddWithValue("@pm",  req.PhoneMobile ?? "");
                    updCard.Parameters.AddWithValue("@cid", cardId);
                    updCard.ExecuteNonQuery();

                    // 5. Cash accounting entry (DAL line 820-831)
                    if (req.CashAmount > 0)
                    {
                        var accCmd = new SqlCommand(
                            "SELECT TOP 1 account_id FROM tblDefAccountHeads WHERE field_name='Gift Card'",
                            con, trans);
                        var accIdObj = accCmd.ExecuteScalar();
                        if (accIdObj != null)
                        {
                            int accId = Convert.ToInt32(accIdObj);

                            var maxAccCmd = new SqlCommand(
                                "SELECT ISNULL(MAX(account_Record_ID),0)+1 FROM tblAccountTransactions WHERE shop_id=@sid",
                                con, trans);
                            maxAccCmd.Parameters.AddWithValue("@sid", shopId);
                            int maxAccId = Convert.ToInt32(maxAccCmd.ExecuteScalar());

                            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                            string insertAcc =
                                $"INSERT INTO tblAccountTransactions" +
                                "(account_Record_ID,shop_id,account_id,amount,transaction_date,comments,transaction_time,pos_code,EnteredBy,EnteredDate) " +
                                $"VALUES({maxAccId},{shopId},{accId},{req.CashAmount}," +
                                $"'{now}',N'Gift Card Sale(Cash)','{now}','{posCode}',{userId},'{now}')";
                            new SqlCommand(insertAcc, con, trans).ExecuteNonQuery();
                        }
                    }

                    trans.Commit();

                    return Request.CreateResponse(HttpStatusCode.OK,
                        new { success = true, data = new { ledger_id = ledgerId, card_id = cardId } });
                }
                catch (Exception ex)
                {
                    trans.Rollback();
                    return Request.CreateResponse(HttpStatusCode.InternalServerError,
                        new { error = ex.Message });
                }
            }
        }
    }

        // POST api/gift-cards/validate
        // Checks that a gift card is active, not expired, and has sufficient balance.
        // Returns card_id (needed to pass into POST /api/sales gift_card_payments[]).
        // Balance = SUM(Top_up_Amt): top-ups positive, redemptions negative.
        // Source: frmPaymentOptions.vb:2253 balance query pattern.
        [HttpPost, Route("validate")]
        public HttpResponseMessage Validate([FromBody] GiftCardValidateRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.CardNo))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "card_no is required" });

            CandelaBootstrap.PrepareRequest();
            try
            {
                // Step 1: resolve card (id + isActive flag from tbldefCards)
                const string cardSql = @"
SELECT c.id, c.Card_no, isnull(c.Alternate_card_no,'') AS display_no,
       isnull(c.isActive, 0) AS is_deactivated
FROM tbldefCards c
WHERE c.Alternate_card_no = @cardNo
   OR CAST(c.Card_no AS varchar) = @cardNo";

                int    cardId        = 0;
                bool   isDeactivated = false;
                string displayNo     = "";

                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var cardCmd = new SqlCommand(cardSql, con);
                    cardCmd.Parameters.AddWithValue("@cardNo", req.CardNo);
                    using (var rdr = cardCmd.ExecuteReader())
                    {
                        if (!rdr.Read())
                            return Request.CreateResponse(HttpStatusCode.NotFound,
                                new { valid = false, reason = $"Gift card '{req.CardNo}' not found." });
                        cardId        = Convert.ToInt32(rdr["id"]);
                        isDeactivated = Convert.ToBoolean(rdr["is_deactivated"]);
                        displayNo     = rdr["display_no"].ToString();
                    }

                    if (isDeactivated)
                        return Request.CreateResponse(HttpStatusCode.OK,
                            new { valid = false, card_id = cardId, reason = "Card has been deactivated." });

                    // Step 2: balance — only sum ledger rows where the row's expiry has not passed.
                    // CardExpiryDate on tblGiftCardLedger is per-row (set at top-up time when
                    // ExpireAfterDays > 0); rows with NULL expiry are treated as non-expiring.
                    // Source: frmPaymentOptions.vb:2253 WHERE clause.
                    const string balSql = @"
SELECT isnull(SUM(l.Top_up_Amt), 0) AS balance
FROM tblGiftCardLedger l
WHERE l.cardid = @cid
  AND isnull(l.CardExpiryDate, GETDATE()) >= GETDATE()";

                    var balCmd = new SqlCommand(balSql, con);
                    balCmd.Parameters.AddWithValue("@cid", cardId);
                    var balObj  = balCmd.ExecuteScalar();
                    double balance = balObj != null && balObj != DBNull.Value
                        ? Convert.ToDouble(balObj) : 0;

                    if (balance <= 0)
                        return Request.CreateResponse(HttpStatusCode.OK,
                            new { valid = false, card_id = cardId, display_no = displayNo,
                                  available_balance = 0, reason = "Card has no available balance." });

                    if (req.Amount > 0 && (double)req.Amount > balance)
                        return Request.CreateResponse(HttpStatusCode.OK,
                            new { valid = false, card_id = cardId, display_no = displayNo,
                                  available_balance = balance,
                                  reason = $"Insufficient balance. Requested {req.Amount}, available {balance}." });

                    return Request.CreateResponse(HttpStatusCode.OK,
                        new { valid = true, card_id = cardId, display_no = displayNo,
                              available_balance = balance });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }
        }

        // POST api/gift-cards/redeem
        // Records a standalone gift card redemption (negative tblGiftCardLedger row).
        // Use when the redemption must be recorded independently from a sale,
        // e.g. gift-card-to-cash refund, manual adjustment, or testing.
        // For redemptions tied to a sale, pass gift_card_payments[] in POST /api/sales —
        // SaleAndReturnDAL.Add() writes the ledger entry as part of the sale transaction.
        // Source: SaleAndReturnDAL.vb:4536 redemption INSERT pattern.
        [HttpPost, Route("redeem")]
        public HttpResponseMessage Redeem([FromBody] GiftCardRedeemRequest req)
        {
            if (req == null || req.CardId <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "card_id is required" });

            if (req.Amount <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "amount must be > 0" });

            CandelaBootstrap.PrepareRequest();

            int    userId  = (int)   Request.Properties["user_id"];
            int    shopId  = (int)   Request.Properties["shop_id"];
            string posCode = (string)Request.Properties["pos_code"];

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var trans = con.BeginTransaction();
                try
                {
                    // 1. Verify card exists and resolve Card_no (printed number)
                    var lookupCmd = new SqlCommand(
                        "SELECT Card_no, isnull(isActive,0) FROM tbldefCards WHERE id = @cid",
                        con, trans);
                    lookupCmd.Parameters.AddWithValue("@cid", req.CardId);

                    int  cardNo       = 0;
                    bool isDeactivated = false;
                    using (var rdr = lookupCmd.ExecuteReader())
                    {
                        if (!rdr.Read())
                            return Request.CreateResponse(HttpStatusCode.NotFound,
                                new { error = $"Gift card id {req.CardId} not found." });
                        cardNo        = Convert.ToInt32(rdr["Card_no"]);
                        isDeactivated = Convert.ToBoolean(rdr[1]);
                    }

                    if (isDeactivated)
                        return Request.CreateResponse(HttpStatusCode.BadRequest,
                            new { error = "Card has been deactivated." });

                    // 2. Check available balance
                    const string balSql =
                        "SELECT isnull(SUM(Top_up_Amt),0) FROM tblGiftCardLedger " +
                        "WHERE cardid = @cid AND isnull(CardExpiryDate,GETDATE()) >= GETDATE()";
                    var balCmd = new SqlCommand(balSql, con, trans);
                    balCmd.Parameters.AddWithValue("@cid", req.CardId);
                    double balance = Convert.ToDouble(balCmd.ExecuteScalar());

                    if ((double)req.Amount > balance)
                        return Request.CreateResponse(HttpStatusCode.BadRequest,
                            new { error = $"Insufficient balance. Requested {req.Amount}, available {balance}." });

                    // 3. New ledger row id (scoped to shop, same as Add() DAL:4536)
                    var idCmd = new SqlCommand(
                        "SELECT ISNULL(MAX(id),0)+1 FROM tblGiftCardLedger WHERE sale_shop_id=@sid",
                        con, trans);
                    idCmd.Parameters.AddWithValue("@sid", shopId);
                    int ledgerId = Convert.ToInt32(idCmd.ExecuteScalar());

                    // 4. Insert redemption row — amount is NEGATIVE (DAL:4564: @item.gftCardAmt * -1)
                    var insCmd = new SqlCommand(@"
INSERT INTO tblGiftCardLedger
    (id, cardid, cardNo, Sale_shop_id, POS_code, sale_Date,
     Top_Up_Amt, Cash_amount, Card_amt, sale_id, SyncDate, EnteredBy, EnteredDate)
VALUES
    (@id, @cid, @cno, @sid, @pos, GETDATE(),
     @amt, 0, 0, @saleId, GETDATE(), @uid, GETDATE())",
                        con, trans);
                    insCmd.Parameters.AddWithValue("@id",     ledgerId);
                    insCmd.Parameters.AddWithValue("@cid",    req.CardId);
                    insCmd.Parameters.AddWithValue("@cno",    cardNo);
                    insCmd.Parameters.AddWithValue("@sid",    shopId);
                    insCmd.Parameters.AddWithValue("@pos",    posCode);
                    insCmd.Parameters.AddWithValue("@amt",    -(double)req.Amount);   // negative = redemption
                    insCmd.Parameters.AddWithValue("@saleId", req.SaleId > 0 ? (object)req.SaleId : DBNull.Value);
                    insCmd.Parameters.AddWithValue("@uid",    userId);
                    insCmd.ExecuteNonQuery();

                    trans.Commit();

                    double newBalance = balance - (double)req.Amount;
                    return Request.CreateResponse(HttpStatusCode.OK,
                        new { success = true, card_id = req.CardId, ledger_id = ledgerId,
                              redeemed = req.Amount, new_balance = newBalance });
                }
                catch (Exception ex)
                {
                    trans.Rollback();
                    return Request.CreateResponse(HttpStatusCode.InternalServerError,
                        new { error = ex.Message });
                }
            }
        }
    }

    public class GiftCardTopupRequest
    {
        public string CardNo      { get; set; }
        public decimal TopupAmount { get; set; }
        public decimal CashAmount  { get; set; }
        public decimal CardAmount  { get; set; }
        public int     ExpDays     { get; set; }
        public string  MemberName  { get; set; }
        public string  PhoneMobile { get; set; }
        public int     CreditCardId { get; set; }
    }

    public class GiftCardValidateRequest
    {
        public string  CardNo { get; set; }
        public decimal Amount { get; set; }  // 0 = just check card is valid; >0 = also check sufficiency
    }

    public class GiftCardRedeemRequest
    {
        public int     CardId { get; set; }
        public decimal Amount { get; set; }
        public int     SaleId { get; set; }  // optional — link to the sale this redemption belongs to
    }
}
