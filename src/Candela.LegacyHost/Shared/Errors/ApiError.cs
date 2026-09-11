using System;
using System.Net;
using System.Net.Http;
using CandelaPOS.Shared.Logging;

namespace CandelaPOS.Shared.Errors
{
    /// <summary>
    /// One place to turn an unexpected exception into a 500 response.
    ///
    /// The body deliberately carries no exception text. A 500 means the caller can do
    /// nothing with the detail anyway, while the message would hand a compromised
    /// tablet a map of the schema — table names, columns, file paths. What support
    /// actually needs is the correlation id, which ties the response to the full
    /// stack trace in the log.
    ///
    /// Business failures the cashier can act on (validation, "POS is closed",
    /// "invoice already returned") must NOT go through here — they keep their own
    /// status code and their own message.
    /// </summary>
    public static class ApiError
    {
        public static HttpResponseMessage Internal(HttpRequestMessage request, Exception ex, string context)
        {
            AppLog.Error(ex, context);
            return request.CreateResponse(HttpStatusCode.InternalServerError,
                new { error = "An internal error occurred.", reference = AppLog.CorrelationId });
        }
    }
}
