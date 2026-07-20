using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Xml.Linq;
using Microsoft.Reporting.WinForms;

namespace CandelaPOS.Infrastructure
{
    // Renders Candela RDLC sales invoices and prints via the Windows print spooler,
    // mirroring SQLReportUtils.vb so the output is identical to what Candela produces.
    internal static class RdlcInvoicePrinter
    {
        internal sealed class ComputerSettings
        {
            public string InvoiceType        { get; set; }
            public string InvoicePrinterName { get; set; }
            public double PageWidth          { get; set; }   // inches; 0 → default 3.2
            public double PageHeight         { get; set; }   // inches; 0 → default 11
            public double TopMargin          { get; set; }
            public double BottomMargin       { get; set; }
            public double LeftMargin         { get; set; }
            public double RightMargin        { get; set; }
        }

        internal sealed class ReportInfo
        {
            public string RdlcPath           { get; set; }  // absolute file path
            public string DataSourceName     { get; set; }  // RDLC dataset name
            public string StoredProc         { get; set; }  // spSalesInvoiceNew or spSalesInvoiceNewLC
            public bool   NeedsTenderSummary { get; set; }  // also fetch SPSaleTenderSummary
        }

        // Reads primary or secondary invoice settings from tblComputerList for this POS terminal.
        // posCode maps to Computer_Name; isSecondary reads Sec_* columns.
        internal static bool TryGetComputerSettings(string posCode, string connStr,
                                                    bool isSecondary, out ComputerSettings settings)
        {
            settings = null;
            const string sql = @"
SELECT InvoiceType, InvoicePrinterName, InvoiceHeight, InvoiceWidth,
       TopMargin, BottomMargin, LeftMargin, RightMargin,
       Sec_InvoiceType, Sec_InvoicePrinterName, Sec_InvoiceHeight, Sec_InvoiceWidth,
       Sec_TopMargin, Sec_BottomMargin, Sec_LeftMargin, Sec_RightMargin
FROM tblComputerList WHERE POS_code = @posCode";

            using (var conn = new SqlConnection(connStr))
            using (var cmd  = new SqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@posCode", posCode);
                conn.Open();
                using (var rdr = cmd.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (!rdr.Read()) return false;

                    string p = isSecondary ? "Sec_" : "";
                    settings = new ComputerSettings
                    {
                        InvoiceType        = Str(rdr[p + "InvoiceType"]),
                        InvoicePrinterName = Str(rdr[p + "InvoicePrinterName"]),
                        PageHeight         = Dbl(rdr[p + "InvoiceHeight"]),
                        PageWidth          = Dbl(rdr[p + "InvoiceWidth"]),
                        TopMargin          = Dbl(rdr[p + "TopMargin"]),
                        BottomMargin       = Dbl(rdr[p + "BottomMargin"]),
                        LeftMargin         = Dbl(rdr[p + "LeftMargin"]),
                        RightMargin        = Dbl(rdr[p + "RightMargin"]),
                    };
                    if (settings.PageHeight <= 0) settings.PageHeight = 11.0;
                    if (settings.PageWidth  <= 0) settings.PageWidth  = 3.2;
                    return !string.IsNullOrEmpty(settings.InvoiceType);
                }
            }
        }

