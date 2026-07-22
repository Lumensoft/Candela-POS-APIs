using System;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using CandelaPOS.Infrastructure;

namespace CandelaPOS.Controllers
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

            CandelaBootstrap.PrepareRequest();

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
                        string clsDate = openingDate.ToString("yyyy-MM-dd HH:mm:ss");
                        var clsCmd = new SqlCommand(
                            "INSERT INTO tblMemberClosing " +
                            "(member_id, shop_id, closing_date, closing_balance, transcation_time) " +
                            "VALUES (@mid, @sid, @cd, @bal, @cd)",
                            con, trans);
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
                catch (Exception)
                {
                    trans.Rollback();
                    return Request.CreateResponse(HttpStatusCode.InternalServerError,
                        new { error = "An internal error occurred." });
                }
            }
        }

        // GET api/customers/{id}/credit-outstanding
        // Returns live credit outstanding for a customer: total credit billed minus receipts received.
        // Called when a customer is selected at POS so the checkout screen always shows a fresh balance.
        [HttpGet, Route("{id:int}/credit-outstanding")]
        public HttpResponseMessage GetCreditOutstanding(int id)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];

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
                        "           WHERE s.member_id = @mid AND s.isCreditSale = 1 AND s.shop_id = @sid), 0) " +
                        "- isnull((SELECT SUM(r.amount) FROM tblMemberReceipts r " +
                        "           WHERE r.member_id = @mid AND r.shop_id = @sid), 0) " +
                        "  AS credit_outstanding " +
                        "FROM tblMemberInfo m " +
                        "WHERE m.member_id = @mid AND m.shop_id = @sid", con);
                    cmd.Parameters.AddWithValue("@mid", id);
                    cmd.Parameters.AddWithValue("@sid", shopId);

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
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }
        }
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
