using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using CandelaPOS.Infrastructure;
using DAL;
using Model;

namespace CandelaPOS.Controllers
{
    [RoutePrefix("api/pos")]
    public class PosController : ApiController
    {
        // GET api/pos/cash-status
        // Returns current-shift cash breakdown for this POS terminal:
        //   opening (last closed shift ClosingCash)
        // + received (Type='Received' detail rows in open shift)
        // + cash_sales (tblSales.Cash_amt since shift OpeningTime + gift card cash if enabled)
        // - skimmed (Type='Skimmed' detail rows in open shift)
        // = available_cash
        // Mirrors the frmSkimCashPopUp.SetPosInfo() calculation.
        [HttpGet, Route("cash-status")]
        public HttpResponseMessage GetCashStatus()
        {
            CandelaBootstrap.PrepareRequest();

            int    shopId  = (int)   Request.Properties["shop_id"];
            string posCode = (string)Request.Properties["pos_code"];

            try
            {
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();

                    // 1. Opening cash — ClosingCash of last closed shift for this POS
                    const string openingSql = @"
SELECT isnull(ClosingCash, 0)
FROM   tblPOSCashManagement
WHERE  IsClosed = 1
  AND  POSCode  = @pos
  AND  ShopID   = @sid
  AND  POSDate  = (
        SELECT MAX(POSDate)
        FROM   tblPOSCashManagement
        WHERE  IsClosed = 1 AND POSCode = @pos AND ShopID = @sid
  )";
                    var openCmd = new SqlCommand(openingSql, con);
                    openCmd.Parameters.AddWithValue("@pos", posCode);
                    openCmd.Parameters.AddWithValue("@sid", shopId);
                    var openObj = openCmd.ExecuteScalar();
                    double opening = openObj != null && openObj != DBNull.Value ? Convert.ToDouble(openObj) : 0;

                    // 2. Current open shift row (OpeningTime needed for cash-sales filter)
                    const string shiftSql = @"
SELECT TOP 1
    m.POSCashManagementID,
    m.OpeningTime,
    m.POSDate,
    isnull(SUM(CASE WHEN d.Type = 'Received' THEN d.Amount ELSE 0 END), 0) AS cash_received,
    isnull(SUM(CASE WHEN d.Type = 'Skimmed'  THEN d.Amount ELSE 0 END), 0) AS cash_skimmed
FROM tblPOSCashManagement m
LEFT JOIN tblPOSCashManagementDetail d
    ON d.POSCashManagementID = m.POSCashManagementID AND d.ShopId = m.ShopID
WHERE m.IsClosed = 0
  AND m.POSCode  = @pos
  AND m.ShopID   = @sid
GROUP BY m.POSCashManagementID, m.OpeningTime, m.POSDate";

                    double   cashReceived  = 0;
                    double   cashSkimmed   = 0;
                    DateTime openingTime   = DateTime.Now;
                    DateTime posDate       = DateTime.Now;
                    bool     shiftOpen     = false;

                    var shiftCmd = new SqlCommand(shiftSql, con);
                    shiftCmd.Parameters.AddWithValue("@pos", posCode);
                    shiftCmd.Parameters.AddWithValue("@sid", shopId);
                    using (var rdr = shiftCmd.ExecuteReader())
                    {
                        if (rdr.Read())
                        {
                            shiftOpen    = true;
                            openingTime  = Convert.ToDateTime(rdr["OpeningTime"]);
                            posDate      = Convert.ToDateTime(rdr["POSDate"]);
                            cashReceived = Convert.ToDouble(rdr["cash_received"]);
                            cashSkimmed  = Convert.ToDouble(rdr["cash_skimmed"]);
                        }
                    }

                    // 3. Cash sales since shift opening (tblSales.Cash_amt)
                    //    + gift card cash payments from tblGiftCardLedger if Activate_Gift_Card = TRUE
                    double cashSales = 0;
                    if (shiftOpen)
                    {
                        string giftCardOn = Utility.Utility.GetSystemConfigurationValue("Activate_Gift_Card") ?? "";
                        string salesSql;
                        if (giftCardOn.ToUpper() == "TRUE")
                        {
                            salesSql = @"
SELECT isnull(SUM(Cash_amt), 0)
FROM   tblSales
WHERE  pos_code = @pos AND shop_id = @sid
  AND  sale_date BETWEEN @from AND @to
UNION ALL
SELECT isnull(SUM(Cash_amount), 0)
FROM   tblGiftCardLedger
WHERE  pos_code = @pos AND sale_shop_id = @sid
  AND  cash_amount > 0
  AND  sale_date BETWEEN @from AND @to";
                        }
                        else
                        {
                            salesSql = @"
SELECT isnull(SUM(Cash_amt), 0)
FROM   tblSales
WHERE  pos_code = @pos AND shop_id = @sid
  AND  sale_date BETWEEN @from AND @to";
                        }

                        var salesCmd = new SqlCommand(salesSql, con);
                        salesCmd.Parameters.AddWithValue("@pos",  posCode);
                        salesCmd.Parameters.AddWithValue("@sid",  shopId);
                        salesCmd.Parameters.AddWithValue("@from", openingTime.ToString("yyyy-MM-dd HH:mm:ss"));
                        salesCmd.Parameters.AddWithValue("@to",   DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                        using (var dt = new DataTable())
                        {
                            new SqlDataAdapter(salesCmd).Fill(dt);
                            foreach (DataRow row in dt.Rows)
                                cashSales += row[0] != DBNull.Value ? Convert.ToDouble(row[0]) : 0;
                        }
                    }

                    double availableCash = opening + cashReceived + cashSales - cashSkimmed;

                    return Request.CreateResponse(HttpStatusCode.OK, new
                    {
                        success        = true,
                        shift_open     = shiftOpen,
                        opening        = opening,
                        cash_received  = cashReceived,
                        cash_sales     = cashSales,
                        cash_skimmed   = cashSkimmed,
                        available_cash = availableCash,
                        shift_since    = shiftOpen ? openingTime.ToString("yyyy-MM-dd HH:mm") : null,
                        pos_code       = posCode,
                        shop_id        = shopId
                    });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // POST api/pos/cash-skim
        // Records a mid-shift cash removal from the till drawer.
        // Mirrors frmSkimCashPopUp.btnOK_Click → POSCashManagmentDAL.ReceiveSkimCash()
        // with Detail.Type = Skimmed.
        //
        // The DAL handles shift-record management automatically:
        //   - CloseShift() finds the open tblPOSCashManagement row for this POS/shop,
        //     or creates one if none exists yet.
        //   - Inserts into tblPOSCashManagementDetail (Type='Skimmed').
        //   - Increments tblPOSCashManagement.CashSkimmed.
        [HttpPost, Route("cash-skim")]
        public HttpResponseMessage CashSkim([FromBody] CashSkimRequest req)
        {
            if (req == null || req.Amount <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "amount is required and must be > 0" });

            CandelaBootstrap.PrepareRequest();

            int    userId  = (int)   Request.Properties["user_id"];
            int    shopId  = (int)   Request.Properties["shop_id"];
            string posCode = (string)Request.Properties["pos_code"];

            try
            {
                var now = DateTime.Now;

                var log = new ActivityLog
                {
                    ScreenTitle = "POS Cash Skim",
                    ShopID      = shopId,
                    UserID      = userId
                };

                var detail = new POSCashManagmentDetail
                {
                    Amount                   = (double)req.Amount,
                    DetailDate               = now,
                    Notes                    = req.Notes ?? string.Empty,
                    POSCashManagementID      = 0,
                    POSCashManagementDetailID = 0,
                    ShopClosingID            = 0,
                    ShopId                   = shopId,
                    Type                     = POSCashManagmentDetailType.POSCashTypes.Skimmed,
                    ActivityLog              = log
                };

                var model = new POSCashManagment
                {
                    PosCashManagementID = 0,
                    POSCode             = posCode,
                    POSDate             = now,
                    ShopID              = shopId,
                    ShopClosingID       = 0,   // set by ReceiveSkimCash → CloseShift
                    IsClosed            = false,
                    CashCounted         = 0,
                    CashSubmitted       = 0,
                    ClosingCash         = 0,
                    Notes               = string.Empty,
                    Opening             = 0,
                    ExchangeRate        = 1,
                    UserID              = userId,
                    ActivityLog         = log,
                    Detail              = detail
                };

                new POSCashManagmentDAL().ReceiveSkimCash(model);

                return Request.CreateResponse(HttpStatusCode.OK,
                    new { success  = true,
                          amount   = req.Amount,
                          notes    = req.Notes ?? string.Empty,
                          pos_code = posCode,
                          shop_id  = shopId,
                          skimmed_at = now.ToString("yyyy-MM-dd HH:mm:ss") });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }
    }

    public class CashSkimRequest
    {
        public decimal Amount { get; set; }
        public string  Notes  { get; set; } // optional
    }
}
