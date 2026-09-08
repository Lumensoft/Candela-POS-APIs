using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Web.Http;
using CandelaPOS.Shared.Errors;
using CandelaPOS.Shared.Logging;
using CandelaPOS.Shared.Net;

namespace CandelaPOS.Features.Hardware
{
    [RoutePrefix("api/hardware")]
    public class HardwareController : ApiController
    {
        // ESC/POS cash drawer kick: ESC p 0 50ms 250ms (pin 2)
        private static readonly byte[] DrawerKickBytes = { 0x1B, 0x70, 0x00, 0x32, 0xFA };

        private const int PrinterPort    = 9100;
        private const int ConnectTimeout = 3000; // ms — fail fast if printer unreachable
        private const int WriteTimeout   = 2000;

        // POST api/hardware/drawer
        // Opens the cash drawer without printing a receipt.
        // Supports two paths:
        //   printer_ip   → raw TCP:9100 ESC/POS command (LAN printer)
        //   printer_name → Windows print spooler via RawPrinterHelper (USB/shared printer)
        // Supply exactly one of the two. printer_port is TCP-only, defaults to 9100.
        [HttpPost, Route("drawer")]
        public HttpResponseMessage OpenDrawer([FromBody] DrawerRequest req)
        {
            if (req == null)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "Request body is required" });

            bool hasIp   = !string.IsNullOrWhiteSpace(req.PrinterIp);
            bool hasName = !string.IsNullOrWhiteSpace(req.PrinterName);

            if (!hasIp && !hasName)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "Either printer_ip or printer_name is required" });

            // SSRF guard. Both branches make the server reach out to a target the
            // caller chose, so validate before connecting — see PrinterGuard.
            int wantedPort = req.PrinterPort > 0 ? req.PrinterPort : PrinterPort;
            var check = hasIp
                ? PrinterGuard.CheckTcp(req.PrinterIp, wantedPort)
                : PrinterGuard.CheckPrinterName(req.PrinterName);
            if (!check.Ok)
            {
                AppLog.Warn("Rejected drawer target ip={0} port={1} name={2}: {3}",
                    req.PrinterIp ?? "-", wantedPort, req.PrinterName ?? "-", check.Reason);
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = check.Reason });
            }

            string posCode = Request.Properties.ContainsKey("pos_code")
                ? Request.Properties["pos_code"] as string : "";

            try
            {
                if (hasIp)
                {
                    int port = req.PrinterPort > 0 ? req.PrinterPort : PrinterPort;
                    SendDrawerKick(req.PrinterIp, port);
                    return Request.CreateResponse(HttpStatusCode.OK,
                        new { success = true, printer_ip = req.PrinterIp, printer_port = port, pos_code = posCode });
                }
                else
                {
                    // Windows spooler path — send raw ESC/POS bytes through the driver.
                    // The Posiflex driver's "Kick-out Drawer At" setting is NOT used here;
                    // we send the command explicitly so this works on any ESC/POS-capable driver.
                    RawPrinterHelper.SendBytesToPrinter(req.PrinterName, DrawerKickBytes);
                    return Request.CreateResponse(HttpStatusCode.OK,
                        new { success = true, printer_name = req.PrinterName, pos_code = posCode });
                }
            }
            catch (SocketException ex)
            {
                // Never echo the socket error back. The difference between "connection
                // refused" and "timed out" is exactly what turns this endpoint into a
                // port scanner for whoever holds a token.
                AppLog.Warn("Drawer kick failed for {0}:{1} - {2}",
                    req.PrinterIp, wantedPort, ex.SocketErrorCode);
                return Request.CreateResponse(HttpStatusCode.BadGateway,
                    new { error = "Could not reach the printer." });
            }
            catch (Exception ex)
            {
                return ApiError.Internal(Request, ex, "HardwareController.OpenDrawer");
            }
        }

        private static void SendDrawerKick(string ip, int port)
        {
            using (var client = new TcpClient())
            {
                // ConnectAsync + Wait gives us a cancellable timeout on .NET Fx
                var connectTask = client.ConnectAsync(ip, port);
                if (!connectTask.Wait(ConnectTimeout))
                    throw new SocketException((int)SocketError.TimedOut);

                if (connectTask.Exception != null)
                    throw connectTask.Exception.InnerException ?? connectTask.Exception;

                using (var stream = client.GetStream())
                {
                    stream.WriteTimeout = WriteTimeout;
                    stream.Write(DrawerKickBytes, 0, DrawerKickBytes.Length);
                    stream.Flush();
                }
            }
        }
    }

    public class DrawerRequest
    {
        public string PrinterIp   { get; set; } // TCP path: LAN printer IP address
        public string PrinterName { get; set; } // Spooler path: Windows printer name or \\server\share
        public int    PrinterPort { get; set; } // TCP only, optional, defaults to 9100
    }
}
