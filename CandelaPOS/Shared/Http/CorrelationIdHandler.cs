using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace CandelaPOS.Shared.Http
{
    /// <summary>
    /// Gives every request an id that appears in each log line and is echoed back in
    /// the X-Correlation-Id response header. When a cashier reports a problem, that
    /// one id is enough to find every log line for the failed request — including the
    /// ones written after the call crosses into the Candela DAL.
    ///
    /// Honours an incoming X-Correlation-Id so the tablet (or a future gateway) can
    /// trace a call end to end.
    /// </summary>
    public class CorrelationIdHandler : DelegatingHandler
    {
        public const string HeaderName = "X-Correlation-Id";
        public const string ItemKey    = "correlation_id";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string id = null;
            System.Collections.Generic.IEnumerable<string> incoming;
            if (request.Headers.TryGetValues(HeaderName, out incoming))
            {
                foreach (var v in incoming)
                {
                    if (!string.IsNullOrWhiteSpace(v)) { id = v.Trim(); break; }
                }
            }

            // Cap length so a hostile header can't bloat every log line.
            if (string.IsNullOrEmpty(id)) id = Guid.NewGuid().ToString("N").Substring(0, 12);
            else if (id.Length > 64)     id = id.Substring(0, 64);

            request.Properties[ItemKey] = id;
            try { if (HttpContext.Current != null) HttpContext.Current.Items[ItemKey] = id; } catch { }

            var response = await base.SendAsync(request, cancellationToken);

            try
            {
                if (response != null && !response.Headers.Contains(HeaderName))
                    response.Headers.Add(HeaderName, id);
            }
            catch { }

            return response;
        }
    }
}
