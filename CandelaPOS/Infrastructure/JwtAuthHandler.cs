using System;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace CandelaPOS.Infrastructure
{
    public class JwtAuthHandler : DelegatingHandler
    {
        // Public paths that don't require a token
        private static readonly string[] PublicPaths =
        {
            "/api/auth/login"
        };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri.AbsolutePath.TrimEnd('/');
            foreach (var pub in PublicPaths)
                if (path.EndsWith(pub, StringComparison.OrdinalIgnoreCase))
                    return base.SendAsync(request, cancellationToken);

            var auth = request.Headers.Authorization;
            if (auth == null || auth.Scheme != "Bearer" || string.IsNullOrEmpty(auth.Parameter))
                return Unauthorized(request, "Missing or malformed Authorization header");

            var principal = JwtHelper.Validate(auth.Parameter);
            if (principal == null)
                return Unauthorized(request, "Invalid or expired token");

            // Reject tokens that have been explicitly logged out
            if (IsBlocklisted(auth.Parameter))
                return Unauthorized(request, "Token has been revoked");

            request.Properties["user_id"]   = JwtHelper.GetUserId(principal);
            request.Properties["shop_id"]   = JwtHelper.GetShopId(principal);
            request.Properties["pos_code"]  = JwtHelper.GetPosCode(principal);
            request.Properties["device_id"] = JwtHelper.GetDeviceId(principal);
            request.Properties["user_name"] = JwtHelper.GetUserName(principal);
            // Carry raw token so /auth/refresh and /auth/logout can blocklist it
            request.Properties["raw_token"] = auth.Parameter;

            Thread.CurrentPrincipal = principal;

            return base.SendAsync(request, cancellationToken);
        }

        // Checks tblPOSTokenBlocklist for the token signature.
        // Only tokens explicitly logged out appear there; expired entries are auto-excluded.
        private static bool IsBlocklisted(string rawToken)
        {
            try
            {
                string sig = JwtHelper.ExtractSignature(rawToken);
                if (string.IsNullOrEmpty(sig)) return false;

                var csb = new System.Data.SqlClient.SqlConnectionStringBuilder(CandelaBootstrap.ConnectionString)
                {
                    ConnectTimeout = 2   // fail fast — don't hold the request for 15 s
                };
                using (var con = new SqlConnection(csb.ConnectionString))
                {
                    con.Open();
                    var cmd = new SqlCommand(
                        "SELECT COUNT(1) FROM tblPOSTokenBlocklist " +
                        "WHERE token_sig = @sig AND expires_at > GETDATE()", con)
                    {
                        CommandTimeout = 2
                    };
                    cmd.Parameters.AddWithValue("@sig", sig);
                    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
            }
            catch (SqlException ex) when (ex.Number == 208)
            {
                // 208 = "Invalid object name" — blocklist table not yet created; treat as not blocked.
                return false;
            }
            catch (Exception ex)
            {
                // DB error (connection failure, timeout, pool exhaustion) — fail OPEN so a DB
                // blip does not log every user out. The blocklist is only for explicit logouts;
                // a revoked token at worst stays valid until its JWT expiry (~12 hr shift).
                System.Diagnostics.Trace.TraceError("IsBlocklisted DB error (fail-open): {0}", ex);
                return false;
            }
        }

        private static Task<HttpResponseMessage> Unauthorized(HttpRequestMessage request, string reason)
        {
            var response = request.CreateResponse(HttpStatusCode.Unauthorized,
                new { error = reason });
            return Task.FromResult(response);
        }
    }
}
