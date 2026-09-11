using Candela.Modules.CustomerClub.GiftCards.Dtos;
using Candela.Platform.Data;
using Candela.Shared.Exceptions;
using Dapper;

namespace Candela.Modules.CustomerClub.GiftCards;

/// <summary>
/// Every SQL statement is copied verbatim from the net48 GiftCardsController, parameter
/// names included.
///
/// Two things here look like bugs and are not ours to fix while the contract is frozen:
/// tbldefCards.isActive is read as "is deactivated" (the column's name is the opposite of
/// its meaning), and the balance is SUM(Top_up_Amt) over the ledger, where redemptions are
/// stored as negative rows. Both are how Candela itself reads these tables.
///
/// The early returns the original made inside its transaction — card not found,
/// deactivated, insufficient balance — are thrown here instead. InTransactionAsync rolls
/// back on the way out, which is what the original's undisposed transaction did, and
/// ExceptionHandlingMiddleware turns each one into the same status and body as before.
/// </summary>
public sealed class GiftCardsRepository(IDb db) : IGiftCardsRepository
{
    /// <summary>
    /// The projection shared by both branches of the balance lookup. Split out in the
    /// original as `selectCols` and concatenated with a WHERE + GROUP BY, so it is kept
    /// that way — the two full statements must stay byte-identical to the originals.
    /// </summary>
    private const string BalanceSelectCols = @"
SELECT
    c.id                                                            AS card_id,
    c.Card_no                                                       AS card_no,
    isnull(c.Alternate_card_no, '')                                 AS display_card_no,
    isnull(c.card_status, '')                                       AS card_status,
    isnull(c.MemberName, '')                                        AS member_name,
    isnull(c.PhoneMobile, '')                                       AS phone_mobile,
    isnull(c.Is_Fixed_den, 0)                                       AS is_fixed_den,
    isnull(c.amount, 0)                                             AS fixed_amount,
    MAX(l.CardExpiryDate)                                           AS expiry_date,
    isnull(SUM(l.Top_up_Amt), 0)                                   AS available_balance,
    isnull(SUM(CASE WHEN l.Top_up_Amt > 0 THEN l.Top_up_Amt ELSE 0 END), 0) AS original_amount
FROM tbldefCards c
LEFT JOIN tblGiftCardLedger l ON l.cardid = c.id";

