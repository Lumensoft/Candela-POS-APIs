using System;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using CandelaPOS.Shared.Data;
using CandelaPOS.Shared.Errors;

namespace CandelaPOS.Features.Customers
{
    [RoutePrefix("api/customers")]
    public class CustomersController : ApiController
    {
        // POST api/customers
        // Creates a new walk-in customer captured at POS (Customer modal).
        // Inserts into tblMemberInfo. member_no and member_id are auto-generated
        // as MAX+1 per shop, matching the pattern in CustomerDAL.
        // Returns the new member_id so the caller can use it for the sale.
        [HttpPost, Route("")]
        public HttpResponseMessage CreateCustomer([FromBody] CreateCustomerRequest req)
        {
            if (req == null)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "Request body is required" });

            if (string.IsNullOrWhiteSpace(req.MemberName))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "member_name is required" });

            if (req.MemberTypeId <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "member_type_id is required" });


            int    userId  = (int)   Request.Properties["user_id"];
            int    shopId  = (int)   Request.Properties["shop_id"];

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var trans = con.BeginTransaction();
                try
                {
                    // Auto-generate member_id and member_no (both MAX+1 scoped to shop)
                    var idCmd = new SqlCommand(
                        "SELECT ISNULL(MAX(member_id),0)+1 FROM tblMemberInfo WHERE shop_id=@sid",
                        con, trans);
                    idCmd.Parameters.AddWithValue("@sid", shopId);
                    int memberId = Convert.ToInt32(idCmd.ExecuteScalar());

                    var noCmd = new SqlCommand(
                        "SELECT ISNULL(MAX(member_no),0)+1 FROM tblMemberInfo WHERE shop_id=@sid",
                        con, trans);
                    noCmd.Parameters.AddWithValue("@sid", shopId);
                    int memberNo = Convert.ToInt32(noCmd.ExecuteScalar());

                    string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                    // Parse optional date fields — default start_date to today,
                    // expiry_date to Dec 31 of the current year (matches Candela behaviour).
                    DateTime startDate  = DateTime.TryParse(req.StartDate,  out DateTime sd)
                        ? sd : DateTime.Today;
                    DateTime expiryDate = DateTime.TryParse(req.ExpiryDate, out DateTime ed)
                        ? ed : new DateTime(DateTime.Today.Year, 12, 31);
                    DateTime openingDate = DateTime.TryParse(req.OpeningDate, out DateTime od)
                        ? od : DateTime.Today;

                    var ins = new SqlCommand(@"
INSERT INTO tblMemberInfo
    (member_id, shop_id, member_no, member_name, member_type_id,
     phone_mobile, phone_Res, email, cust_Address,
     allow_credit, credit_limit, card_duplicate_no,
     group_id, start_date, expiry_date,
     status, EnteredDate, EditedDate, enteredby)
VALUES
    (@mid, @sid, @mno, @nm, @mtid,
     @pm, @pr, @em, @addr,
     @ac, @cl, 0,
     @gid, @sd, @ed,
     'Activate', @now, @now, @uid)",
                        con, trans);

                    ins.Parameters.AddWithValue("@mid",  memberId);
                    ins.Parameters.AddWithValue("@sid",  shopId);
                    ins.Parameters.AddWithValue("@mno",  memberNo);
                    ins.Parameters.AddWithValue("@nm",   req.MemberName.Trim());
                    ins.Parameters.AddWithValue("@mtid", req.MemberTypeId);
                    ins.Parameters.AddWithValue("@pm",   (object)req.PhoneMobile ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@pr",   (object)req.PhoneRes    ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@em",   (object)req.Email       ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@addr", (object)req.Address     ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@ac",   req.AllowCredit ? 1 : 0);
                    ins.Parameters.AddWithValue("@cl",   req.CreditLimit);
                    ins.Parameters.AddWithValue("@gid",  req.GroupId.HasValue ? (object)req.GroupId.Value : DBNull.Value);
                    ins.Parameters.AddWithValue("@sd",   startDate.ToString("yyyy-MM-dd"));
                    ins.Parameters.AddWithValue("@ed",   expiryDate.ToString("yyyy-MM-dd"));
                    ins.Parameters.AddWithValue("@now",  now);
                    ins.Parameters.AddWithValue("@uid",  userId);
                    ins.ExecuteNonQuery();

                    // Opening balance — stored as a seed row in tblMemberClosing
                    // (same pattern as CustomerDAL.vb:1110). Only inserted when
                    // allow_credit is ON and the cashier entered a non-zero balance.
                    if (req.AllowCredit && req.OpeningBalance > 0)
                    {
                        // member_closing_id is NOT NULL, not an identity column, and part of
                        // tblMemberClosing's PK — must be generated the same MAX+1-per-shop way
                        // as member_id/member_no above (mirrors CustomerDAL.vb's GetMaxID call).
                        var closingIdCmd = new SqlCommand(
                            "SELECT ISNULL(MAX(member_closing_id),0)+1 FROM tblMemberClosing WHERE shop_id=@sid",
                            con, trans);
                        closingIdCmd.Parameters.AddWithValue("@sid", shopId);
                        int memberClosingId = Convert.ToInt32(closingIdCmd.ExecuteScalar());

                        string clsDate = openingDate.ToString("yyyy-MM-dd HH:mm:ss");
                        var clsCmd = new SqlCommand(
                            "INSERT INTO tblMemberClosing " +
                            "(member_closing_id, member_id, shop_id, closing_date, closing_balance, transcation_time) " +
                            "VALUES (@mcid, @mid, @sid, @cd, @bal, @cd)",
                            con, trans);
                        clsCmd.Parameters.AddWithValue("@mcid", memberClosingId);
                        clsCmd.Parameters.AddWithValue("@mid", memberId);
                        clsCmd.Parameters.AddWithValue("@sid", shopId);
                        clsCmd.Parameters.AddWithValue("@cd",  clsDate);
                        clsCmd.Parameters.AddWithValue("@bal", req.OpeningBalance);
                        clsCmd.ExecuteNonQuery();
                    }

                    trans.Commit();

                    return Request.CreateResponse(HttpStatusCode.OK,
                        new { success = true,
                              data = new { member_id = memberId, member_no = memberNo, shop_id = shopId } });
                }
                catch (Exception ex)
                {
                    trans.Rollback();
                    return ApiError.Internal(Request, ex, "CustomersController.CreateCustomer");
                }
            }
        }

        // GET api/customers/{id}/credit-outstanding
        // Returns live credit outstanding for a customer: total credit billed minus receipts received.
        // Called when a customer is selected at POS so the checkout screen always shows a fresh balance.
        // Mirrors SaleAndReturnDAL.GetCustomerCurrentOutStanding(ShopId, CustomerID) — Candela calls
        // this with the CUSTOMER'S OWN shop (SelectedCustomerShopID), not the viewing shop, and its
        // CalculateOutstanding sums tblSales.memberShopID / tblMemberReceipts.MemberShop_id — a tag for
        // which customer a transaction belongs to, stamped regardless of which physical shop it happened
        // at. So this must look the customer up and filter their transactions by their OWN shop_id, not
        // the caller's shop — a cross-shop customer must never be filtered out or 404 here; that's the
        // exact bug this endpoint used to have (m.shop_id = @sid rejected any other shop's customer).
        // The POS API has direct, real-time access to the one shared central DB (see GiftCardsController
        // .GetUnsold), so there is no separate "HO" to round-trip to for a fresher figure — this query
        // already is the live, authoritative source Candela's HOWebService.getCustomerCreditLimit exists
        // to provide desktop clients running against a local, possibly-stale replicated shop DB.
        [HttpGet, Route("{id:int}/credit-outstanding")]
        public HttpResponseMessage GetCreditOutstanding(int id)
        {

            try
            {
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();

                    var cmd = new SqlCommand(
                        "SELECT " +
                        "  isnull(m.credit_limit, 0) AS credit_limit, " +
                        "  isnull(m.allow_credit,  0) AS allow_credit, " +
                        "  isnull((SELECT SUM(s.NT_amount) FROM tblSales s " +
                        "           WHERE s.member_id = @mid AND s.isCreditSale = 1 AND s.memberShopID = m.shop_id), 0) " +
                        "- isnull((SELECT SUM(r.amount) FROM tblMemberReceipts r " +
                        "           WHERE r.member_id = @mid AND r.MemberShop_id = m.shop_id), 0) " +
                        "  AS credit_outstanding " +
                        "FROM tblMemberInfo m " +
                        "WHERE m.member_id = @mid", con);
                    cmd.Parameters.AddWithValue("@mid", id);

                    using (var rdr = cmd.ExecuteReader())
                    {
                        if (!rdr.Read())
                            return Request.CreateResponse(HttpStatusCode.NotFound,
                                new { error = $"Customer {id} not found." });

                        decimal limit       = Convert.ToDecimal(rdr["credit_limit"]);
                        decimal outstanding = Convert.ToDecimal(rdr["credit_outstanding"]);
                        decimal available   = Math.Max(0, limit - outstanding);

                        return Request.CreateResponse(HttpStatusCode.OK, new
                        {
                            member_id          = id,
                            credit_limit       = limit,
                            credit_outstanding = outstanding,
                            credit_available   = available,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return ApiError.Internal(Request, ex, "CustomersController.GetCreditOutstanding");
            }
        }
        // PUT api/customers/{id}/comments
        // Updates the customer's persistent note (tblMemberInfo.comments).
        // chkAllowCustomerComments — frmCustomerComent.vb / frmSaleAndReturn.vb:17372,39781.
        // Independent of any sale: saved directly against the customer profile so it carries
        // forward to future visits, same as the legacy comment popup's Update button.
        [HttpPut, Route("{id:int}/comments")]
        public HttpResponseMessage UpdateCustomerComments(int id, [FromBody] UpdateCustomerCommentsRequest req)
        {
            int shopId = (int)Request.Properties["shop_id"];

            if (req == null)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "Request body is required" });

            try
            {
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var cmd = new SqlCommand(
                        "UPDATE tblMemberInfo SET comments=@c, EditedDate=@now " +
                        "WHERE member_id=@mid AND shop_id=@sid", con);
                    cmd.Parameters.AddWithValue("@c",   (object)req.Comments ?? "");
                    cmd.Parameters.AddWithValue("@now", DateTime.Now);
                    cmd.Parameters.AddWithValue("@mid", id);
                    cmd.Parameters.AddWithValue("@sid", shopId);
                    int rows = cmd.ExecuteNonQuery();

                    if (rows == 0)
                        return Request.CreateResponse(HttpStatusCode.NotFound,
                            new { error = $"Customer {id} not found." });

                    return Request.CreateResponse(HttpStatusCode.OK, new { success = true });
                }
            }
            catch (Exception ex)
            {
                return ApiError.Internal(Request, ex, "CustomersController.UpdateCustomerComments");
            }
        }
    }

    public class UpdateCustomerCommentsRequest
    {
        public string Comments { get; set; }
    }

    public class CreateCustomerRequest
    {
        public string  MemberName      { get; set; }
        public int     MemberTypeId    { get; set; }
        public string  PhoneMobile     { get; set; }
        public string  PhoneRes        { get; set; }
        public string  Email           { get; set; }
        public string  Address         { get; set; }
        public bool    AllowCredit     { get; set; }
        public decimal CreditLimit     { get; set; }
        public int?    GroupId         { get; set; }
        public string  StartDate       { get; set; }
        public string  ExpiryDate      { get; set; }
        public decimal OpeningBalance  { get; set; }
        public string  OpeningDate     { get; set; }
    }
}
