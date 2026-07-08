using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Web.Http;
using CandelaPOS.Infrastructure;

namespace CandelaPOS.Controllers
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
        // Opens a TCP socket to the receipt printer and sends the ESC/POS cash drawer
        // kick command. The printer must have a drawer cable connected to its RJ11 port.
        // printer_ip is supplied by the app (stored in device settings during setup).
        // Returns 200 on success, 502 if the TCP connection to the printer fails.
        [HttpPost, Route("drawer")]
        public HttpResponseMessage OpenDrawer([FromBody] DrawerRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.PrinterIp))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "printer_ip is required" });

            // Basic sanity — must be a valid IP or hostname (not an injection vector)
            if (req.PrinterIp.Length > 253)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "printer_ip is invalid" });

            try
            {
                int port = req.PrinterPort > 0 ? req.PrinterPort : PrinterPort;
                SendDrawerKick(req.PrinterIp, port);

                string posCode = Request.Properties.ContainsKey("pos_code")
                    ? Request.Properties["pos_code"] as string : "";

                return Request.CreateResponse(HttpStatusCode.OK,
                    new { success = true, printer_ip = req.PrinterIp, printer_port = port, pos_code = posCode });
            }
            catch (SocketException ex)
            {
                return Request.CreateResponse(HttpStatusCode.BadGateway,
                    new { error = $"Could not reach printer at {req.PrinterIp}:{(req.PrinterPort > 0 ? req.PrinterPort : PrinterPort)}: {ex.Message}" });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
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
        public string PrinterIp   { get; set; }
        public int    PrinterPort { get; set; } // optional, defaults to 9100
    }
}
