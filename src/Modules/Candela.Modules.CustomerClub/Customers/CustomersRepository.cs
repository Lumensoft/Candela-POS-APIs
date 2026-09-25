using Candela.Modules.CustomerClub.Customers.Dtos;
using Candela.Platform.Data;
using Dapper;

namespace Candela.Modules.CustomerClub.Customers;

/// <summary>
/// Every statement is copied verbatim from the net48 CustomersController, parameter
/// names included, so the SQL text sent to the server is unchanged.
///
/// The ids are still MAX+1-per-shop rather than identity columns. That is not an
/// oversight to tidy up: Candela's CustomerDAL generates member_id, member_no and
/// member_closing_id the same way because those keys are (shop_id, id) composites that
/// HO replication merges across shops. An identity column here would collide the moment
/// a second shop replicated.
/// </summary>
public sealed class CustomersRepository(IDb db) : ICustomersRepository
{
    public async Task<CreateCustomerResponse> CreateAsync(CreateCustomerRequest req, int shopId,
        int userId, CancellationToken ct)
    {
        return await db.InTransactionAsync(async (con, trans) =>
        {
            // Auto-generate member_id and member_no (both MAX+1 scoped to shop)
            int memberId = await con.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT ISNULL(MAX(member_id),0)+1 FROM tblMemberInfo WHERE shop_id=@sid",
                new { sid = shopId }, trans, cancellationToken: ct));

            int memberNo = await con.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT ISNULL(MAX(member_no),0)+1 FROM tblMemberInfo WHERE shop_id=@sid",
                new { sid = shopId }, trans, cancellationToken: ct));

            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // Parse optional date fields — default start_date to today,
            // expiry_date to Dec 31 of the current year (matches Candela behaviour).
            DateTime startDate = DateTime.TryParse(req.StartDate, out DateTime sd)
                ? sd : DateTime.Today;
            DateTime expiryDate = DateTime.TryParse(req.ExpiryDate, out DateTime ed)
                ? ed : new DateTime(DateTime.Today.Year, 12, 31);
            DateTime openingDate = DateTime.TryParse(req.OpeningDate, out DateTime od)
                ? od : DateTime.Today;

            const string insertSql = @"
INSERT INTO tblMemberInfo
    (member_id, shop_id, member_no, member_name, member_type_id,
     phone_mobile, phone_Res, email, cust_Address, nic_no, InvoiceNo,
     allow_credit, credit_limit, card_duplicate_no,
     group_id, start_date, expiry_date,
     status, EnteredDate, EditedDate, enteredby)