        // Maps InvoiceType string → RDLC file path and datasource config.
        // basePath = value of CandelaReportPath app setting
        // (e.g. D:\candela\5990\CandelaRMS\Candela\Reports\)
        internal static ReportInfo GetReportInfo(string invoiceType, string basePath)
        {
            if (!basePath.EndsWith("\\") && !basePath.EndsWith("/"))
                basePath += "\\";

            switch (invoiceType)
            {
                // SR-01
                case "SR-01 Custom Receipt":
                    return Std(basePath, @"Sales Reports\rptSaleInvoice.rdlc");

                // SR-03
                case "SR-03 Small (3 Inch)":
                case "SR-03 Small (3 Inch) (FBR)":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptInvoice3Inch.rdlc");
                case "SR-03 A 3 inch (With full Product Name)":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptInvoice3Inch (With full Product Name).rdlc");

                // SR-04
                case "SR-04 Small (3 Inch) with Price and Qty":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptInvoice3InchWithPriceQty.rdlc");
                case "SR-04 Small (3 Inch) with Price and Qty LC":
                    return LC(basePath, @"Loyalty Club Reports\rptInvoice3InchWithPriceQtyLC.rdlc");
                case "SR-04 A 3 inch with Price and Qty (With full Product Name)":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptInvoice3InchWithPriceQty (With full Product Name).rdlc");
                case "SR-04 A 3 inch with Price and Qty LC (With full Product Name)":
                    return LC(basePath, @"Loyalty Club Reports\rptInvoice3InchWithPriceQtyLC (With full Product Name).rdlc");

                // SR-05
                case "SR-05 Small (3 Inch) with Price and Qty (Tax %)":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptInvoice3InchWithPriceQtyTax.rdlc");
                case "SR-05 A 3 inch with Price and Qty (Tax%) (With full Product Name)":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptInvoice3InchWithPriceQtyTax (With full Product Name).rdlc");

                // SR-06 — uses SPSaleTenderSummary as second datasource
                case "SR-06 Small (3 inch) with Product Discount":
                    return StdTender(basePath, @"Sales Reports\Sales Invoices\rptinvoice3inchwithdiscount.rdlc");
                case "SR-06 A 3 inch with Product Discount (With full Product Name)":
                    return StdTender(basePath, @"Sales Reports\Sales Invoices\rptinvoice3inchwithdiscount (With full Product Name).rdlc");

                // SR-07
                case "SR-07 Small (3 inch) with Price and Qty-Urdu":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptSaleInvoiceUrdu.rdlc");
                case "SR-07 A 3 inch with Price and Qty-Urdu (With full Product Name)":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptSaleInvoiceUrdu (With full Product Name).rdlc");

                // SR-09 — LC variants use spSalesInvoiceNewLC + "newCandelaDataset"
                case "SR-09 3 inch Receipt with price,Qty & Tenders LC":
                    return LCTender(basePath, @"Loyalty Club Reports\rptInvoice3InchWithPriceQtyTenderLC.rdlc");
                case "SR-09 A 3 inch Receipt with price,Qty & Tenders LC (With full Product Name)":
                    return LCTender(basePath, @"Loyalty Club Reports\rptInvoice3InchWithPriceQtyTenderLC (With full Product Name).rdlc");
                // SR-09 non-LC uses "newCandelaDataset" (not _spSalesInvoiceNew) + SPSaleTenderSummary
                case "SR-09 3 inch Receipt with price,Qty & Tenders":
                    return StdDs(basePath, @"Sales Reports\Sales Invoices\3inchinvoicePriceandQtywithTenderSummary.rdlc");
                case "SR-09 A 3 inch Receipt with price,Qty and Tenders (With full Product Name)":
                    return StdDs(basePath, @"Sales Reports\Sales Invoices\3inchinvoicePriceandQtywithTenderSummary (With full Product Name).rdlc");

                // SR-10
                case "SR-10 Product Based Small (3 Inch) with Price and Qty":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptInvoice3InchWithPriceQty(Product Based).rdlc");
                case "SR-10 A Product Based 3 inch with Price and Qty (With full Product Name)":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptInvoice3InchWithPriceQty(Product Based) (With full Product Name).rdlc");

                // SR-11
                case "SR-11 Standard (5 Inch) with customer ledger":
                    return StdTender(basePath, @"Sales Reports\Sales Invoices\rptInvoice5Inch.rdlc");

                // SR-12
                case "SR-12 Standard (5 Inch) with Product Discount":
                    return StdTender(basePath, @"Sales Reports\Sales Invoices\rptInvoice5InchWithDisc.rdlc");
                case "SR-12 Standard (5 Inch) with Product Discount LC":
                    return LCTender(basePath, @"Loyalty Club Reports\rptInvoice5InchWithDisc_LC.rdlc");

                // SR-13
                case "SR-13 Standard (5 Inch) with Product Discount Percentage & Tenders (FBR) with Product Discount":
                case "SR-13 Standard (5 Inch) with Product Discount Percentage & Tenders":
                    return StdTender(basePath, @"Sales Reports\Sales Invoices\rptInvoice5InchWithDiscPercnt.rdlc");

                // SR-15
                case "SR-15 Full Page (A4) with Customer Ledger & Tenders":
                    return StdTender(basePath, @"Sales Reports\Sales Invoices\rptInvoiceA4.rdlc");

                // SR-16
                case "SR-16 Full page (A4) with Product Discount and customer ledger (FBR)":
                case "SR-16 Full page (A4) with Product Discount and customer ledger":
                    return StdTender(basePath, @"Sales Reports\Sales Invoices\rptInvoiceA4WithDisc.rdlc");

                // SR-17
                case "SR-17 Standard (A4) with Product Discount Percentage and customer ledger":
                    return StdTender(basePath, @"Sales Reports\Sales Invoices\rptInvoiceA4WithDiscPercnt.rdlc");

                // SR-18
                case "SR-18 Small (3 Inch) with Tag Price and Qty (FBR)":
                case "SR-18 Small (3 Inch) with Tag Price and Qty":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptInvoice3InchWithPriceQtyTax_WithTagPrice.rdlc");
                case "SR-18 A (3 Inch) with Tag Price and Qty (With full Product Name) (FBR)":
                case "SR-18 A (3 Inch) with Tag Price and Qty (With full Product Name)":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptInvoice3InchWithPriceQtyTax_WithTagPrice (With full Product Name).rdlc");

                // SR-19
                case "SR-19 Small (3 Inch) with Promo points LC":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptInvoice3InchWithPromoPointsLC.rdlc");
                case "SR-19 A 3 inch with Promo points LC (With full Product Name)":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptInvoice3InchWithPromoPointsLC (With full Product Name).rdlc");

                // SR-20
                case "SR-20 Standard (5 Inch) with Price and Qty-Urdu":
                    return StdTender(basePath, @"Sales Reports\Sales Invoices\rptInvoice5InchWithPriceQty-Urdu.rdlc");

                // SR-21
                case "SR-21 Small (3 Inch) with Price, Qty and without Tax":
                    return Std(basePath, @"Sales Reports\Sales Invoices\rptInvoice3InchWithPriceQty_WithTagPrice.rdlc");

                default:
                    return null;
            }
        }

