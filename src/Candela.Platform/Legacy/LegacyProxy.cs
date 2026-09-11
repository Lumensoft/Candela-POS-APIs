using System.Net;
using Candela.Platform.Http;
using Candela.Shared.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Newtonsoft.Json;

namespace Candela.Platform.Legacy;

/// <summary>
/// The migration's safety net: any <c>/api/*</c> request this process has no controller
/// for is forwarded, unchanged, to the .NET Framework host, and its answer is returned
/// to the caller untouched.
///
/// Why it exists
/// -------------
/// The tablet has exactly one base URL (apiClient.js reads a single server_url), so it
/// cannot split traffic between two processes. Without this, pointing it at Candela.Api
/// would mean every route that has not been migrated yet answers 404 — and worse, an
/// unauthenticated one answers 401, which the tablet's axios interceptor turns into a
/// forced logout mid-shift.
///
/// So the cutover cannot wait for the migration to finish. It also must not: PrintController
/// renders RDLC through Microsoft.Reporting.WinForms and stays on .NET Framework
/// permanently, so "migrate everything, then switch" would never reach the switch.
///
/// What it buys
/// ------------
///   * The tablet can be pointed at Candela.Api today. Nothing in the frontend changes —
///     its paths are already relative, so this is an IIS site-root change.
///   * Each migration becomes a silent route takeover: the moment a controller here
///     answers a path, the fallback stops seeing it.
///   * Rollback is one line. Drop the module's AddApplicationPart and the request falls
///     through to the old implementation, which is still sitting in the legacy host.
///
/// Where it sits
/// -------------
/// Registered with MapFallback, so it runs only when no endpoint matched — never in
/// front of a real controller. It is still behind JwtAuthMiddleware, so a forwarded
/// request has already had its token checked here; the legacy host checks it again with
/// the same secret, which is why the Authorization header is passed through.
///
/// This is not a general-purpose reverse proxy and should not grow into one. It handles
/// one hop, on loopback, between two processes that trust each other.
///
/// One known difference from before
/// --------------------------------
/// A fallback matches on path AND method, so sending a migrated route the wrong verb
/// (DELETE where only GET exists) is forwarded instead of answering 405. Nothing wrong
/// runs as a result: every controller here covers the same routes and verbs the legacy
/// controller did, so a request that missed locally misses there too and comes back 404.
/// The visible effect is 405 becoming 404. OPTIONS, the one verb a browser sends on its
/// own, is excluded above.
/// </summary>
public static class LegacyProxy
{
    /// <summary>Name of the HttpClient used for forwarding. Configured in Program.cs.</summary>
    public const string HttpClientName = "legacy-proxy";

    /// <summary>
    /// Stamped on every forwarded request. If a request arrives already carrying it, the
    /// forward target is this process itself — a misconfigured Legacy:AppUrl pointing back
    /// at Candela.Api — and forwarding again would loop until the caller times out. Seeing
    /// it means: refuse now, loudly.
    /// </summary>
    private const string LoopMarkerHeader = "X-Candela-Proxy-Hop";

