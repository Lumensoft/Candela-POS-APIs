using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Web.Http;
using CandelaPOS.Infrastructure;
using DAL.ShopActivities;

namespace CandelaPOS.Controllers
{
    [RoutePrefix("api/print")]
    public class PrintController : ApiController
    {
        private const int DefaultPort    = 9100;
        private const int ConnectTimeout = 5000; // ms
        private const int WriteTimeout   = 5000; // ms

        // ESC @ — initialize/reset printer
        private static readonly byte[] InitPrinter = { 0x1B, 0x40 };

        // GS V 0 — full cut (most thermal printers support this)
        private static readonly byte[] FullCut = { 0x1D, 0x56, 0x00 };

        // POST api/print
        // Renders a sale receipt as ESC/POS and sends it to the thermal printer via TCP.
        // Uses spSalesInvoiceNew (same SP as Candela's 3-inch text receipt) for all
        // receipt data — no RDLC or ReportViewer dependency.
        // printer_ip is supplied by the app (stored in device settings during setup).
        // is_duplicate=true prints "Duplicate" header on the receipt (same as Candela
        // reprint mode).
        [HttpPost, Route("")]
        public HttpResponseMessage Print([FromBody] PrintRequest req)
        {
            if (req == null || req.SaleId <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "sale_id is required and must be > 0" });

            if (string.IsNullOrWhiteSpace(req.PrinterIp))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "printer_ip is required" });

            CandelaBootstrap.PrepareRequest();

            int shopId = (int)Request.Properties["shop_id"];
            int mode   = req.IsDuplicate ? 1 : 0;
            int copies = Math.Max(1, Math.Min(req.Copies > 0 ? req.Copies : 1, 5));
            int port   = req.PrinterPort > 0 ? req.PrinterPort : DefaultPort;

            try
            {
                string receiptText = new SaleAndReturnDAL().GetOposInvoice(shopId, req.SaleId, mode);

                if (string.IsNullOrEmpty(receiptText))
                    return Request.CreateResponse(HttpStatusCode.NotFound,
                        new { error = $"No invoice data found for sale_id {req.SaleId} in shop {shopId}" });

                byte[] payload = BuildPayload(receiptText, copies);
                SendTcp(req.PrinterIp, port, payload);

                return Request.CreateResponse(HttpStatusCode.OK,
                    new { success = true,
                          sale_id      = req.SaleId,
                          printer_ip   = req.PrinterIp,
                          printer_port = port,
                          copies });
            }
            catch (SocketException ex)
            {
                return Request.CreateResponse(HttpStatusCode.BadGateway,
                    new { error = $"Could not reach printer at {req.PrinterIp}:{port}: {ex.Message}" });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }
        }

        // Encode receipt text as ESC/POS bytes.
        // Layout per copy: [ESC @] [text bytes] [GS V 0]
        // The text from GetOposInvoice already includes 6 trailing blank lines for tear-off.
        // Code Page 437 is the ESC/POS standard encoding (handles basic Latin + box chars).
        private static byte[] BuildPayload(string receiptText, int copies)
        {
            // Normalize line endings to CR+LF (ESC/POS standard)
            string normalized  = receiptText.Replace("\r\n", "\n").Replace("\r", "\n")
                                            .Replace("\n", "\r\n");
            Encoding enc       = Encoding.GetEncoding(437);
            byte[]   textBytes = enc.GetBytes(normalized);

            int totalLen = InitPrinter.Length
                         + (textBytes.Length + FullCut.Length) * copies;

            var payload = new byte[totalLen];
            int offset  = 0;

            Buffer.BlockCopy(InitPrinter, 0, payload, offset, InitPrinter.Length);
            offset += InitPrinter.Length;

            for (int i = 0; i < copies; i++)
            {
                Buffer.BlockCopy(textBytes, 0, payload, offset, textBytes.Length);
                offset += textBytes.Length;
                Buffer.BlockCopy(FullCut,   0, payload, offset, FullCut.Length);
                offset += FullCut.Length;
            }

            return payload;
        }

        private static void SendTcp(string ip, int port, byte[] data)
        {
            using (var client = new TcpClient())
            {
                var task = client.ConnectAsync(ip, port);
                if (!task.Wait(ConnectTimeout))
                    throw new SocketException((int)SocketError.TimedOut);

                if (task.Exception != null)
                    throw task.Exception.InnerException ?? task.Exception;

                using (var stream = client.GetStream())
                {
                    stream.WriteTimeout = WriteTimeout;
                    stream.Write(data, 0, data.Length);
                    stream.Flush();
                }
            }
        }
    }

    public class PrintRequest
    {
        public int    SaleId      { get; set; }
        public string PrinterIp   { get; set; }
        public int    PrinterPort { get; set; } // optional, defaults to 9100
        public int    Copies      { get; set; } // optional, defaults to 1, max 5
        public bool   IsDuplicate { get; set; } // true → "Duplicate" printed on header
    }
}
