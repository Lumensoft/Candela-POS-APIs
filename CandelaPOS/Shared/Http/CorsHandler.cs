using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CandelaPOS.Shared.Http
{
    /// <summary>
    /// Reflects the Origin header back only for origins we recognise.
    ///
    /// The allow-list comes from the Cors:AllowedOrigins appSetting (comma separated)
    /// so a shop can be deployed without a rebuild. It used to be compiled in as three
    /// localhost dev ports, which meant the tablet could never talk to a real shop
    /// server — and the usual "fix" for that is a wildcard, which with
    /// Access-Control-Allow-Credentials: true would let any site on the network make
    /// authenticated calls on a cashier's behalf.
    ///
    /// Those same dev origins remain the fallback, so nothing changes until the
    /// setting is present.
    /// </summary>
    public class CorsHandler : DelegatingHandler
    {
        private static readonly string[] DevDefaults =
        {
            "http://localhost:3000",
            "http://localhost:5173",
            "http://localhost:5174",
        };

        private static string[] _cache;
        private static string   _cacheRaw;

        private static string[] AllowedOrigins()
        {
            var raw = ConfigurationManager.AppSettings["Cors:AllowedOrigins"];
            if (string.IsNullOrWhiteSpace(raw)) return DevDefaults;

            // appSettings is re-read cheaply, but splitting on every request is waste.
            if (!string.Equals(raw, _cacheRaw, StringComparison.Ordinal))
            {
                _cache = raw.Split(',')
                            .Select(s => s.Trim().TrimEnd('/'))
                            .Where(s => s.Length > 0)
                            .ToArray();
                _cacheRaw = raw;
            }
            return _cache.Length > 0 ? _cache : DevDefaults;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string origin = null;
            if (request.Headers.Contains("Origin"))
                origin = request.Headers.GetValues("Origin").FirstOrDefault();

            bool isAllowed = origin != null &&
                             AllowedOrigins().Contains(origin.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);

            // Handle preflight
            if (request.Method == HttpMethod.Options)
            {
                var preflight = new HttpResponseMessage(HttpStatusCode.NoContent);
                if (isAllowed)
                    AddCorsHeaders(preflight, origin);
                return preflight;
            }

            var response = await base.SendAsync(request, cancellationToken);

            if (isAllowed)
                AddCorsHeaders(response, origin);

            return response;
        }

        private static void AddCorsHeaders(HttpResponseMessage response, string origin)
        {
            response.Headers.Remove("Access-Control-Allow-Origin");
            response.Headers.Add("Access-Control-Allow-Origin",  origin);
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization, X-Correlation-Id");
            response.Headers.Add("Access-Control-Expose-Headers", "X-Correlation-Id");
            response.Headers.Add("Access-Control-Allow-Credentials", "true");
        }
    }
}