    /// <summary>
    /// Headers that describe THIS connection rather than the message, so they must not be
    /// copied to the next hop. Content-Length is left out on purpose: HttpClient sets it
    /// from the body it is actually sending.
    /// </summary>
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host", "Content-Length"
    };

    /// <summary>
    /// Adds the fallback. Call after MapControllers().
    /// </summary>
    public static void MapLegacyFallback(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapFallback(async context =>
        {
            var path = context.Request.Path.Value ?? "";

            // Non-API traffic that reached the fallback is a client-side route of the
            // single-page app (/login, /sale, …). When the built frontend is deployed
            // alongside the API (one IIS site), serve its index.html so a page refresh or
            // a deep link works; the SPA router then takes over. UseStaticFiles has
            // already handled real files (/assets/*, /favicon.ico) before this point.
            // With no build present (dev, API-only), fall through to 404.
            if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                var env = context.RequestServices.GetService<IWebHostEnvironment>();
                var indexHtml = env is null ? null : Path.Combine(env.WebRootPath ?? "", "index.html");

                if (HttpMethods.IsGet(context.Request.Method)
                    && indexHtml is not null && File.Exists(indexHtml))
                {
                    context.Response.ContentType = "text/html";
                    await context.Response.SendFileAsync(indexHtml, context.RequestAborted);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // OPTIONS is never a real API call — it is a CORS preflight, and UseCors
            // already answers the ones that matter before routing runs. Forwarding it
            // would let the legacy host's CORS policy answer on this application's
            // behalf, which is not its decision to make.
            //
            // It also matters because a fallback matches on path AND method: OPTIONS on a
            // migrated route does not match that route's GET, so without this it would be
            // proxied and the caller would see the legacy host's answer instead of the 405
            // routing would have produced.
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            var options = context.RequestServices.GetRequiredService<LegacyHostOptions>();
            var appUrl = options.LegacyAppUrl();

            if (string.IsNullOrWhiteSpace(appUrl))
            {
                // Not configured — say so plainly rather than pretending the route is missing.
                AppLog.Error(null, "Legacy fallback is not configured (Legacy:AppUrl), cannot forward {0} {1}",
                    context.Request.Method, path);
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway,
                    "The request could not be completed.");
                return;
            }

            // Loop guard: this request already passed through a proxy hop, which means
            // Legacy:AppUrl resolves back to Candela.Api itself. Refuse immediately instead
            // of forwarding into a recursion that only ends when the tablet times out.
            if (context.Request.Headers.ContainsKey(LoopMarkerHeader))
            {
                AppLog.Error(null,
                    "Legacy:AppUrl ({0}) points back at Candela.Api — {1} {2} would loop. " +
                    "Point it at the legacy host's own address/port.",
                    appUrl, context.Request.Method, path);
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway,
                    "The request could not be completed.");
                return;
            }

            var factory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient(HttpClientName);

            var target = new Uri(appUrl.TrimEnd('/') + path + context.Request.QueryString);

            using var forwarded = BuildRequest(context, target);

            try
            {
                using var response = await http.SendAsync(forwarded,
                    HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

                await CopyResponseAsync(context, response);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The tablet went away. Nobody is left to answer.
            }
            catch (Exception ex)
            {
                // A dead legacy host must not look like a missing route, or the migration
                // status becomes impossible to read from the outside.
                AppLog.Error(ex, "Legacy fallback failed for {0} {1}", context.Request.Method, path);
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway,
                    "The request could not be completed.");
            }
        });
    }

    private static HttpRequestMessage BuildRequest(HttpContext context, Uri target)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);

        // GET/DELETE/HEAD may legitimately carry no body; sending an empty StreamContent
        // for them would add a Content-Length: 0 the original request did not have.
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            request.Content = new StreamContent(context.Request.Body);

        foreach (var header in context.Request.Headers)
        {
            if (HopByHop.Contains(header.Key)) continue;

            // Content-* headers belong on the content, everything else on the request.
            if (!request.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value);
        }

        // So one correlation id finds the request in both processes' logs.
        var correlationId = context.Items[CorrelationIdMiddleware.HeaderName] as string;
        if (!string.IsNullOrEmpty(correlationId) &&
            !request.Headers.Contains(CorrelationIdMiddleware.HeaderName))
            request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, correlationId);

        // Marks this as a proxied hop, so if the target turns out to be Candela.Api the
        // next hop can refuse rather than recurse.
        request.Headers.TryAddWithoutValidation(LoopMarkerHeader, "1");

        return request;
    }

    private static async Task CopyResponseAsync(HttpContext context, HttpResponseMessage response)
    {
        context.Response.StatusCode = (int)response.StatusCode;

        foreach (var header in response.Headers)
            if (!HopByHop.Contains(header.Key))
                context.Response.Headers[header.Key] = header.Value.ToArray();

        foreach (var header in response.Content.Headers)
            if (!HopByHop.Contains(header.Key))
                context.Response.Headers[header.Key] = header.Value.ToArray();

        // Kestrel decides its own framing, so whatever the legacy host chose is not ours
        // to repeat.
        context.Response.Headers.Remove("Transfer-Encoding");

        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        // Same body shape as ExceptionHandlingMiddleware, including the correlation id,
        // so the tablet's error handling does not need a special case for this path.
        return context.Response.WriteAsync(JsonConvert.SerializeObject(new
        {
            error = message,
            reference = AppLog.CorrelationId
        }));
    }
}
