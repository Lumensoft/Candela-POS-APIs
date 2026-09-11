using System;
using System.Configuration;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CandelaPOS.Shared.Logging;

namespace CandelaPOS.Shared.Http
{
    /// <summary>
    /// Guards the /legacy/* routes, which exist only for Candela.Api to call.
    ///
    /// These endpoints can post a sale and move stock, so they are protected twice over:
    ///
    ///   1. IIS restricts the /legacy application to 127.0.0.1, so nothing off the
    ///      machine can reach them at all.
    ///   2. This handler requires a shared secret header, so another process on the same
    ///      machine cannot call them either — a loopback bind alone would not stop that.
    ///
    /// They are deliberately NOT behind the tablet's JWT: the caller is our own API
    /// process, which has no cashier session, and giving it one would mean minting a
    /// token with nobody behind it.
    /// </summary>
    public class LegacySecretHandler : DelegatingHandler
    {
        public const string HeaderName = "X-Legacy-Secret";
        private const string RoutePrefix = "/legacy/";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri.AbsolutePath ?? "";
            if (path.IndexOf(RoutePrefix, StringComparison.OrdinalIgnoreCase) < 0)
                return base.SendAsync(request, cancellationToken);

            var expected = ConfigurationManager.AppSettings["Legacy:SharedSecret"];
            if (string.IsNullOrWhiteSpace(expected))
            {
                // Refuse rather than fall open. An unset secret means the deployment is
                // misconfigured, and these routes are too powerful to serve on a guess.
                AppLog.Error(null, "Legacy:SharedSecret is not configured; refusing {0}", path);
                return Deny(request, "Legacy endpoint is not configured.");
            }

            string supplied = null;
            System.Collections.Generic.IEnumerable<string> values;
            if (request.Headers.TryGetValues(HeaderName, out values))
            {
                foreach (var v in values) { supplied = v; break; }
            }

            if (string.IsNullOrEmpty(supplied) || !FixedTimeEquals(supplied, expected))
            {
                AppLog.Warn("Rejected {0}: missing or wrong {1}", path, HeaderName);
                return Deny(request, "Not authorised.");
            }

            return base.SendAsync(request, cancellationToken);
        }

        private static Task<HttpResponseMessage> Deny(HttpRequestMessage request, string reason)
        {
            var response = request.CreateResponse(HttpStatusCode.Forbidden, new { error = reason });
            return Task.FromResult(response);
        }

        /// <summary>
        /// Compares every byte regardless of where the first difference falls, so the time
        /// taken does not reveal how much of a guessed secret was correct.
        /// </summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            var ba = Encoding.UTF8.GetBytes(a);
            var bb = Encoding.UTF8.GetBytes(b);
            if (ba.Length != bb.Length) return false;

            var diff = 0;
            for (var i = 0; i < ba.Length; i++) diff |= ba[i] ^ bb[i];
            return diff == 0;
        }
    }
}
