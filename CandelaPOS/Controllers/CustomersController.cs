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

                    var ins = new SqlCommand(@"
INSERT INTO tblMemberInfo
    (member_id, shop_id, member_no, member_name, member_type_id,
     phone_mobile, phone_Res, email, cust_Address,
     allow_credit, credit_limit, card_duplicate_no,
     status, EnteredDate, EditedDate, enteredby)
VALUES
    (@mid, @sid, @mno, @nm, @mtid,
     @pm, @pr, @em, @addr,
     @ac, @cl, 0,
     'Activate', @now, @now, @uid)",
                        con, trans);

                    ins.Parameters.AddWithValue("@mid",  memberId);
                    ins.Parameters.AddWithValue("@sid",  shopId);
                    ins.Parameters.AddWithValue("@mno",  memberNo);
                    ins.Parameters.AddWithValue("@nm",   req.MemberName.Trim());
                    ins.Parameters.AddWithValue("@mtid", req.MemberTypeId);
                    ins.Parameters.AddWithValue("@pm",   (object)req.PhoneMobile  ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@pr",   (object)req.PhoneRes     ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@em",   (object)req.Email        ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@addr", (object)req.Address      ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@ac",   req.AllowCredit ? 1 : 0);
                    ins.Parameters.AddWithValue("@cl",   req.CreditLimit);
                    ins.Parameters.AddWithValue("@now",  now);
                    ins.Parameters.AddWithValue("@uid",  userId);
                    ins.ExecuteNonQuery();

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
    }

    public class CreateCustomerRequest
    {
        public string  MemberName   { get; set; }
        public int     MemberTypeId { get; set; }
        public string  PhoneMobile  { get; set; }
        public string  PhoneRes     { get; set; }
        public string  Email        { get; set; }
        public string  Address      { get; set; }
        public bool    AllowCredit  { get; set; }
        public decimal CreditLimit  { get; set; }
    }
}
