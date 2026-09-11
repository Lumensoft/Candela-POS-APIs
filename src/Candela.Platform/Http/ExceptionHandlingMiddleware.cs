using Candela.Shared.Exceptions;
using Candela.Shared.Logging;
using Newtonsoft.Json;

namespace Candela.Platform.Http;

/// <summary>
/// Turns any exception into the response shape the tablet already understands.
///
/// This is what replaces the try/catch in every action. A controller or service simply
/// throws — NotFoundException, BusinessRuleException, or anything else — and this
/// decides the status code, the message and what gets logged. There is exactly one
/// place that answers a failure, so two endpoints cannot describe the same failure
/// differently.
///
/// Response bodies, unchanged from the .NET Framework host:
///   expected failure   { "error": "POS is closed before the requested Transaction Date" }
///   unexpected fault   { "error": "An internal error occurred.", "reference": "a3f2b8c9" }
///
/// Note this deliberately does NOT use ProblemDetails. The tablet reads
/// response.data.error in 47 places and knows nothing about RFC 7807.
///
/// Registered first in the pipeline so it also covers correlation, CORS and auth.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;

    public ExceptionHandlingMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The till navigated away or the tablet dropped the connection. Not a fault,
            // and there is nobody left to answer, so do not log it as an error.
            AppLog.Info("Request cancelled by the client: {0} {1}",
                context.Request.Method, context.Request.Path);
        }
        catch (ApiException ex)
        {
            await WriteExpectedAsync(context, ex);
        }
        catch (Exception ex)
        {
            await WriteUnexpectedAsync(context, ex);
        }
    }

    /// <summary>
    /// A failure we described on purpose. Logged as a warning, not an error: it is the
    /// system working, not breaking, and mixing the two makes the log useless.
    /// </summary>
    private static Task WriteExpectedAsync(HttpContext context, ApiException ex)
    {
        AppLog.Warn("{0} {1} -> {2}: {3}",
            context.Request.Method, context.Request.Path, ex.StatusCode, ex.Message);

        var message = ex.MessageIsSafeForClient
            ? ex.Message
            : "The request could not be completed.";

        // A message unsafe to show still has to be recoverable from the log, so give the
        // caller the correlation id to quote.
        object body = ex.MessageIsSafeForClient
            ? new { error = message }
            : new { error = message, reference = AppLog.CorrelationId };

        return WriteAsync(context, ex.StatusCode, body);
    }

    /// <summary>
    /// Anything we did not anticipate. The full exception goes to the log; the caller
    /// gets a generic message and the correlation id. Exception text is never returned:
    /// the caller can do nothing with it, while it would hand a compromised tablet a
    /// map of the schema.
    /// </summary>
    private static Task WriteUnexpectedAsync(HttpContext context, Exception ex)
    {
        AppLog.Error(ex, "Unhandled exception for {0} {1}",
            context.Request.Method, context.Request.Path);

        return WriteAsync(context, StatusCodes.Status500InternalServerError, new
        {
            error = "An internal error occurred.",
            reference = AppLog.CorrelationId
        });
    }

    private static async Task WriteAsync(HttpContext context, int statusCode, object body)
    {
        if (context.Response.HasStarted)
        {
            // Headers are already on the wire, so the body cannot be replaced. Record it
            // rather than throwing a second exception on top of the first.
            AppLog.Warn("Response had already started; could not write the error body for {0}",
                context.Request.Path);
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonConvert.SerializeObject(body));
    }
}