        // Renders the RDLC to EMF streams then prints via PrintDocument (GDI driver).
        // printerName may be a local name or UNC path (\\server\share).
        internal static void Print(int shopId, int saleId, int mode, int copies,
                                   string printerName, ComputerSettings settings,
                                   ReportInfo info, string connStr)
        {
            DataTable invoiceData = Fetch(
                $"exec {info.StoredProc} 1,{saleId},{shopId},{mode}", connStr);

            DataTable tenderData = info.NeedsTenderSummary
                ? Fetch($"exec SPSaleTenderSummary {saleId},{shopId}", connStr)
                : null;

            var report = new LocalReport { ReportPath = info.RdlcPath, EnableExternalImages = true };
            report.DataSources.Add(new ReportDataSource(info.DataSourceName, invoiceData));
            if (tenderData != null)
                report.DataSources.Add(
                    new ReportDataSource("newCandelaDataset_SPSaleTenderSummary", tenderData));

            // Supply safe defaults for every required RDLC parameter.
            // LocalReport.GetParameters() returns empty before the first Render(), so we
            // parse the RDLC XML directly — that is always available at this point.
            SetDefaultParameters(report, info.RdlcPath);

            var streams = new List<MemoryStream>();
            report.Render("Image", DeviceInfo(settings),
                (name, ext, enc, mime, willSeek) => { var ms = new MemoryStream(); streams.Add(ms); return ms; },
                out Warning[] _);

            foreach (var ms in streams) ms.Position = 0;
            if (streams.Count == 0) return;

            int pg = 0;
            using (var doc = new PrintDocument())
            {
                doc.PrinterSettings.PrinterName = printerName;
                doc.PrinterSettings.Copies      = (short)Math.Max(1, Math.Min(copies, 5));
                doc.DefaultPageSettings.Landscape = false;
                doc.PrintPage += (s, e) =>
                {
                    using (var img = new Metafile(streams[pg]))
                    {
                        pg++;
                        e.Graphics.DrawImage(img, e.PageBounds);
                        e.HasMorePages = pg < streams.Count;
                    }
                };
                doc.Print();
            }

            foreach (var ms in streams) ms.Dispose();
        }

        // ── ReportInfo factory helpers ────────────────────────────────────────
        // Std:      spSalesInvoiceNew → "newCandelaDataset_spSalesInvoiceNew", no tender
        // StdTender: same + SPSaleTenderSummary
        // StdDs:    spSalesInvoiceNew → "newCandelaDataset" + SPSaleTenderSummary  (SR-09 non-LC)
        // LC:       spSalesInvoiceNewLC → "newCandelaDataset", no tender
        // LCTender: same + SPSaleTenderSummary

        private static ReportInfo Std(string b, string r) => new ReportInfo
        { RdlcPath = b + r, DataSourceName = "newCandelaDataset_spSalesInvoiceNew",
          StoredProc = "spSalesInvoiceNew", NeedsTenderSummary = false };

        private static ReportInfo StdTender(string b, string r) => new ReportInfo
        { RdlcPath = b + r, DataSourceName = "newCandelaDataset_spSalesInvoiceNew",
          StoredProc = "spSalesInvoiceNew", NeedsTenderSummary = true };

