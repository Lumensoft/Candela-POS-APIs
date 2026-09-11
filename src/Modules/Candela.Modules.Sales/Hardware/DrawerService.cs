using System.Net.Sockets;
using Candela.Modules.Sales.Hardware.Dtos;
using Candela.Platform.Printing;
using Candela.Shared.Exceptions;
using Candela.Shared.Logging;

namespace Candela.Modules.Sales.Hardware;

/// <summary>
/// Ported from the net48 HardwareController. The ESC/POS bytes, the timeouts, the order
/// of the checks, the log messages and the two response shapes are unchanged.
///
/// One thing that looks like a bug and is kept: if the caller sends BOTH PrinterIp and
/// PrinterName, the TCP path wins and the name is ignored. That is what the original did,
/// and a till configured with both should keep behaving the way it does today.
/// </summary>
public sealed class DrawerService(PrinterGuard guard) : IDrawerService
{
    // ESC/POS cash drawer kick: ESC p 0 50ms 250ms (pin 2)
    private static readonly byte[] DrawerKickBytes = { 0x1B, 0x70, 0x00, 0x32, 0xFA };

    private const int PrinterPort = 9100;
    private const int ConnectTimeout = 3000; // ms — fail fast if printer unreachable
    private const int WriteTimeout = 2000;

    public async Task<DrawerResponse> OpenAsync(DrawerRequest req, string posCode, CancellationToken ct)
    {
        bool hasIp = !string.IsNullOrWhiteSpace(req.PrinterIp);
        bool hasName = !string.IsNullOrWhiteSpace(req.PrinterName);

        if (!hasIp && !hasName)
            throw new ValidationException("Either printer_ip or printer_name is required");

        // SSRF guard. Both branches make the server reach out to a target the
        // caller chose, so validate before connecting — see PrinterGuard.
        int wantedPort = req.PrinterPort > 0 ? req.PrinterPort : PrinterPort;
        var check = hasIp
            ? guard.CheckTcp(req.PrinterIp, wantedPort)
            : guard.CheckPrinterName(req.PrinterName);

        if (!check.Ok)
        {
            AppLog.Warn("Rejected drawer target ip={0} port={1} name={2}: {3}",
                req.PrinterIp ?? "-", wantedPort, req.PrinterName ?? "-", check.Reason);
            throw new ValidationException(check.Reason);
        }

        if (hasIp)
        {
            int port = req.PrinterPort > 0 ? req.PrinterPort : PrinterPort;
            await SendDrawerKickAsync(req.PrinterIp!, port, ct);

            return new DrawerResponse
            {
                Success = true,
                PrinterIp = req.PrinterIp,
                PrinterPort = port,
                PosCode = posCode
            };
        }

        // Windows spooler path — send raw ESC/POS bytes through the driver.
        // The Posiflex driver's "Kick-out Drawer At" setting is NOT used here;
        // we send the command explicitly so this works on any ESC/POS-capable driver.
        RawPrinterHelper.SendBytesToPrinter(req.PrinterName!, DrawerKickBytes);

        return new DrawerResponse
        {
            Success = true,
            PrinterName = req.PrinterName,
            PosCode = posCode
        };
    }

    private static async Task SendDrawerKickAsync(string ip, int port, CancellationToken ct)
    {
        using var client = new TcpClient();

        // The original used ConnectAsync(...).Wait(ConnectTimeout) because .NET Framework
        // had no cancellable overload. Here the timeout is a linked token, which keeps the
        // same 3s budget without blocking a thread — and keeps the caller's own
        // cancellation distinct from a timeout, so a tablet that went away is not reported
        // as an unreachable printer.
        using var timeout = new CancellationTokenSource(ConnectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            await client.ConnectAsync(ip, port, linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Same outcome as the original's Wait() returning false: SocketError.TimedOut
            // on the way to a 502.
            Warn(ip, port, SocketError.TimedOut);
            throw new PrinterUnreachableException();
        }
        catch (SocketException ex)
        {
            Warn(ip, port, ex.SocketErrorCode);
            throw new PrinterUnreachableException(ex);
        }

        try
        {
            using var stream = client.GetStream();
            stream.WriteTimeout = WriteTimeout;
            await stream.WriteAsync(DrawerKickBytes, ct);
            await stream.FlushAsync(ct);
        }
        catch (SocketException ex)
        {
            Warn(ip, port, ex.SocketErrorCode);
            throw new PrinterUnreachableException(ex);
        }
        catch (IOException ex) when (ex.InnerException is SocketException se)
        {
            // A write that times out surfaces as IOException wrapping the socket error;
            // the original's catch was on SocketException, which NetworkStream.Write threw
            // directly on .NET Framework. Same failure, same answer.
            Warn(ip, port, se.SocketErrorCode);
            throw new PrinterUnreachableException(ex);
        }
    }

    /// <summary>
    /// Never echo the socket error back. The difference between "connection
    /// refused" and "timed out" is exactly what turns this endpoint into a
    /// port scanner for whoever holds a token — so it is logged, not returned.
    /// </summary>
    private static void Warn(string ip, int port, SocketError error) =>
        AppLog.Warn("Drawer kick failed for {0}:{1} - {2}", ip, port, error);
}
