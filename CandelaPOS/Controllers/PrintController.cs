using System;
using System.Configuration;
using System.IO;
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
        private const int ConnectTimeout = 5000;
        private const int WriteTimeout   = 5000;

        private static readonly byte[] InitPrinter = { 0x1B, 0x40 };
        private static readonly byte[] FullCut     = { 0x1D, 0x56, 0x00 };

        // POST api/print
        // Tries RDLC rendering (reads InvoiceType from tblComputerList for this POS terminal)
        // and falls back to ESC/POS text if RDLC isn't configured or the file is missing.
        [HttpPost, Route("")]
        public HttpResponseMessage Print([FromBody] PrintRequest req)
        {
            if (req == null || req.SaleId <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "sale_id is required and must be > 0" });

            // printer_name is optional — RDLC path uses tblComputerList.InvoicePrinterName.
            // A non-empty value here overrides the DB setting.

            CandelaBootstrap.PrepareRequest();

            int    shopId  = (int)Request.Properties["shop_id"];
            string posCode = Request.Properties["pos_code"]?.ToString() ?? "";
            int    mode    = req.IsDuplicate ? 1 : 0;
            int    copies  = Math.Max(1, Math.Min(req.Copies > 0 ? req.Copies : 1, 5));
            int    port    = req.PrinterPort > 0 ? req.PrinterPort : DefaultPort;
            string connStr = CandelaBootstrap.ConnectionString;

            try
            {
                // ── RDLC path ─────────────────────────────────────────────────────────
                // Request value (set by user in Setup screen) takes priority over Web.config.
                string basePath = !string.IsNullOrWhiteSpace(req.ReportsPath)
                                  ? req.ReportsPath
                                  : ConfigurationManager.AppSettings["CandelaReportPath"];
                if (!string.IsNullOrEmpty(basePath) && !string.IsNullOrEmpty(posCode))
                {
                    if (RdlcInvoicePrinter.TryGetComputerSettings(
                            posCode, connStr, req.IsSecondary, out var cs)
                        && !string.IsNullOrEmpty(cs?.InvoiceType))
                    {
                        var info = RdlcInvoicePrinter.GetReportInfo(cs.InvoiceType, basePath);
                        if (info != null && File.Exists(info.RdlcPath))
                        {
                            // req.PrinterName from the device takes precedence;
                            // fall back to the printer saved in tblComputerList.
                            string printer = !string.IsNullOrWhiteSpace(req.PrinterName)
                                             ? req.PrinterName
                                             : cs.InvoicePrinterName;

                            RdlcInvoicePrinter.Print(shopId, req.SaleId, mode, copies,
                                                     printer, cs, info, connStr);

                            return Request.CreateResponse(HttpStatusCode.OK,
                                new { success      = true,
                                      sale_id      = req.SaleId,
                                      printer,
                                      copies,
                                      invoice_type = cs.InvoiceType,
                                      mode         = "rdlc" });
                        }
                    }
                }

                // ── ESC/POS fallback ──────────────────────────────────────────────────
                string receiptText = new SaleAndReturnDAL().GetOposInvoice(shopId, req.SaleId, mode);

                if (string.IsNullOrEmpty(receiptText))
                    return Request.CreateResponse(HttpStatusCode.NotFound,
                        new { error = $"No invoice data found for sale_id {req.SaleId} in shop {shopId}" });

                byte[] payload = BuildPayload(receiptText, copies);

                if (IPAddress.TryParse(req.PrinterName, out _))
                    SendTcp(req.PrinterName, port, payload);
                else
                    RawPrinterHelper.SendBytesToPrinter(req.PrinterName, payload);

                return Request.CreateResponse(HttpStatusCode.OK,
                    new { success = true,
                          sale_id = req.SaleId,
                          printer = req.PrinterName,
                          copies,
                          mode    = "escpos" });
            }
            catch (SocketException ex)
            {
                return Request.CreateResponse(HttpStatusCode.BadGateway,
                    new { error = $"Could not reach printer at {req.PrinterName}:{port}: {ex.Message}" });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message, detail = ex.ToString() });
            }
        }

        private static byte[] BuildPayload(string receiptText, int copies)
        {
            string normalized = receiptText.Replace("\r\n", "\n").Replace("\r", "\n")
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
        [Newtonsoft.Json.JsonProperty("sale_id")]
        public int    SaleId      { get; set; }

        // IP address → raw TCP:9100 (direct LAN printer)
        // Printer name or \\server\share → Windows spooler (USB/shared printer)
        [Newtonsoft.Json.JsonProperty("printer_name")]
        public string PrinterName { get; set; }

        [Newtonsoft.Json.JsonProperty("printer_port")]
        public int    PrinterPort { get; set; }    // TCP only, optional, defaults to 9100

        [Newtonsoft.Json.JsonProperty("copies")]
        public int    Copies      { get; set; }    // optional, defaults to 1, max 5

        [Newtonsoft.Json.JsonProperty("is_duplicate")]
        public bool   IsDuplicate { get; set; }    // true → "Duplicate" header on receipt

        [Newtonsoft.Json.JsonProperty("is_secondary")]
        public bool   IsSecondary { get; set; }    // true → use Sec_* columns from tblComputerList

        // Optional: overrides CandelaReportPath in Web.config.
        // Set once in the Setup screen so Web.config never needs manual editing.
        [Newtonsoft.Json.JsonProperty("reports_path")]
        public string ReportsPath { get; set; }
    }
}
