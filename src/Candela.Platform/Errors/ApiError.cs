using Candela.Shared.Logging;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Platform.Errors;

/// <summary>
/// One place an unexpected exception becomes a 500.
///
/// The body carries no exception text. A 500 means the caller can do nothing with the
/// detail anyway, while the message would hand a compromised tablet a map of the
/// schema. Support gets the correlation id instead, which finds the full stack trace
/// in the log.
///
/// Shape is identical to the .NET Framework host so the tablet sees no difference
/// whichever process answered:
///
///     { "error": "An internal error occurred.", "reference": "&lt;correlation id&gt;" }
///
/// Business failures the cashier can act on must NOT come through here — they keep
/// their own status code and their own message.
/// </summary>
public static class ApiError
{
    public static IActionResult Internal(Exception? ex, string context)
    {
        AppLog.Error(ex, context);
        return new JsonResult(new
        {
            error = "An internal error occurred.",
            reference = AppLog.CorrelationId
        })
        {
            StatusCode = StatusCodes.Status500InternalServerError
        };
    }
}
