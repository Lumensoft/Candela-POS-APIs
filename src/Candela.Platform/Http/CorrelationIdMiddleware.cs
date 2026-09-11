using Candela.Shared.Logging;

namespace Candela.Platform.Http;

/// <summary>
/// Gives every request an id that appears in each log line and comes back in the
/// X-Correlation-Id response header. One id is then enough to find everything a failed
/// request did, including the part served by the .NET Framework host.
///
/// Honours an incoming id so a call can be traced end to end across both processes.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext context)
    {
        var id = context.Request.Headers[HeaderName].FirstOrDefault();

        // Cap the length so a hostile header cannot bloat every log line.
        if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("N")[..12];
        else if (id.Length > 64) id = id[..64];

        AppLog.CorrelationId = id;
        context.Items[HeaderName] = id;

        context.Response.OnStarting(() =>
        {
            if (!context.Response.Headers.ContainsKey(HeaderName))
                context.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });

        await _next(context);
    }
}
