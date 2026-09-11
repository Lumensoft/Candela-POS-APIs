using Candela.Shared.Auth;
using Candela.Platform.Data;
using Candela.Shared.Logging;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;

namespace Candela.Platform.Auth;

/// <summary>
/// Bearer authentication, ported from the .NET Framework host so both processes accept
/// exactly the same tokens during the migration.
///
/// Everything except the public paths needs a valid token. A rejection is written as
/// { "error": "..." } with 401, which is the shape the tablet already handles — the
/// axios interceptor clears the session and redirects to /login on any 401.
/// </summary>
public sealed class JwtAuthMiddleware
{
    private static readonly string[] PublicPaths =
    {
        "/api/auth/login",
        "/api/auth/web-login"
    };

    private readonly RequestDelegate _next;

    public JwtAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext context, IDbConnectionFactory db)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";

        // Only API traffic is challenged. In the .NET Framework host this was a Web API
        // message handler, so it never saw anything but /api/*. Here it is ordinary
        // middleware, and without this guard it answers 401 for every path in the
        // application — including ones that simply are not there, which have to read as
        // 404, and any static content the site may serve alongside the API.
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals("/api", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (PublicPaths.Any(p => path.EndsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        // CORS preflight must never be challenged, or the browser sees the 401 instead
        // of the preflight result and reports a CORS failure.
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await Unauthorized(context, "Missing or malformed Authorization header");
            return;
        }

        var rawToken = header["Bearer ".Length..].Trim();
        var principal = JwtHelper.Validate(rawToken);
        if (principal is null)
        {
            await Unauthorized(context, "Invalid or expired token");
            return;
        }

        if (await IsBlocklistedAsync(db, rawToken, context.RequestAborted))
        {
            await Unauthorized(context, "Token has been revoked");
            return;
        }

        context.User = principal;
        context.Items["raw_token"] = rawToken;

        await _next(context);
    }

    /// <summary>
    /// Only tokens that were explicitly logged out appear in tblPOSTokenBlocklist.
    ///
    /// Fails OPEN on a database error, matching the .NET Framework host: a database
    /// blip must not log every cashier out mid-shift, and the worst case is that an
    /// already-revoked token stays usable until its own expiry.
    /// </summary>
    private static async Task<bool> IsBlocklistedAsync(IDbConnectionFactory db, string rawToken, CancellationToken ct)
    {
        try
        {
            var sig = JwtHelper.ExtractSignature(rawToken);
            if (string.IsNullOrEmpty(sig)) return false;

            await using var con = await db.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT COUNT(1) FROM tblPOSTokenBlocklist WHERE token_sig = @sig AND expires_at > GETDATE()", con)
            {
                CommandTimeout = 2   // fail fast rather than hold the request
            };
            cmd.Parameters.AddWithValue("@sig", sig);

            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result) > 0;
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            // 208 = invalid object name: the table has not been created yet.
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Blocklist check failed, allowing the token through: {0}", ex.Message);
            return false;
        }
    }

    private static Task Unauthorized(HttpContext context, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonConvert.SerializeObject(new { error = reason }));
    }
}
