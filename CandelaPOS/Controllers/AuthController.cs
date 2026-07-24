using System;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using CandelaPOS.Infrastructure;
using CandelaPOS.Models;
using static Utility.Utility;
using System.Security.Claims;

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

            try
            {
                string conStr = CandelaBootstrap.ConnectionString;

                int    userId              = 0;
                string userName            = "";
                int    shopId              = 0;
                string posCode             = "";
                string shopName            = "";
                string computerName        = "";
                string invoicePrinterName  = "";
                bool   allowDiscEditing    = false;
                bool   allowPriceEditing   = false;
                bool   canAdjust           = false;
                bool   isOpenAdjust        = false;

                using (var con = new SqlConnection(conStr))
                {
                    con.Open();

                    // Step 1 — validate credentials against tblSecurityUser
                    const string credSql =
                        "SELECT b.user_id, b.User_log_password, b.User_name," +
                        " isnull(b.AllowPOSDiscountEditing,0) AS AllowPOSDiscountEditing," +
                        " isnull(b.AllowPOSPriceEditing,0)    AS AllowPOSPriceEditing," +
                        " isnull(b.ApplyAdjustment,0)         AS ApplyAdjustment," +
                        " isnull(b.ApplyOpenAdjustment,0)     AS ApplyOpenAdjustment" +
                        " FROM tblSecurityGroup a" +
                        " INNER JOIN TblSecurityUser b ON a.GROUP_ID = b.GROUP_ID" +
                        " WHERE b.user_log_id = @uid" +
                        "   AND isnull(b.end_date, GETDATE()+1) >= DATEADD(dd,0,DATEDIFF(dd,0,GETDATE()))";

                    var credCmd = new SqlCommand(credSql, con);
                    credCmd.Parameters.AddWithValue("@uid", req.Username);

                    using (var reader = credCmd.ExecuteReader())
                    {
                        if (!reader.HasRows)
                            return Request.CreateResponse(HttpStatusCode.Unauthorized,
                                new { error = "Invalid username or password" });

                        reader.Read();

                        string storedEncrypted = reader["User_log_password"].ToString();
                        if (string.IsNullOrEmpty(storedEncrypted))
                            return Request.CreateResponse(HttpStatusCode.Unauthorized,
                                new { error = "Invalid username or password" });

                        string decrypted = SymmetricEncryption.Decrypt(storedEncrypted, "f");
                        if (!req.Password.Equals(decrypted, StringComparison.Ordinal))
                            return Request.CreateResponse(HttpStatusCode.Unauthorized,
                                new { error = "Invalid username or password" });

                        userId            = Convert.ToInt32(reader["user_id"]);
                        userName          = reader["User_name"].ToString();
                        allowDiscEditing  = Convert.ToBoolean(reader["AllowPOSDiscountEditing"]);
                        allowPriceEditing = Convert.ToBoolean(reader["AllowPOSPriceEditing"]);
                        canAdjust         = Convert.ToBoolean(reader["ApplyAdjustment"]) ||
                                            Convert.ToBoolean(reader["ApplyOpenAdjustment"]);
                        isOpenAdjust      = Convert.ToBoolean(reader["ApplyOpenAdjustment"]);
                    }

                    // Step 2a — check if this device is already registered in tblComputerList
                    const string findSql =
                        "SELECT computer_id, computer_name, shop_id, POS_code, isTabActive," +
                        " isnull(InvoicePrinterName,'') AS InvoicePrinterName" +
                        " FROM tblComputerList" +
                        " WHERE deviceid = @uuid AND istablet = 1";

                    var findCmd = new SqlCommand(findSql, con);
                    findCmd.Parameters.AddWithValue("@uuid", req.DeviceId);

                    using (var dr = findCmd.ExecuteReader())
                    {
                        if (dr.Read())
                        {
                            bool isActive = Convert.ToBoolean(dr["isTabActive"]);
                            if (!isActive)
                                return Request.CreateResponse((HttpStatusCode)403,
                                    new { error = "This tablet has been deactivated by administrator" });

                            shopId               = Convert.ToInt32(dr["shop_id"]);
                            posCode              = dr["POS_code"].ToString();
                            computerName         = dr["computer_name"]?.ToString() ?? "";
                            invoicePrinterName   = dr["InvoicePrinterName"].ToString();
                        }
                    }

                    // Step 2b — new device: claim a free pre-allocated slot atomically
                    if (shopId == 0)
                    {
                        const string claimSql =
                            "UPDATE TOP(1) tblComputerList" +
                            " SET deviceid = @uuid, isTabActive = 1" +
                            " OUTPUT inserted.shop_id, inserted.POS_code, inserted.computer_name," +
                            "        isnull(inserted.InvoicePrinterName,'') AS InvoicePrinterName" +
                            " WHERE istablet = 1 AND isTabActive = 0 AND deviceid IS NULL";

                        var claimCmd = new SqlCommand(claimSql, con);
                        claimCmd.Parameters.AddWithValue("@uuid", req.DeviceId);

                        using (var dr = claimCmd.ExecuteReader())
                        {
                            if (!dr.Read())
                                return Request.CreateResponse((HttpStatusCode)403,
                                    new { error = "No tablet slots available. Please contact HO to add a tablet." });

                            shopId              = Convert.ToInt32(dr["shop_id"]);
                            posCode             = dr["POS_code"].ToString();
                            computerName        = dr["computer_name"]?.ToString() ?? "";
                            invoicePrinterName  = dr["InvoicePrinterName"].ToString();
                        }
                    }

                    // Step 3 — get shop name
                    var shopCmd = new SqlCommand(
                        "SELECT shop_name FROM tblDefShops WHERE shop_id = @sid", con);
                    shopCmd.Parameters.AddWithValue("@sid", shopId);
                    var shopNameVal = shopCmd.ExecuteScalar();
                    shopName = shopNameVal?.ToString() ?? "";
                }

                // Step 4 — bootstrap Candela globals then issue JWT
                CandelaBootstrap.PrepareRequest();
                string token = JwtHelper.Generate(userId, userName, shopId, posCode, req.DeviceId);

                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<LoginResponse>.Ok(new LoginResponse
                    {
                        Token                = token,
                        UserId               = userId,
                        UserName             = userName,
                        ShopId               = shopId,
                        ShopName             = shopName,
                        PosCode              = posCode,
                        ComputerName         = computerName,
                        InvoicePrinterName   = invoicePrinterName,
                        AllowDiscountEditing = allowDiscEditing,
                        AllowPriceEditing    = allowPriceEditing,
                        CanAdjust            = canAdjust,
                        IsOpenAdjust         = isOpenAdjust,
                    }));
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // POST api/auth/refresh
        // Issues a new JWT with a fresh expiry window from the same claims.
        // The old token is blocklisted so it can no longer be used.
        // Call this before the current token expires (e.g., 5 minutes before expiry).
        [HttpPost, Route("refresh")]
        public HttpResponseMessage Refresh()
        {
            // JwtAuthHandler already validated the token and placed claims in Properties
            string rawToken = Request.Properties.ContainsKey("raw_token")
                ? Request.Properties["raw_token"] as string : null;

            if (string.IsNullOrEmpty(rawToken))
                return Request.CreateResponse(HttpStatusCode.Unauthorized,
                    new { error = "No token found on request" });

            int    userId   = (int)   Request.Properties["user_id"];
            int    shopId   = (int)   Request.Properties["shop_id"];
            string posCode  = (string)Request.Properties["pos_code"];
            string deviceId = (string)Request.Properties["device_id"];
            string userName = (string)Request.Properties["user_name"];

            try
            {
                // Blocklist the old token so it can't be reused after this refresh
                BlocklistToken(rawToken);

                string newToken = JwtHelper.Generate(userId, userName, shopId, posCode, deviceId);

                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<object>.Ok(new { token = newToken }));
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // POST api/auth/logout
        // Invalidates the current token immediately by blocklisting its signature.
        // The tablet seat remains registered in tblComputerList (device stays authorised);
        // the next login will issue a fresh token for the same slot.
        [HttpPost, Route("logout")]
        public HttpResponseMessage Logout()
        {
            string rawToken = Request.Properties.ContainsKey("raw_token")
                ? Request.Properties["raw_token"] as string : null;

            if (string.IsNullOrEmpty(rawToken))
                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<object>.Ok(new { logged_out = true }));

            try
            {
                BlocklistToken(rawToken);

                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<object>.Ok(new { logged_out = true }));
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // POST api/auth/supervisor
        // Validates a supervisor's credentials without changing the cashier's session.
        // The returned name/id are held in React state for the duration of the elevated
        // session; the cashier's JWT is unchanged throughout.
        [HttpPost, Route("supervisor")]
        public HttpResponseMessage SupervisorLogin([FromBody] SupervisorRequest req)
        {
            if (req == null || string.IsNullOrEmpty(req.Username) || string.IsNullOrEmpty(req.Password))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "username and password are required" });

            try
            {
                string conStr = CandelaBootstrap.ConnectionString;

                const string sql =
                    "SELECT b.user_id, b.User_log_password, b.User_name" +
                    " FROM tblSecurityGroup a" +
                    " INNER JOIN TblSecurityUser b ON a.GROUP_ID = b.GROUP_ID" +
                    " WHERE b.user_log_id = @uid" +
                    "   AND isnull(b.end_date, GETDATE()+1) >= DATEADD(dd,0,DATEDIFF(dd,0,GETDATE()))";

                using (var con = new SqlConnection(conStr))
                {
                    con.Open();
                    var cmd = new SqlCommand(sql, con);
                    cmd.Parameters.AddWithValue("@uid", req.Username);

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.HasRows)
                            return Request.CreateResponse(HttpStatusCode.Unauthorized,
                                new { error = "Invalid supervisor credentials" });

                        reader.Read();

                        string stored = reader["User_log_password"].ToString();
                        if (string.IsNullOrEmpty(stored))
                            return Request.CreateResponse(HttpStatusCode.Unauthorized,
                                new { error = "Invalid supervisor credentials" });

                        string decrypted = SymmetricEncryption.Decrypt(stored, "f");
                        if (!req.Password.Equals(decrypted, StringComparison.Ordinal))
                            return Request.CreateResponse(HttpStatusCode.Unauthorized,
                                new { error = "Invalid supervisor credentials" });

                        int    supervisorId   = Convert.ToInt32(reader["user_id"]);
                        string supervisorName = reader["User_name"].ToString();

                        return Request.CreateResponse(HttpStatusCode.OK,
                            ApiResponse<object>.Ok(new
                            {
                                supervisor_id   = supervisorId,
                                supervisor_name = supervisorName,
                            }));
                    }
                }
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // GET api/auth/adjustment-rights
        // Returns whether the current user may apply a manual invoice adjustment.
        // Config gate (AdjustmentLimit > 0) is checked by the frontend from IndexedDB
        // before calling this endpoint — this endpoint is purely about per-user rights.
        [HttpGet, Route("adjustment-rights")]
        public HttpResponseMessage AdjustmentRights()
        {
            try
            {
                int userId = (int)Request.Properties["user_id"];

                // ApplyAdjustment / ApplyOpenAdjustment are bit columns on TblSecurityUser
                // (per-user flags, not group control rights) — frmSaleAndReturn.vb:3126
                bool hasOpenAdj = false, hasAdj = false;
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var adjRightCmd = new SqlCommand(
                        "SELECT isnull(ApplyAdjustment, 0)    AS ApplyAdjustment," +
                        "       isnull(ApplyOpenAdjustment, 0) AS ApplyOpenAdjustment" +
                        " FROM TblSecurityUser WHERE user_id = @uid", con);
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

                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<object>.Ok(new
                    {
                        can_adjust = hasOpenAdj || hasAdj,
                        is_open    = hasOpenAdj,
                    }));
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // Adds the token signature to tblPOSTokenBlocklist.
        // expires_at matches the token's own exp claim so the row is naturally stale
        // after the token would have expired anyway (allows periodic cleanup).
        private static void BlocklistToken(string rawToken)
        {
            string   sig     = JwtHelper.ExtractSignature(rawToken);
            DateTime expires = JwtHelper.ExtractExpiry(rawToken);

            if (string.IsNullOrEmpty(sig)) return;
            if (expires == DateTime.MinValue) expires = DateTime.UtcNow.AddHours(24);

            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                var ins = new SqlCommand(
                    "INSERT INTO tblPOSTokenBlocklist (token_sig, expires_at) " +
                    "VALUES (@sig, @exp)", con);
                ins.Parameters.AddWithValue("@sig", sig);
                ins.Parameters.AddWithValue("@exp", expires);
                ins.ExecuteNonQuery();
            }
        }
    }
}
