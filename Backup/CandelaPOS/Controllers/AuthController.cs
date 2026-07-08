using System;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using CandelaPOS.Infrastructure;
using CandelaPOS.Models;
using DAL;

namespace CandelaPOS.Controllers
{
    [RoutePrefix("api/auth")]
    public class AuthController : ApiController
    {
        // POST api/auth/login
        [HttpPost, Route("login")]
        public HttpResponseMessage Login([FromBody] LoginRequest req)
        {
            if (req == null || string.IsNullOrEmpty(req.Username) || string.IsNullOrEmpty(req.Password))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "username and password are required" });

            if (string.IsNullOrEmpty(req.DeviceId))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "device_id is required" });

            CandelaBootstrap.PrepareRequest();

            try
            {
                // Step 1 — credential check (shop_id comes from server, never from tablet)
                var userDal = new SecurityUserDAL();
                var userRow = userDal.FindValidUser(req.Username, req.Password);
                if (userRow == null)
                    return Request.CreateResponse(HttpStatusCode.Unauthorized,
                        new { error = "Invalid username or password" });

                int userId   = userRow.UserID;
                int shopId   = userRow.ShopInfo.ShopID;
                string userName  = userRow.UserName ?? req.Username;
                string shopName  = userRow.ShopInfo.ShopName ?? "";

                // Step 2 — device check
                string conStr = CandelaBootstrap.ConnectionString;
                using (var con = new SqlConnection(conStr))
                {
                    con.Open();

                    // Is device already registered?
                    var checkCmd = new SqlCommand(
                        "SELECT is_blocked FROM tblPOSRegisteredDevices WHERE device_id = @did", con);
                    checkCmd.Parameters.AddWithValue("@did", req.DeviceId);
                    var blocked = checkCmd.ExecuteScalar();

                    if (blocked != null)
                    {
                        if (Convert.ToBoolean(blocked))
                            return Request.CreateResponse((HttpStatusCode)403,
                                new { error = "This device has been blocked by administrator" });
                        // device known and not blocked — fall through to issue JWT
                    }
                    else
                    {
                        // New device — quota check
                        var quotaCmd = new SqlCommand(
                            "SELECT config_value FROM tblShopConfiguration " +
                            "WHERE shop_id = @sid AND config_name = 'AllowTab_AtShop'", con);
                        quotaCmd.Parameters.AddWithValue("@sid", shopId);
                        var quotaVal = quotaCmd.ExecuteScalar();
                        int quota = quotaVal != null ? Convert.ToInt32(quotaVal) : 0;

                        var countCmd = new SqlCommand(
                            "SELECT COUNT(*) FROM tblPOSRegisteredDevices WHERE shop_id = @sid", con);
                        countCmd.Parameters.AddWithValue("@sid", shopId);
                        int registered = Convert.ToInt32(countCmd.ExecuteScalar());

                        if (registered >= quota)
                            return Request.CreateResponse((HttpStatusCode)429,
                                new { error = $"Shop tablet quota reached ({registered} of {quota} used)" });

                        // Register the new device
                        var insertCmd = new SqlCommand(
                            "INSERT INTO tblPOSRegisteredDevices " +
                            "(device_id, pos_name, device_model, shop_id, registered_at, is_blocked) " +
                            "VALUES (@did, @fn, @dm, @sid, GETDATE(), 0)", con);
                        insertCmd.Parameters.AddWithValue("@did", req.DeviceId);
                        insertCmd.Parameters.AddWithValue("@fn",  req.FriendlyName ?? "");
                        insertCmd.Parameters.AddWithValue("@dm",  req.DeviceModel  ?? "");
                        insertCmd.Parameters.AddWithValue("@sid", shopId);
                        insertCmd.ExecuteNonQuery();
                    }
                }

                // Step 3 — issue JWT
                string token = JwtHelper.Generate(userId, userName, shopId, req.DeviceId);

                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<LoginResponse>.Ok(new LoginResponse
                    {
                        Token    = token,
                        UserId   = userId,
                        UserName = userName,
                        ShopId   = shopId,
                        ShopName = shopName
                    }));
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }
        }
    }
}