    public async Task<Dictionary<string, object?>?> GetBalanceAsync(string q, bool byPhone,
        CancellationToken ct)
    {
        string sql;
        object param;

        if (byPhone)
        {
            // Phone number search — mirrors frmPaymetOptions.vb:2050
            sql = BalanceSelectCols + @"
WHERE c.PhoneMobile = @q
GROUP BY c.id, c.Card_no, c.Alternate_card_no, c.card_status, c.MemberName, c.PhoneMobile, c.Is_Fixed_den, c.amount";
            param = new { q };
        }
        else
        {
            // Card number search — supports composite key, alternate_card_no, and numeric card_no
            string numericLookup = q;
            var parts = q.Split('-');
            if (parts.Length == 3 && parts[1].Length == 6 &&
                int.TryParse(parts[1], out int parsedCardNo))
                numericLookup = parsedCardNo.ToString();

            sql = BalanceSelectCols + @"
WHERE c.Alternate_card_no = @cardNo
   OR CAST(c.Card_no AS varchar) = @numericLookup
GROUP BY c.id, c.Card_no, c.Alternate_card_no, c.card_status, c.MemberName, c.PhoneMobile, c.Is_Fixed_den, c.amount";
            param = new { cardNo = q, numericLookup };
        }

        // The original filled a DataTable and took Rows[0] when there was one, so the
        // first row wins and extra rows are ignored — QueryRows plus FirstOrDefault, not
        // a single-row query that would throw when a card number matches twice.
        var rows = await db.QueryRowsAsync(sql, param, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<string?> GetUnsoldCardNoAsync(int shopId, CancellationToken ct)
    {
        // The POS API runs server-side with direct DB access, so replication does not apply here.
        // Candela's HOWebService path exists because desktop clients run against a local shop DB;
        // we always query the central DB directly (mirrors SaleAndReturnDAL.GetUnSoldGiftCard vb:17963).
        const string sql = @"
SELECT TOP 1
    tblDefShops.ShopMembershipCode + '-' +
    RIGHT('000000' + CAST(tbldefCards.Card_no AS varchar), 6)
    + '-' + tbldefCardType.code AS Card_No,
    isnull(tbldefCards.Alternate_card_no, '') AS Alternate_card_no
FROM tbldefCards
INNER JOIN tblDefShops    ON tbldefCards.Shop_id      = tblDefShops.shop_id
INNER JOIN tbldefCardType ON tbldefCards.Card_Type_id = tbldefCardType.id
LEFT OUTER JOIN tblGiftCardLedger ON tblGiftCardLedger.cardid = tbldefCards.ID
WHERE isnull(tblGiftCardLedger.Top_up_Amt, 0) = 0
  AND tbldefCards.Card_Status IS NULL
  AND tbldefCards.Shop_id = @shopId
ORDER BY tbldefCards.Card_Gen_Date DESC";

        var rows = await db.QueryRowsAsync(sql, new { shopId }, ct);
        if (rows.Count == 0) return null;

        // The original read only Card_No off the reader; Alternate_card_no is selected but
        // was never returned.
        return rows[0]["Card_No"]?.ToString();
    }

    public async Task<GiftCardTopupResponse> TopupAsync(GiftCardTopupRequest req, int shopId,
        int userId, string posCode, CancellationToken ct)
    {
        return await db.InTransactionAsync(async (con, trans) =>
        {
            // 1. Resolve card — support composite key ShopCode-PaddedCardNo-TypeCode
            string cardNoRaw = req.CardNo!;
            var cnParts = cardNoRaw.Split('-');
            string cnNumeric = cardNoRaw;
            if (cnParts.Length == 3 && cnParts[1].Length == 6 &&
                int.TryParse(cnParts[1], out int cnParsed))
                cnNumeric = cnParsed.ToString();

            var card = await con.QueryFirstOrDefaultAsync<CardRow>(new CommandDefinition(
                "SELECT TOP 1 id, Card_no FROM tbldefCards " +
                "WHERE Alternate_card_no = @cn OR CAST(Card_no AS varchar) = @cnNum",
                new { cn = cardNoRaw, cnNum = cnNumeric }, trans, cancellationToken: ct));

            if (card is null)
                throw new NotFoundException($"Gift card '{req.CardNo}' not found.");

            int cardId = card.id;
            int cardNo = card.Card_no;

            // 2. Generate ledger row ID (per-shop sequential, same as DAL line 758)
            int ledgerId = await con.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT ISNULL(MAX(id),0)+1 FROM tblGiftCardLedger WHERE sale_shop_id = @sid",
                new { sid = shopId }, trans, cancellationToken: ct));

            // 3. Insert ledger row — two statements, one with the expiry column and one
            //    without, exactly as the original branched on ExpDays.
            var ledgerParams = new DynamicParameters(new
            {
                id = ledgerId,
                cid = cardId,
                cno = cardNo,
                sid = shopId,
                pos = posCode,
                amt = req.TopupAmount,
                cash = req.CashAmount,
                card = req.CardAmount,
                uid = userId
            });

            string ledgerSql;
            if (req.ExpDays > 0)
            {
                ledgerSql =
                    "INSERT INTO tblGiftCardLedger" +
                    "(id,cardid,cardNo,Sale_shop_id,POS_code,sale_Date,Top_Up_Amt,Cash_amount,Card_amt,CardExpiryDate,SyncDate,EnteredBy,EnteredDate) " +
                    "VALUES (@id,@cid,@cno,@sid,@pos,GETDATE(),@amt,@cash,@card,@exp,GETDATE(),@uid,GETDATE())";
                ledgerParams.Add("@exp",
                    DateTime.Now.AddDays(req.ExpDays).Date.AddSeconds(86399));
            }
            else
            {
                ledgerSql =
                    "INSERT INTO tblGiftCardLedger" +
                    "(id,cardid,cardNo,Sale_shop_id,POS_code,sale_Date,Top_Up_Amt,Cash_amount,Card_amt,SyncDate,EnteredBy,EnteredDate) " +
                    "VALUES (@id,@cid,@cno,@sid,@pos,GETDATE(),@amt,@cash,@card,GETDATE(),@uid,GETDATE())";
            }

            await con.ExecuteAsync(new CommandDefinition(ledgerSql, ledgerParams, trans,
                cancellationToken: ct));

            // 4. Mark card Sold (DAL line 791)
            await con.ExecuteAsync(new CommandDefinition(
                "UPDATE tbldefCards SET card_status='Sold' WHERE id=@cid",
                new { cid = cardId }, trans, cancellationToken: ct));

            // 5. Cash accounting entry (DAL line 820-831)
            if (req.CashAmount > 0)
            {
                // No 'Gift Card' account head configured means no accounting row and no
                // error — the top-up still stands. That is the original's behaviour and
                // shops that never set the head up depend on it.
                int? accId = await con.ExecuteScalarAsync<int?>(new CommandDefinition(
                    "SELECT TOP 1 account_id FROM tblDefAccountHeads WHERE field_name='Gift Card'",
                    transaction: trans, cancellationToken: ct));

                if (accId.HasValue)
                {
                    int maxAccId = await con.ExecuteScalarAsync<int>(new CommandDefinition(
                        "SELECT ISNULL(MAX(account_Record_ID),0)+1 FROM tblAccountTransactions WHERE shop_id=@sid",
                        new { sid = shopId }, trans, cancellationToken: ct));

                    await con.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO tblAccountTransactions" +
                        "(account_Record_ID,shop_id,account_id,amount,transaction_date,comments,transaction_time,pos_code,EnteredBy,EnteredDate) " +
                        "VALUES(@rid,@sid,@aid,@amt,GETDATE(),N'Gift Card Sale(Cash)',GETDATE(),@pos,@uid,GETDATE())",
                        new
                        {
                            rid = maxAccId,
                            sid = shopId,
                            aid = accId.Value,
                            amt = req.CashAmount,
                            pos = posCode,
                            uid = userId
                        }, trans, cancellationToken: ct));
                }
            }

            return new GiftCardTopupResponse { LedgerId = ledgerId, CardId = cardId };
        }, ct);
    }

    public async Task<GiftCardValidateResponse?> ValidateAsync(GiftCardValidateRequest req,
        CancellationToken ct)
    {
        // Step 1: resolve card (id + isActive flag from tbldefCards)
        const string cardSql = @"
SELECT c.id, c.Card_no, isnull(c.Alternate_card_no,'') AS display_no,
       isnull(c.isActive, 0) AS is_deactivated
FROM tbldefCards c
WHERE c.Alternate_card_no = @cardNo
   OR CAST(c.Card_no AS varchar) = @cardNo";

        var card = await db.QueryFirstOrDefaultAsync<ValidateCardRow>(
            cardSql, new { cardNo = req.CardNo }, ct);

        if (card is null) return null;

        int cardId = card.id;
        bool isDeactivated = card.is_deactivated;
        string displayNo = card.display_no;

        if (isDeactivated)
            return new GiftCardValidateResponse
            {
                Valid = false,
                CardId = cardId,
                Reason = "Card has been deactivated."
            };

        // Step 2: balance — only sum ledger rows where the row's expiry has not passed.
        // CardExpiryDate on tblGiftCardLedger is per-row (set at top-up time when
        // ExpireAfterDays > 0); rows with NULL expiry are treated as non-expiring.
        // Source: frmPaymentOptions.vb:2253 WHERE clause.
        const string balSql = @"
SELECT isnull(SUM(l.Top_up_Amt), 0) AS balance
FROM tblGiftCardLedger l
WHERE l.cardid = @cid
  AND isnull(l.CardExpiryDate, GETDATE()) >= GETDATE()";

        double balance = await db.ExecuteScalarAsync<double?>(balSql, new { cid = cardId }, ct) ?? 0;

        if (balance <= 0)
            return new GiftCardValidateResponse
            {
                Valid = false,
                CardId = cardId,
                DisplayNo = displayNo,
                AvailableBalance = 0,          // int, not double — see the DTO
                Reason = "Card has no available balance."
            };

        if (req.Amount > 0 && (double)req.Amount > balance)
            return new GiftCardValidateResponse
            {
                Valid = false,
                CardId = cardId,
                DisplayNo = displayNo,
                AvailableBalance = balance,
                Reason = $"Insufficient balance. Requested {req.Amount}, available {balance}."
            };

        return new GiftCardValidateResponse
        {
            Valid = true,
            CardId = cardId,
            DisplayNo = displayNo,
            AvailableBalance = balance
        };
    }

    public async Task<GiftCardRedeemResponse> RedeemAsync(GiftCardRedeemRequest req, int shopId,
        int userId, string posCode, CancellationToken ct)
    {
        return await db.InTransactionAsync(async (con, trans) =>
        {
            // 1. Verify card exists and resolve Card_no (printed number)
            var card = await con.QueryFirstOrDefaultAsync<RedeemCardRow>(new CommandDefinition(
                "SELECT Card_no, isnull(isActive,0) AS is_deactivated FROM tbldefCards WHERE id = @cid",
                new { cid = req.CardId }, trans, cancellationToken: ct));

            if (card is null)
                throw new NotFoundException($"Gift card id {req.CardId} not found.");

            int cardNo = card.Card_no;
            bool isDeactivated = card.is_deactivated;

            if (isDeactivated)
                throw new ValidationException("Card has been deactivated.");

            // 2. Check available balance
            const string balSql =
                "SELECT isnull(SUM(Top_up_Amt),0) FROM tblGiftCardLedger " +
                "WHERE cardid = @cid AND isnull(CardExpiryDate,GETDATE()) >= GETDATE()";

            double balance = await con.ExecuteScalarAsync<double>(new CommandDefinition(
                balSql, new { cid = req.CardId }, trans, cancellationToken: ct));

            if ((double)req.Amount > balance)
                throw new ValidationException(
                    $"Insufficient balance. Requested {req.Amount}, available {balance}.");

            // 3. New ledger row id (scoped to shop, same as Add() DAL:4536)
            int ledgerId = await con.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT ISNULL(MAX(id),0)+1 FROM tblGiftCardLedger WHERE sale_shop_id=@sid",
                new { sid = shopId }, trans, cancellationToken: ct));

            // 4. Insert redemption row — amount is NEGATIVE (DAL:4564: @item.gftCardAmt * -1)
            const string insSql = @"
INSERT INTO tblGiftCardLedger
    (id, cardid, cardNo, Sale_shop_id, POS_code, sale_Date,
     Top_Up_Amt, Cash_amount, Card_amt, sale_id, SyncDate, EnteredBy, EnteredDate)
VALUES
    (@id, @cid, @cno, @sid, @pos, GETDATE(),
     @amt, 0, 0, @saleId, GETDATE(), @uid, GETDATE())";

            await con.ExecuteAsync(new CommandDefinition(insSql, new
            {
                id = ledgerId,
                cid = req.CardId,
                cno = cardNo,
                sid = shopId,
                pos = posCode,
                amt = -(double)req.Amount,                            // negative = redemption
                saleId = req.SaleId > 0 ? (int?)req.SaleId : null,
                uid = userId
            }, trans, cancellationToken: ct));

            double newBalance = balance - (double)req.Amount;

            return new GiftCardRedeemResponse
            {
                Success = true,
                CardId = req.CardId,
                LedgerId = ledgerId,
                Redeemed = req.Amount,
                NewBalance = newBalance
            };
        }, ct);
    }

    // Column names match the SELECT aliases so Dapper binds by name without mapping.
    private sealed class CardRow
    {
        public int id { get; set; }
        public int Card_no { get; set; }
    }

    private sealed class ValidateCardRow
    {
        public int id { get; set; }
        public int Card_no { get; set; }
        public string display_no { get; set; } = "";
        public bool is_deactivated { get; set; }
    }

    /// <summary>
    /// The original read the deactivation flag positionally (rdr[1]) because the
    /// isnull(...) had no alias. Dapper binds by name, so the alias is added — the only
    /// change to that statement, and it does not alter which rows or values come back.
    /// </summary>
    private sealed class RedeemCardRow
    {
        public int Card_no { get; set; }
        public bool is_deactivated { get; set; }
    }
}
