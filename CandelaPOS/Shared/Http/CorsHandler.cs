using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CandelaPOS.Infrastructure
{
    public class CorsHandler : DelegatingHandler
    {
        private static readonly string[] AllowedOrigins = new[]
        {
            "http://localhost:3000",
            "http://localhost:5173",
            "http://localhost:5174",
        };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string origin = null;
            if (request.Headers.Contains("Origin"))
                origin = request.Headers.GetValues("Origin").FirstOrDefault();

            bool isAllowed = origin != null && AllowedOrigins.Contains(origin);

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
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");
            response.Headers.Add("Access-Control-Allow-Credentials", "true");
        }
    }
}