VALUES
    (@mid, @sid, @mno, @nm, @mtid,
     @pm, @pr, @em, @addr, @nic, @ntn,
     @ac, @cl, 0,
     @gid, @sd, @ed,
     'Activate', @now, @now, @uid)";

            // Dates go over as the same "yyyy-MM-dd" / "yyyy-MM-dd HH:mm:ss" strings the
            // net48 endpoint sent. Binding them as DateTime would change the parameter
            // type on the wire and, with it, how SQL Server converts them.
            // Nulls: Dapper sends DBNull for a null reference or a null int?, which is
            // what the explicit "?? DBNull.Value" in the original did.
            await con.ExecuteAsync(new CommandDefinition(insertSql, new
            {
                mid = memberId,
                sid = shopId,
                mno = memberNo,
                nm = req.MemberName!.Trim(),
                mtid = req.MemberTypeId,
                pm = req.PhoneMobile,
                pr = req.PhoneRes,
                em = req.Email,
                addr = req.Address,
                nic = string.IsNullOrWhiteSpace(req.Cnic) ? null : req.Cnic.Trim(),
                ntn = string.IsNullOrWhiteSpace(req.Ntn) ? null : req.Ntn.Trim(),
                ac =req.AllowCredit ? 1 : 0,
                cl = req.CreditLimit,
                gid = req.GroupId,
                sd = startDate.ToString("yyyy-MM-dd"),
                ed = expiryDate.ToString("yyyy-MM-dd"),
                now,
                uid = userId
            }, trans, cancellationToken: ct));

            // Opening balance — stored as a seed row in tblMemberClosing
            // (same pattern as CustomerDAL.vb:1110). Only inserted when
            // allow_credit is ON and the cashier entered a non-zero balance.
            if (req.AllowCredit && req.OpeningBalance > 0)
            {
                // member_closing_id is NOT NULL, not an identity column, and part of
                // tblMemberClosing's PK — must be generated the same MAX+1-per-shop way
                // as member_id/member_no above (mirrors CustomerDAL.vb's GetMaxID call).
                int memberClosingId = await con.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT ISNULL(MAX(member_closing_id),0)+1 FROM tblMemberClosing WHERE shop_id=@sid",
                    new { sid = shopId }, trans, cancellationToken: ct));

                string clsDate = openingDate.ToString("yyyy-MM-dd HH:mm:ss");

                await con.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO tblMemberClosing " +
                    "(member_closing_id, member_id, shop_id, closing_date, closing_balance, transcation_time) " +
                    "VALUES (@mcid, @mid, @sid, @cd, @bal, @cd)",
                    new
                    {
                        mcid = memberClosingId,
                        mid = memberId,
                        sid = shopId,
                        cd = clsDate,
                        bal = req.OpeningBalance
                    }, trans, cancellationToken: ct));
            }

            return new CreateCustomerResponse
            {
                MemberId = memberId,
                MemberNo = memberNo,
                ShopId = shopId
            };
        }, ct);
    }

    public async Task<bool> UpdateAsync(int memberId, int shopId, UpdateCustomerRequest req,
        CancellationToken ct)
    {
        // Dates use the same "yyyy-MM-dd" strings as the insert; an unparsable / missing date
        // leaves the stored value alone (ISNULL) rather than blanking it.
        string? sd = DateTime.TryParse(req.StartDate, out DateTime s)  ? s.ToString("yyyy-MM-dd") : null;
        string? ed = DateTime.TryParse(req.ExpiryDate, out DateTime e) ? e.ToString("yyyy-MM-dd") : null;

        // EditedDate is bumped so the tablets' delta sync (?since=) picks the change up.
        const string sql = @"
UPDATE tblMemberInfo SET
    member_name  = @nm,
    phone_mobile = @pm,
    email        = @em,
    cust_Address = @addr,
    nic_no       = @nic,
    InvoiceNo    = @ntn,
    group_id     = @gid,
    start_date   = ISNULL(@sd, start_date),
    expiry_date  = ISNULL(@ed, expiry_date),
    EditedDate   = @now
WHERE member_id = @mid AND shop_id = @sid";

        int rows = await db.ExecuteAsync(sql, new
        {
            nm = req.MemberName!.Trim(),
            pm = string.IsNullOrWhiteSpace(req.PhoneMobile) ? null : req.PhoneMobile.Trim(),
            em = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim(),
            addr = string.IsNullOrWhiteSpace(req.Address) ? null : req.Address.Trim(),
            nic = string.IsNullOrWhiteSpace(req.Cnic) ? null : req.Cnic.Trim(),
            ntn = string.IsNullOrWhiteSpace(req.Ntn) ? null : req.Ntn.Trim(),
            gid = req.GroupId,
            sd,
            ed,
            now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            mid = memberId,
            sid = shopId
        }, ct);

        return rows > 0;
    }

    public async Task<CreditOutstandingResponse?> GetCreditOutstandingAsync(int memberId,
        CancellationToken ct)
    {
        // Mirrors SaleAndReturnDAL.GetCustomerCurrentOutStanding(ShopId, CustomerID) — Candela calls
        // this with the CUSTOMER'S OWN shop (SelectedCustomerShopID), not the viewing shop, and its
        // CalculateOutstanding sums tblSales.memberShopID / tblMemberReceipts.MemberShop_id — a tag for
        // which customer a transaction belongs to, stamped regardless of which physical shop it happened
        // at. So this must look the customer up and filter their transactions by their OWN shop_id, not
        // the caller's shop — a cross-shop customer must never be filtered out or 404 here; that's the
        // exact bug this endpoint used to have (m.shop_id = @sid rejected any other shop's customer).
        const string sql =
            "SELECT " +
            "  isnull(m.credit_limit, 0) AS credit_limit, " +
            "  isnull(m.allow_credit,  0) AS allow_credit, " +
            "  isnull((SELECT SUM(s.NT_amount) FROM tblSales s " +
            "           WHERE s.member_id = @mid AND s.isCreditSale = 1 AND s.memberShopID = m.shop_id), 0) " +
            "- isnull((SELECT SUM(r.amount) FROM tblMemberReceipts r " +
            "           WHERE r.member_id = @mid AND r.MemberShop_id = m.shop_id), 0) " +
            "  AS credit_outstanding " +
            "FROM tblMemberInfo m " +
            "WHERE m.member_id = @mid";

        // The original read the first row off a DataReader and ignored any further ones,
        // so this is QueryFirstOrDefault: member_id alone is not unique across shops, and
        // QuerySingleOrDefault would turn a cross-shop duplicate into a 500.
        var row = await db.QueryFirstOrDefaultAsync<Row>(sql, new { mid = memberId }, ct);
        if (row is null) return null;

        return new CreditOutstandingResponse
        {
            MemberId = memberId,
            CreditLimit = row.credit_limit,
            CreditOutstanding = row.credit_outstanding,
            CreditAvailable = Math.Max(0, row.credit_limit - row.credit_outstanding)
        };
    }

    public async Task<bool> UpdateCommentsAsync(int memberId, int shopId, string? comments,
        CancellationToken ct)
    {
        const string sql =
            "UPDATE tblMemberInfo SET comments=@c, EditedDate=@now " +
            "WHERE member_id=@mid AND shop_id=@sid";

        // The "?? empty string" is kept from the original: a null note blanks the field
        // rather than writing SQL NULL, so the column never flips between '' and NULL.
        int rows = await db.ExecuteAsync(sql, new
        {
            c = comments ?? "",
            now = DateTime.Now,
            mid = memberId,
            sid = shopId
        }, ct);

        return rows > 0;
    }

    // allow_credit is selected by the query but was never returned by the endpoint;
    // it is bound here only so the shape matches the SELECT.
    private sealed class Row
    {
        public decimal credit_limit { get; set; }
        public bool allow_credit { get; set; }
        public decimal credit_outstanding { get; set; }
    }
}
