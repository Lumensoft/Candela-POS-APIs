using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Web.Http;
using CandelaPOS.Infrastructure;
using DAL;

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

        // ESC p 0 50ms 250ms — cash drawer kick on pin 2 (DK port)
        private static readonly byte[] DrawerKick = { 0x1B, 0x70, 0x00, 0x32, 0xFA };

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

            CandelaBootstrap.PrepareRequest();

            int    shopId   = (int)   Request.Properties["shop_id"];
            string deviceId = (string)Request.Properties["device_id"];

            // Resolve printer name: app-supplied value takes priority; fall back to
            // tblComputerList.InvoicePrinterName (or Sec_InvoicePrinterName) for this device.
            string printerName = req.PrinterName;
            if (string.IsNullOrWhiteSpace(printerName) && !string.IsNullOrEmpty(deviceId))
                printerName = GetPrinterNameFromDb(deviceId, req.IsSecondary);

            if (string.IsNullOrWhiteSpace(printerName))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "No printer configured for this device. Please set InvoicePrinterName in tblComputerList." });

            int mode   = req.IsDuplicate ? 1 : 0;
            int copies = Math.Max(1, Math.Min(req.Copies > 0 ? req.Copies : 1, 5));
            int port   = req.PrinterPort > 0 ? req.PrinterPort : DefaultPort;
            bool openDrawer = req.OpenDrawer ?? true; // default true — cash sales always open drawer

            try
            {
                string receiptText = new SaleAndReturnDAL().GetOposInvoice(shopId, req.SaleId, mode);

                if (string.IsNullOrEmpty(receiptText))
                    return Request.CreateResponse(HttpStatusCode.NotFound,
                        new { error = $"No invoice data found for sale_id {req.SaleId} in shop {shopId}" });

                bool isTcp = System.Net.IPAddress.TryParse(printerName, out _);

                // RawPrinterHelper sends RAW bytes which bypass the driver pipeline entirely —
                // the driver's "Kick-out Drawer At: Page Start" setting is never triggered.
                // Embed the drawer kick explicitly in the payload for both paths.
                byte[] payload = BuildPayload(receiptText, copies, openDrawer);

                // If the value is a plain IP address → raw TCP:9100 (true LAN printer).
                // Anything else (printer name or \\server\share) → Windows spooler.
                if (isTcp)
                    SendTcp(printerName, port, payload);
                else
                    RawPrinterHelper.SendBytesToPrinter(printerName, payload);

                return Request.CreateResponse(HttpStatusCode.OK,
                    new { success = true,
                          sale_id = req.SaleId,
                          printer = printerName,
                          copies });
            }
            catch (SocketException ex)
            {
                return Request.CreateResponse(HttpStatusCode.BadGateway,
                    new { error = $"Could not reach printer at {printerName}:{port}: {ex.Message}" });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        private static string GetPrinterNameFromDb(string deviceId, bool secondary)
        {
            string col = secondary ? "Sec_InvoicePrinterName" : "InvoicePrinterName";
            try
            {
                using (var con = new System.Data.SqlClient.SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var cmd = new System.Data.SqlClient.SqlCommand(
                        $"SELECT TOP 1 {col} FROM tblComputerList WHERE deviceId = @deviceId", con);
                    cmd.Parameters.AddWithValue("@deviceId", deviceId);
                    return cmd.ExecuteScalar()?.ToString() ?? "";
                }
            }
            catch { return ""; }
        }

        // Encode receipt text as ESC/POS bytes.
        // Layout: [ESC @] [text + cut] × copies [drawer kick if openDrawer]
        // Drawer kick is appended once after all copies so it fires exactly once per job.
        // Code Page 437 is the ESC/POS standard encoding (handles basic Latin + box chars).
        private static byte[] BuildPayload(string receiptText, int copies, bool openDrawer = false)
        {
            // Normalize line endings to CR+LF (ESC/POS standard)
            string normalized  = receiptText.Replace("\r\n", "\n").Replace("\r", "\n")
                                            .Replace("\n", "\r\n");
            Encoding enc       = Encoding.GetEncoding(437);
            byte[]   textBytes = enc.GetBytes(normalized);

            int totalLen = InitPrinter.Length
                         + (textBytes.Length + FullCut.Length) * copies
                         + (openDrawer ? DrawerKick.Length : 0);

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

            if (openDrawer)
            {
                Buffer.BlockCopy(DrawerKick, 0, payload, offset, DrawerKick.Length);
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
        [Newtonsoft.Json.JsonProperty("sale_id")]
        public int    SaleId      { get; set; }

        // IP address → raw TCP:9100 (direct LAN printer)
        // Printer name or \\server\share → Windows spooler (USB/shared printer)
        [Newtonsoft.Json.JsonProperty("printer_name")]
        public string PrinterName { get; set; }

        [Newtonsoft.Json.JsonProperty("printer_port")]
        public int    PrinterPort { get; set; } // TCP only, optional, defaults to 9100

        [Newtonsoft.Json.JsonProperty("copies")]
        public int    Copies      { get; set; } // optional, defaults to 1, max 5

        [Newtonsoft.Json.JsonProperty("is_duplicate")]
        public bool   IsDuplicate { get; set; } // true → "Duplicate" printed on header

        [Newtonsoft.Json.JsonProperty("is_secondary")]
        public bool   IsSecondary { get; set; } // true → resolve Sec_InvoicePrinterName from tblComputerList

        // Null = default (true). Set false for kitchen/label printers that have no cash drawer.
        [Newtonsoft.Json.JsonProperty("open_drawer")]
        public bool?  OpenDrawer  { get; set; }
    }
}
