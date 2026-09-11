using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using static Utility.Utility;
using System.Security.Claims;
using CandelaPOS.Shared.Data;
using CandelaPOS.Shared.Auth;
using CandelaPOS.Shared.Api;
using CandelaPOS.Shared.Errors;
using CandelaPOS.Shared.Logging;

namespace CandelaPOS.Features.Auth
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
                string  groupName           = "";
                int     groupType          = 0;
                decimal saleReturnLimit    = 0m;
                int     groupId            = 0;
                bool    hasBelowCostRight  = false;
                var     grantedRights      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var con = new SqlConnection(conStr))
                {
                    con.Open();

                    // Step 1 — validate credentials against tblSecurityUser
                    const string credSql =
                        "SELECT b.user_id, b.User_log_password, b.User_name," +
                        " isnull(b.AllowPOSDiscountEditing,0) AS AllowPOSDiscountEditing," +
                        " isnull(b.AllowPOSPriceEditing,0)    AS AllowPOSPriceEditing," +
                        " isnull(b.ApplyAdjustment,0)         AS ApplyAdjustment," +
                        " isnull(b.ApplyOpenAdjustment,0)     AS ApplyOpenAdjustment," +
                        " isnull(a.GROUP_NAME,'')              AS GROUP_NAME," +
                        " isnull(a.GROUP_TYPE,0)               AS GROUP_TYPE," +
                        " isnull(a.SaleReturnLimit,0)          AS SaleReturnLimit," +
                        " a.GROUP_ID                           AS GROUP_ID" +
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
                        groupName         = reader["GROUP_NAME"].ToString();
                        groupType         = Convert.ToInt32(reader["GROUP_TYPE"]);
                        saleReturnLimit   = Convert.ToDecimal(reader["SaleReturnLimit"]);
                        groupId           = Convert.ToInt32(reader["GROUP_ID"]);
                    }

                    // Step 1b — fetch all frmSaleAndReturn control rights for this group in one query
                    var rightsCmd = new SqlCommand(
                        "SELECT sfc.controlName " +
                        "FROM tblSecurityControlRight scr " +
                        "INNER JOIN tblSecurityFormControl sfc ON sfc.ControlId = scr.ControlId " +
                        "INNER JOIN tblSecurityForm sf ON sf.FORM_ID = sfc.FormID " +
                        "WHERE scr.GroupId = @gid AND sf.Form_Name_New = 'frmSaleAndReturn'", con);
                    rightsCmd.Parameters.AddWithValue("@gid", groupId);
                    using (var rdr = rightsCmd.ExecuteReader())
                    {
                        while (rdr.Read())
                            grantedRights.Add(rdr["controlName"].ToString());
                    }
                    hasBelowCostRight = grantedRights.Contains("BelowCostSales");

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

                    // Step 3b — may this user's group work at the shop this device belongs to?
                    //
                    // The shop comes from the device (tblComputerList), and until now nothing
                    // checked that the person signing in belongs there: a user from one shop
                    // could sign in on another shop's tablet and transact against its stock
                    // and its cash drawer. Candela's own model for this is
                    // tblSecurityGroupShops plus tblSecurityGroup.IsSelectedShop, which is
                    // what AuthRules consults.
                    var access = AuthRules.CheckShopAccess(con, groupId, shopId);
                    if (!access.Allowed)
                    {
                        AppLog.Warn("Login refused: user {0} (group {1}) at shop {2} - {3}",
                            userId, groupId, shopId, access.Reason);
                        return Request.CreateResponse((HttpStatusCode)403, new { error = access.Reason });
                    }
                    if (access.UnconfiguredGroup)
                    {
                        AppLog.Warn(
                            "Group {0} has no rows in tblSecurityGroupShops and IsSelectedShop=0, so user {1} " +
                            "was allowed at shop {2} on the assumption the mapping was never configured. " +
                            "Fill in Group Shop Rights, then set Security:StrictShopRights=true to enforce.",
                            groupId, userId, shopId);
                    }
                }

                // Step 4 — bootstrap Candela globals then issue JWT
                string controlRightsStr = string.Join(",", grantedRights);
                string token = JwtHelper.Generate(userId, userName, shopId, posCode, req.DeviceId, groupName, groupType, saleReturnLimit, hasBelowCostRight, controlRightsStr);

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
                        ControlRights        = new List<string>(grantedRights),
                    }));
            }
            catch (Exception ex)
            {
                return ApiError.Internal(Request, ex, "AuthController.Login");
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

            int    userId    = (int)   Request.Properties["user_id"];
            int    shopId    = (int)   Request.Properties["shop_id"];
            string posCode   = (string)Request.Properties["pos_code"];
            string deviceId  = (string)Request.Properties["device_id"];
            string userName  = (string)Request.Properties["user_name"];
            string  groupName       = (string) Request.Properties["group_name"];
            int     groupType       = (int)    Request.Properties["group_type"];
            decimal saleReturnLimit = (decimal) Request.Properties["sale_return_limit"];

            // Rights have to be carried across the refresh. Generate() defaults these
            // last two parameters to false/"" and this call used to omit them, so every
            // refresh silently stripped the cashier of BelowCostSales and every other
            // frmSaleAndReturn control right for the rest of the shift. JwtAuthHandler
            // has already put both on Request.Properties from the token being refreshed.
            bool belowCostRight = Request.Properties.ContainsKey("below_cost_right")
                                  && (bool)Request.Properties["below_cost_right"];
            var rightsSet = Request.Properties.ContainsKey("scr_rights")
                ? Request.Properties["scr_rights"] as HashSet<string>
                : null;
            string controlRightsStr = rightsSet != null ? string.Join(",", rightsSet) : "";

            try
            {
                // Blocklist the old token so it can't be reused after this refresh
                BlocklistToken(rawToken);

                string newToken = JwtHelper.Generate(userId, userName, shopId, posCode, deviceId,
                    groupName, groupType, saleReturnLimit, belowCostRight, controlRightsStr);

                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<object>.Ok(new { token = newToken }));
            }
            catch (Exception ex)
            {
                return ApiError.Internal(Request, ex, "AuthController.Refresh");
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
            catch (Exception ex)
            {
                return ApiError.Internal(Request, ex, "AuthController.Logout");
            }
        }

        // POST api/auth/supervisor
        // Verifies a second user so the cashier may exceed a discount or price limit.
        // The cashier JWT is never changed - the returned name/id are held in React
        // state for the duration of the elevated session.
        //
        // Mirrors the desktop Override Login (frmOverrideLogin.vb:92-101), which applies
        // three rules the API previously applied none of. Without rule 1 a cashier could
        // type their own credentials here and authorise themselves, which made the whole
        // supervisor control decorative.
        [HttpPost, Route("supervisor")]
        public HttpResponseMessage SupervisorLogin([FromBody] SupervisorRequest req)
        {
            if (req == null || string.IsNullOrEmpty(req.Username) || string.IsNullOrEmpty(req.Password))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "username and password are required" });

            try
            {
                // Who is asking. JwtAuthHandler has already validated the caller token.
                int cashierUserId    = (int)Request.Properties["user_id"];
                int cashierShopId    = (int)Request.Properties["shop_id"];
                int cashierGroupType = (int)Request.Properties["group_type"];

                const string sql =
                    "SELECT b.user_id, b.User_log_password, b.User_name," +
                    " isnull(a.GROUP_TYPE,0) AS GROUP_TYPE," +
                    " a.GROUP_ID             AS GROUP_ID" +
                    " FROM tblSecurityGroup a" +
                    " INNER JOIN TblSecurityUser b ON a.GROUP_ID = b.GROUP_ID" +
                    " WHERE b.user_log_id = @uid" +
                    "   AND isnull(b.end_date, GETDATE()+1) >= DATEADD(dd,0,DATEDIFF(dd,0,GETDATE()))";

                int    supervisorId;
                string supervisorName;
                int    supervisorGroupType;
                int    supervisorGroupId;

                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();

                    // Read and close the reader before any further query on this connection.
                    using (var cmd = new SqlCommand(sql, con))
                    {
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

                            supervisorId        = Convert.ToInt32(reader["user_id"]);
                            supervisorName      = reader["User_name"].ToString();
                            supervisorGroupType = Convert.ToInt32(reader["GROUP_TYPE"]);
                            supervisorGroupId   = Convert.ToInt32(reader["GROUP_ID"]);
                        }
                    }

                    // Rule 1 - nobody authorises themselves.
                    // frmOverrideLogin.vb:92
                    //   If txtLoginID.Text.Trim.ToUpper = gObjUserInfo.LoginID.ToUpper Then Return False
                    // Compared by user_id rather than by login string: same identity, and
                    // immune to case or whitespace differences.
                    if (supervisorId == cashierUserId)
                    {
                        AppLog.Warn("Supervisor override refused: user {0} tried to authorise themselves at shop {1}",
                            cashierUserId, cashierShopId);
                        return Request.CreateResponse((HttpStatusCode)403,
                            new { error = "You cannot authorise your own override. Ask another user to sign in." });
                    }

                    // Rules 2 and 3 - same group type, and entitled to this shop.
                    // frmOverrideLogin.vb:94-101 skips both on Candela Basic (Version = 1).
                    if (!IsBasicEdition())
                    {
                        if (supervisorGroupType != cashierGroupType)
                        {
                            AppLog.Warn("Supervisor override refused: supervisor {0} group type {1} does not match " +
                                        "cashier {2} group type {3} at shop {4}",
                                supervisorId, supervisorGroupType, cashierUserId, cashierGroupType, cashierShopId);
                            return Request.CreateResponse((HttpStatusCode)403,
                                new { error = "Could not authorise because the user belongs to a different Group/Shop." });
                        }

                        var supAccess = AuthRules.CheckShopAccess(con, supervisorGroupId, cashierShopId);
                        if (!supAccess.Allowed)
                        {
                            AppLog.Warn("Supervisor override refused: supervisor {0} (group {1}) not entitled to shop {2} - {3}",
                                supervisorId, supervisorGroupId, cashierShopId, supAccess.Reason);
                            return Request.CreateResponse((HttpStatusCode)403,
                                new { error = "Could not authorise because the user belongs to a different Group/Shop." });
                        }
                    }
                }

                AppLog.Info("Supervisor override granted: supervisor {0} for user {1} at shop {2}",
                    supervisorId, cashierUserId, cashierShopId);

                return Request.CreateResponse(HttpStatusCode.OK,
                    ApiResponse<object>.Ok(new
                    {
                        supervisor_id   = supervisorId,
                        supervisor_name = supervisorName,
                    }));
            }
            catch (Exception ex)
            {
                return ApiError.Internal(Request, ex, "AuthController.SupervisorLogin");
            }
        }

        // Candela edition flag, stored encrypted in tblRCMSConfiguration: 1 means Basic.
        // frmOverrideLogin.vb:94 relaxes the group/shop pairing on Basic, so we match it.
        // If the flag cannot be read we return false, which keeps the stricter path.
        private static bool IsBasicEdition()
        {
            try
            {
                var cfg = CandelaBootstrap.GetRCMSConfig();
                string raw;
                if (!cfg.TryGetValue("Version", out raw) || string.IsNullOrWhiteSpace(raw))
                    return false;
                return SymmetricEncryption.Decrypt(raw, "f") == "1";
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not read the Candela Version flag, applying strict override rules: {0}", ex.Message);
                return false;
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
            catch (Exception ex)
            {
                return ApiError.Internal(Request, ex, "AuthController.AdjustmentRights");
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
