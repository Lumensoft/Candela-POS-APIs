using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace CandelaPOS.Infrastructure
{
    /// <summary>
    /// Validates Bearer tokens on every request.
    /// Passes through unauthenticated to /api/auth/login only.
    /// </summary>
    public class JwtAuthHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Login endpoint is public
            if (request.RequestUri.AbsolutePath.TrimEnd('/').EndsWith("/api/auth/login",
                    System.StringComparison.OrdinalIgnoreCase))
                return base.SendAsync(request, cancellationToken);

            var auth = request.Headers.Authorization;
            if (auth == null || auth.Scheme != "Bearer" || string.IsNullOrEmpty(auth.Parameter))
                return Unauthorized(request);

            var principal = JwtHelper.Validate(auth.Parameter);
            if (principal == null)
                return Unauthorized(request);

            // Attach identity so controllers can read claims
            var identity = principal.Identity as ClaimsIdentity;
            request.Properties["user_id"]   = JwtHelper.GetUserId(principal);
            request.Properties["shop_id"]   = JwtHelper.GetShopId(principal);
            request.Properties["device_id"] = JwtHelper.GetDeviceId(principal);
            request.Properties["user_name"] = JwtHelper.GetUserName(principal);

            Thread.CurrentPrincipal = principal;

            return base.SendAsync(request, cancellationToken);
        }

        private static Task<HttpResponseMessage> Unauthorized(HttpRequestMessage request)
        {
            var response = request.CreateResponse(HttpStatusCode.Unauthorized,
                new { error = "Invalid or missing token" });
            return Task.FromResult(response);
        }
    }
}
