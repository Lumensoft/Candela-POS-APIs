using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CandelaPOS.Shared.Http
{
    /// <summary>
    /// Fills Request.Properties with the caller's identity for /legacy/* requests, from
    /// the X-Ctx-* headers Candela.Api's LegacyHostClient attaches.
    ///
    /// Why this exists
    /// ---------------
    /// The DAL wrappers (HoldsLegacyController and the like) read
    /// <c>(int)Request.Properties["shop_id"]</c> exactly as the original controllers did,
    /// so their bodies could be copied across verbatim. But JwtAuthHandler skips /legacy/*
    /// — that channel carries no cashier token — so nothing else populates those
    /// properties. Without this handler every wrapper throws KeyNotFoundException on the
    /// first property it reads.
    ///
    /// Trust model: the values are set by Candela.Api AFTER it has validated the cashier's
    /// JWT, and /legacy/* is already reachable only from loopback and only with the shared
    /// secret. So this handler runs after LegacySecretHandler and simply copies the
    /// headers through — it does not re-authenticate anything.
    ///
    /// It is a no-op for every non-/legacy path, so /api/* is unaffected.
    /// </summary>
    public class LegacyContextHandler : DelegatingHandler
    {
        private const string RoutePrefix = "/legacy/";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri.AbsolutePath ?? "";
            if (path.IndexOf(RoutePrefix, StringComparison.OrdinalIgnoreCase) < 0)
                return base.SendAsync(request, cancellationToken);

            request.Properties["user_id"]    = ReadInt(request, "X-Ctx-User-Id");
            request.Properties["shop_id"]    = ReadInt(request, "X-Ctx-Shop-Id");
            request.Properties["pos_code"]   = ReadString(request, "X-Ctx-Pos-Code");
            request.Properties["user_name"]  = ReadString(request, "X-Ctx-User-Name");
            request.Properties["device_id"]  = ReadString(request, "X-Ctx-Device-Id");
            request.Properties["group_name"] = ReadString(request, "X-Ctx-Group-Name");
            request.Properties["group_type"] = ReadInt(request, "X-Ctx-Group-Type");

            return base.SendAsync(request, cancellationToken);
        }

        private static int ReadInt(HttpRequestMessage request, string header)
        {
            var raw = ReadString(request, header);
            return int.TryParse(raw, out var v) ? v : 0;
        }

        private static string ReadString(HttpRequestMessage request, string header)
        {
            if (request.Headers.TryGetValues(header, out IEnumerable<string> values))
                foreach (var v in values)
                    return v;
            return "";
        }
    }
}
