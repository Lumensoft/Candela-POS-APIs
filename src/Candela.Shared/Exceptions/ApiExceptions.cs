namespace Candela.Shared.Exceptions;

/// <summary>
/// Base for every failure the API knows how to describe to a caller.
///
/// The point of these types is that a controller or service can simply throw and stop
/// thinking about HTTP. One middleware turns the type into a status code and a message.
/// The alternative — the shape the .NET Framework host is in — is a try/catch in every
/// action (41 of them) each deciding for itself what to return, which is how two
/// endpoints end up answering the same failure differently.
///
/// Anything NOT derived from this is treated as an unexpected fault: logged in full,
/// answered with a generic 500 and a correlation id, never with exception text.
/// </summary>
public abstract class ApiException : Exception
{
    protected ApiException(string message, Exception? inner = null) : base(message, inner) { }

    /// <summary>HTTP status this failure maps to.</summary>
    public abstract int StatusCode { get; }

    /// <summary>
    /// True when Message is safe and useful to show the person at the till.
    /// False means the middleware substitutes a generic message and logs the real one.
    /// </summary>
    public virtual bool MessageIsSafeForClient => true;
}

/// <summary>400 — the request itself is malformed or fails a field-level rule.</summary>
public sealed class ValidationException : ApiException
{
    public ValidationException(string message) : base(message) { }
    public override int StatusCode => 400;
}

/// <summary>
/// 401 — the caller did not prove who they are. In practice: wrong credentials at
/// /api/auth/login or /api/auth/supervisor, since JwtAuthMiddleware answers token
/// problems itself before any controller runs.
///
/// Note for anyone adding a 401 elsewhere: the tablet's axios interceptor clears the
/// session and redirects to /login on ANY 401, so this must never be used for a failure
/// the cashier could correct without signing in again.
/// </summary>
public sealed class UnauthorizedException : ApiException
{
    public UnauthorizedException(string message) : base(message) { }
    public override int StatusCode => 401;
}

/// <summary>403 — authenticated, but not allowed to do this.</summary>
public sealed class ForbiddenException : ApiException
{
    public ForbiddenException(string message) : base(message) { }
    public override int StatusCode => 403;
}

/// <summary>404 — the thing asked for does not exist.</summary>
public sealed class NotFoundException : ApiException
{
    public NotFoundException(string message) : base(message) { }
    public override int StatusCode => 404;
}

/// <summary>409 — a conflicting state, e.g. a transaction id already used.</summary>
public sealed class ConflictException : ApiException
{
    public ConflictException(string message) : base(message) { }
    public override int StatusCode => 409;
}

/// <summary>
/// 422 — the request is well formed but a Candela business rule refuses it:
/// "POS is closed before the requested Transaction Date", "physical audit in progress".
///
/// These messages come from the DAL and are meant for the cashier, so they are passed
/// through unchanged. This is the one place exception text is deliberately shown.
/// </summary>
public sealed class BusinessRuleException : ApiException
{
    public BusinessRuleException(string message, Exception? inner = null) : base(message, inner) { }
    public override int StatusCode => 422;
}

/// <summary>
/// 502 — the .NET Framework legacy host could not be reached or failed.
///
/// Its message is never shown: it describes an internal hop the caller knows nothing
/// about, and may carry DAL detail. The real text goes to the log.
/// </summary>
public sealed class LegacyHostException : ApiException
{
    public LegacyHostException(string message, Exception? inner = null) : base(message, inner) { }
    public override int StatusCode => 502;
    public override bool MessageIsSafeForClient => false;
}
