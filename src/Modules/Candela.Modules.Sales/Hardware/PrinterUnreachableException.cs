using Candela.Shared.Exceptions;

namespace Candela.Modules.Sales.Hardware;

/// <summary>
/// 502 — the printer did not answer.
///
/// The net48 endpoint caught SocketException and returned
/// <c>502 { error: "Could not reach the printer." }</c> with the socket detail logged but
/// never sent. That distinction is the point: telling the caller apart "connection
/// refused" from "timed out" is exactly what turns this endpoint into a port scanner for
/// anyone holding a token. So the message is fixed and deliberately uninformative, and
/// MessageIsSafeForClient stays true because this IS the message we mean to send — not
/// something the middleware should replace.
/// </summary>
public sealed class PrinterUnreachableException : ApiException
{
    public PrinterUnreachableException(Exception? inner = null)
        : base("Could not reach the printer.", inner) { }

    public override int StatusCode => 502;
}
