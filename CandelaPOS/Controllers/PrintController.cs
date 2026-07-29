using System;
using System.Configuration;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using CandelaPOS.Infrastructure;

namespace CandelaPOS.Controllers
{
    [RoutePrefix("api/print")]
    public class PrintController : ApiController
    {
        // ESC p 0 50ms 250ms — cash drawer kick on pin 2 (DK port)
        private static readonly byte[] DrawerKick = { 0x1B, 0x70, 0x00, 0x32, 0xFA };

        // POST api/print
        // Reads InvoiceType, InvoicePrinterName, and page settings from tblComputerList
        // for this POS terminal (by posCode), renders the matching RDLC, and sends it to
        // the Windows shared printer. Optionally fires a cash drawer kick after printing.
        [HttpPost, Route("")]
        public HttpResponseMessage Print([FromBody] PrintRequest req)
        {
            if (req == null || req.SaleId <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "sale_id is required and must be > 0" });

            CandelaBootstrap.PrepareRequest();

            int    shopId  = (int)   Request.Properties["shop_id"];
            string posCode = (string)Request.Properties["pos_code"];
            int    mode    = req.IsDuplicate ? 1 : 0;
            int    copies  = Math.Max(1, Math.Min(req.Copies > 0 ? req.Copies : 1, 5));
            string connStr = CandelaBootstrap.ConnectionString;

            // IsEnableCashDrawer from tblRCMSConfiguration (System Config → External Devices).
            // req.OpenDrawer overrides per-job (e.g. false for kitchen/label printers).
            var    config        = CandelaBootstrap.GetRCMSConfig();
            bool   drawerEnabled = config.TryGetValue("IsEnableCashDrawer", out string dv)
                                   && dv.Equals("True", StringComparison.OrdinalIgnoreCase);
            bool   openDrawer    = req.OpenDrawer ?? drawerEnabled;

            // reports_path from the app (Device Setup screen) takes priority over Web.config.
            string basePath = !string.IsNullOrWhiteSpace(req.ReportsPath)
                              ? req.ReportsPath
                              : ConfigurationManager.AppSettings["CandelaReportPath"];

            if (string.IsNullOrWhiteSpace(basePath))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "No reports path configured. Set it in Device Setup or add CandelaReportPath to Web.config." });

            try
            {
                if (!RdlcInvoicePrinter.TryGetComputerSettings(
                        posCode, connStr, req.IsSecondary, out var cs)
                    || string.IsNullOrEmpty(cs?.InvoiceType))
                    return Request.CreateResponse(HttpStatusCode.BadRequest,
                        new { error = $"No InvoiceType configured in tblComputerList for POS terminal {posCode}." });

                var info = RdlcInvoicePrinter.GetReportInfo(cs.InvoiceType, basePath);
                if (info == null)
                    return Request.CreateResponse(HttpStatusCode.BadRequest,
                        new { error = $"InvoiceType '{cs.InvoiceType}' is not mapped to an RDLC file." });

                if (!File.Exists(info.RdlcPath))
                    return Request.CreateResponse(HttpStatusCode.BadRequest,
                        new { error = $"RDLC file not found: {info.RdlcPath}" });

                // req.PrinterName from the app takes precedence;
                // fall back to the printer saved in tblComputerList.
                string printer = !string.IsNullOrWhiteSpace(req.PrinterName)
                                 ? req.PrinterName
                                 : cs.InvoicePrinterName;

                RdlcInvoicePrinter.Print(shopId, req.SaleId, mode, copies,
                                         printer, cs, info, connStr);

                if (openDrawer)
                    RawPrinterHelper.SendBytesToPrinter(printer, DrawerKick);

                return Request.CreateResponse(HttpStatusCode.OK,
                    new { success      = true,
                          sale_id      = req.SaleId,
                          printer,
                          copies,
                          invoice_type = cs.InvoiceType,
                          mode         = "rdlc" });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message, detail = ex.ToString() });
            }
        }
    }

    public class PrintRequest
    {
        [Newtonsoft.Json.JsonProperty("sale_id")]
        public int    SaleId      { get; set; }

        // Optional: overrides tblComputerList.InvoicePrinterName for this job.
        [Newtonsoft.Json.JsonProperty("printer_name")]
        public string PrinterName { get; set; }

        [Newtonsoft.Json.JsonProperty("copies")]
        public int    Copies      { get; set; }     // optional, defaults to 1, max 5

        [Newtonsoft.Json.JsonProperty("is_duplicate")]
        public bool   IsDuplicate { get; set; }     // true → "Duplicate" header on receipt

        [Newtonsoft.Json.JsonProperty("is_secondary")]
        public bool   IsSecondary { get; set; }     // true → use Sec_* columns from tblComputerList

        // Set once in the Device Setup screen so Web.config never needs manual editing.
        [Newtonsoft.Json.JsonProperty("reports_path")]
        public string ReportsPath { get; set; }

        // Null = default (true). Set false for kitchen/label printers with no cash drawer.
        [Newtonsoft.Json.JsonProperty("open_drawer")]
        public bool?  OpenDrawer  { get; set; }
    }
}
