using System.Web.Http.ExceptionHandling;
using CandelaPOS.Shared.Logging;

namespace CandelaPOS.Shared.Errors
{
    /// <summary>
    /// Catches every exception Web API sees that no controller handled, and writes it
    /// to the log with its correlation id. Purely additive: an ExceptionLogger does not
    /// alter the response, so the client contract is unchanged.
    /// </summary>
    public class GlobalExceptionLogger : ExceptionLogger
    {
        public override void Log(ExceptionLoggerContext context)
        {
            try
            {
                var req = context?.Request;
                AppLog.Error(context?.Exception,
                    "Unhandled exception for {0} {1}",
                    req?.Method?.Method ?? "?",
                    req?.RequestUri?.AbsolutePath ?? "?");
            }
            catch { }
        }
    }
}