        private static ReportInfo StdDs(string b, string r) => new ReportInfo
        { RdlcPath = b + r, DataSourceName = "newCandelaDataset",
          StoredProc = "spSalesInvoiceNew", NeedsTenderSummary = true };

        private static ReportInfo LC(string b, string r) => new ReportInfo
        { RdlcPath = b + r, DataSourceName = "newCandelaDataset",
          StoredProc = "spSalesInvoiceNewLC", NeedsTenderSummary = false };

        private static ReportInfo LCTender(string b, string r) => new ReportInfo
        { RdlcPath = b + r, DataSourceName = "newCandelaDataset",
          StoredProc = "spSalesInvoiceNewLC", NeedsTenderSummary = true };

        // ── Helpers ──────────────────────────────────────────────────────────

        private static DataTable Fetch(string sql, string connStr)
        {
            var dt = new DataTable();
            using (var da = new SqlDataAdapter(sql, connStr))
                da.Fill(dt);
            return dt;
        }

        private static string DeviceInfo(ComputerSettings s)
        {
            double w = s.PageWidth  > 0 ? s.PageWidth  : 3.2;
            double h = s.PageHeight > 0 ? s.PageHeight : 11.0;
            return
                $"<DeviceInfo><OutputFormat>EMF</OutputFormat>" +
                $"<PageWidth>{w}in</PageWidth><PageHeight>{h}in</PageHeight>" +
                $"<MarginTop>{s.TopMargin}in</MarginTop><MarginLeft>{s.LeftMargin}in</MarginLeft>" +
                $"<MarginRight>{s.RightMargin}in</MarginRight><MarginBottom>{s.BottomMargin}in</MarginBottom>" +
                $"</DeviceInfo>";
        }

        // Sets a safe default value for every RDLC parameter that has no default.
        // RDLC throws "One or more parameters required" at Render() if any required
        // parameter (no DefaultValue, no Nullable/AllowBlank) is left unset.
        // Candela normally populates these from CustomReceiptDesignerDal; we supply
        // type-appropriate no-op defaults so the report renders with its built-in layout.
        // Reads parameter definitions straight from the RDLC XML and sets safe defaults.
        // LocalReport.GetParameters() is empty before the first Render(), so we cannot
        // use it here. Parsing the file directly is always reliable.
        //
        // Key rule: for String params with AllowBlank=false, the RDLC engine treats ""
        // the same as "no value" and still throws the "parameter required" error.
        // We use "False" for those (most are boolean-flag strings like ApplyUrduFont).
        private static void SetDefaultParameters(LocalReport report, string rdlcPath)
        {
            // No try/catch — let any file or SetParameters error propagate so the
            // outer PrintController catch returns it as a diagnostic detail.
            var doc = XDocument.Load(rdlcPath);
            XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            var prmList = new List<ReportParameter>();
            foreach (var el in doc.Descendants(ns + "ReportParameter"))
            {
                string name = el.Attribute("Name")?.Value;
                if (string.IsNullOrEmpty(name)) continue;

                // Skip params that already have a DefaultValue in the RDLC.
                if (el.Element(ns + "DefaultValue") != null) continue;

                string dataType  = el.Element(ns + "DataType")?.Value  ?? "String";
                bool   nullable  = string.Equals(el.Element(ns + "Nullable")?.Value,
                                       "true", StringComparison.OrdinalIgnoreCase);
                bool   allowBlank = string.Equals(el.Element(ns + "AllowBlank")?.Value,
                                       "true", StringComparison.OrdinalIgnoreCase);

                switch (dataType)
                {
                    case "Boolean":
                        prmList.Add(new ReportParameter(name, "False")); break;
                    case "Integer":
                        if (!nullable) prmList.Add(new ReportParameter(name, "0")); break;
                    case "Float":
                        if (!nullable) prmList.Add(new ReportParameter(name, "0")); break;
                    case "String":
                        // AllowBlank=false: empty string == "no value" to the engine.
                        prmList.Add(new ReportParameter(name, allowBlank ? "" : "False"));
                        break;
                    case "DateTime":
                        prmList.Add(new ReportParameter(name,
                            DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"))); break;
                }
            }
            if (prmList.Count > 0)
                report.SetParameters(prmList);
        }

        private static string Str(object v) =>
            v == null || v == DBNull.Value ? "" : v.ToString();

        private static double Dbl(object v)
        {
            if (v == null || v == DBNull.Value) return 0;
            return double.TryParse(v.ToString(), out double d) ? d : 0;
        }
    }
}
