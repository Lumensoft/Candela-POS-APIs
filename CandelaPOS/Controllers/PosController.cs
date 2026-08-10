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
using Newtonsoft.Json;

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
                          skimmed_at = now.ToString("yyyy-MM-dd HH:mm:ss"),
                          pos_cash_management_id = model.PosCashManagementID });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // ══ Shift / Till Cash Flow Management (mirrors frmPOSCashManagement.vb) ══

        // GET api/pos/shift-status
        // Same breakdown as cash-status, plus IsClosed history so the UI can show
        // "no shift open" / "Open Till" vs the live drawer summary.
        [HttpGet, Route("shift-status")]
        public HttpResponseMessage GetShiftStatus()
        {
            CandelaBootstrap.PrepareRequest();

            int    shopId  = (int)   Request.Properties["shop_id"];
            string posCode = (string)Request.Properties["pos_code"];

            try
            {
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var bd = ComputeShiftBreakdown(con, shopId, posCode);

                    double availableCash = bd.Opening + bd.CashReceived + bd.CashSales - bd.CashSkimmed;

                    object lastClosed = bd.ShiftOpen ? null : GetLastClosedShiftSummary(con, shopId, posCode);

                    return Request.CreateResponse(HttpStatusCode.OK, new
                    {
                        success        = true,
                        shift_open     = bd.ShiftOpen,
                        opening        = bd.Opening,
                        cash_received  = bd.CashReceived,
                        cash_sales     = bd.CashSales,
                        net_sales      = bd.CashSales,
                        cash_skimmed   = bd.CashSkimmed,
                        available_cash = availableCash,
                        shift_since    = bd.OpeningTime.HasValue ? bd.OpeningTime.Value.ToString("yyyy-MM-dd HH:mm") : null,
                        pos_code       = posCode,
                        shop_id        = shopId,
                        last_closed    = lastClosed,
                        pos_cash_management_id = bd.ShiftOpen ? (int?)bd.PosCashManagementId : null
                    });
                }
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // GET api/pos/shifts?from=&to=&page=&page_size=
        // Paginated shift-closing search for the Shift Search modal — mirrors
        // frmPOSCashManagement's Search tab. Scoped to the current shop_id + pos_code
        // (till-level, matching the rest of this feature) — there is no shop/POS picker
        // anywhere else in this app. Also returns unpaginated totals for the filtered
        // set, matching the legacy grid's summary footer row.
        [HttpGet, Route("shifts")]
        public HttpResponseMessage SearchShifts(
            [FromUri] string from      = null,
            [FromUri] string to        = null,
            [FromUri] int    page      = 1,
            [FromUri] int    page_size = 20)
        {
            CandelaBootstrap.PrepareRequest();

            int    shopId  = (int)   Request.Properties["shop_id"];
            string posCode = (string)Request.Properties["pos_code"];

            if (page      < 1) page      = 1;
            if (page_size < 1 || page_size > 200) page_size = 20;

            try
            {
                const string baseSql = @"
SELECT
    m.POSCashManagementID AS closing_id,
    m.POSCode             AS pos_code,
    m.POSDate             AS closing_date,
    isnull(m.CashCounted, 0)   AS cash_counted,
    isnull(m.CashSubmitted, 0) AS cash_submitted,
    isnull(m.ClosingCash, 0)   AS closing_cash,
    m.IsClosed                 AS is_closed,
    isnull(m.NetSales, 0)      AS net_sales,
    isnull(m.Opening, 0)       AS opening,
    m.OpeningTime               AS opening_time,
    isnull(m.CashSkimmed, 0)   AS cash_skimmed,
    isnull(m.CashReceived, 0)  AS cash_received,
    isnull(m.CashCounted, 0) - (isnull(m.Opening,0) + isnull(m.CashReceived,0) + isnull(m.NetSales,0) - isnull(m.CashSkimmed,0)) AS cash_difference,
    isnull(u.User_name, '')    AS user_name
FROM tblPOSCashManagement m
LEFT JOIN tblSecurityUser u ON u.user_id = m.UserID
WHERE m.ShopID = @sid AND m.POSCode = @pos";

                var where = new System.Text.StringBuilder(baseSql);
                var cmd   = new SqlCommand();
                cmd.Parameters.AddWithValue("@sid", shopId);
                cmd.Parameters.AddWithValue("@pos", posCode);

                if (!string.IsNullOrEmpty(from) && DateTime.TryParse(from, out DateTime fromDt))
                {
                    where.Append(" AND m.POSDate >= @fromDt");
                    cmd.Parameters.AddWithValue("@fromDt", fromDt.Date);
                }
                if (!string.IsNullOrEmpty(to) && DateTime.TryParse(to, out DateTime toDt))
                {
                    where.Append(" AND m.POSDate < @toDt");
                    cmd.Parameters.AddWithValue("@toDt", toDt.Date.AddDays(1));
                }

                int offset = (page - 1) * page_size;
                string finalSql = where.ToString()
                    + " ORDER BY m.POSCashManagementID DESC"
                    + $" OFFSET {offset} ROWS FETCH NEXT {page_size} ROWS ONLY";

                string countSql = "SELECT COUNT(*) FROM (" + where.ToString() + ") AS cnt";

                string totalsSql = @"
SELECT
    isnull(SUM(cash_counted), 0)    AS cash_counted,
    isnull(SUM(cash_submitted), 0)  AS cash_submitted,
    isnull(SUM(closing_cash), 0)    AS closing_cash,
    isnull(SUM(net_sales), 0)       AS net_sales,
    isnull(SUM(opening), 0)         AS opening,
    isnull(SUM(cash_skimmed), 0)    AS cash_skimmed,
    isnull(SUM(cash_received), 0)   AS cash_received,
    isnull(SUM(cash_difference), 0) AS cash_difference
FROM (" + where.ToString() + ") totals_src";

                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    cmd.Connection = con;

                    var countCmd = cmd.Clone() as SqlCommand;
                    countCmd.CommandText = countSql;
                    int total = Convert.ToInt32(countCmd.ExecuteScalar());

                    var totalsCmd = cmd.Clone() as SqlCommand;
                    totalsCmd.CommandText = totalsSql;
                    object totals;
                    using (var rdr = totalsCmd.ExecuteReader())
                    {
                        rdr.Read();
                        totals = new
                        {
                            cash_counted    = Convert.ToDouble(rdr["cash_counted"]),
                            cash_submitted  = Convert.ToDouble(rdr["cash_submitted"]),
                            closing_cash    = Convert.ToDouble(rdr["closing_cash"]),
                            net_sales       = Convert.ToDouble(rdr["net_sales"]),
                            opening         = Convert.ToDouble(rdr["opening"]),
                            cash_skimmed    = Convert.ToDouble(rdr["cash_skimmed"]),
                            cash_received   = Convert.ToDouble(rdr["cash_received"]),
                            cash_difference = Convert.ToDouble(rdr["cash_difference"]),
                        };
                    }

                    cmd.CommandText = finalSql;
                    var list = new List<Dictionary<string, object>>();
                    using (var dt = new DataTable())
                    {
                        new SqlDataAdapter(cmd).Fill(dt);
                        foreach (DataRow row in dt.Rows)
                        {
                            var dict = new Dictionary<string, object>();
                            foreach (DataColumn col in dt.Columns)
                                dict[col.ColumnName] = row[col] == DBNull.Value ? null : row[col];
                            list.Add(dict);
                        }
                    }

                    return Request.CreateResponse(HttpStatusCode.OK, new
                    {
                        success   = true,
                        total,
                        page,
                        page_size,
                        count     = list.Count,
                        data      = list,
                        totals
                    });
                }
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // GET api/pos/shifts/{closingId}/detail
        // Per-transaction breakdown for a single shift closing row from SearchShifts() above -
        // the individual Cash Received / Cash Skimmed events (tblPOSCashManagementDetail rows)
        // that make up that closing's aggregate cash_received/cash_skimmed totals. Mirrors
        // frmPOSCashManagement's expandable "Cash Received" / "Cash Skimmed" sub-grids.
        [HttpGet, Route("shifts/{closingId:int}/detail")]
        public HttpResponseMessage GetShiftDetail(int closingId)
        {
            CandelaBootstrap.PrepareRequest();

            int    shopId  = (int)   Request.Properties["shop_id"];
            string posCode = (string)Request.Properties["pos_code"];

            try
            {
                const string sql = @"
SELECT d.Amount, d.DetailDate, d.Notes, d.Type
FROM tblPOSCashManagement m
INNER JOIN tblPOSCashManagementDetail d
    ON d.POSCashManagementID = m.POSCashManagementID AND d.ShopId = m.ShopID
WHERE m.POSCashManagementID = @closingId AND m.ShopID = @sid AND m.POSCode = @pos
ORDER BY d.DetailDate";

                var received = new List<object>();
                var skimmed  = new List<object>();

                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                using (var cmd = new SqlCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@closingId", closingId);
                    cmd.Parameters.AddWithValue("@sid", shopId);
                    cmd.Parameters.AddWithValue("@pos", posCode);
                    con.Open();

                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            var row = new
                            {
                                amount      = Convert.ToDouble(rdr["Amount"]),
                                detail_date = Convert.ToDateTime(rdr["DetailDate"]).ToString("yyyy-MM-dd HH:mm:ss"),
                                notes       = rdr["Notes"] == DBNull.Value ? "" : rdr["Notes"].ToString(),
                            };

                            string type = rdr["Type"].ToString();
                            if (type == "Received") received.Add(row);
                            else if (type == "Skimmed") skimmed.Add(row);
                        }
                    }
                }

                return Request.CreateResponse(HttpStatusCode.OK, new
                {
                    success    = true,
                    closing_id = closingId,
                    received,
                    skimmed
                });
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // POST api/pos/shift-open
        // Explicitly opens a till shift. Idempotent — if a shift is already open for
        // this POS/shop, just returns its opening cash instead of erroring.
        // Mirrors what CloseShift() already does implicitly on the first Received/
        // Skimmed cash movement, exposed here as its own user-facing action.
        [HttpPost, Route("shift-open")]
        public HttpResponseMessage OpenShift()
        {
            CandelaBootstrap.PrepareRequest();

            int    userId  = (int)   Request.Properties["user_id"];
            int    shopId  = (int)   Request.Properties["shop_id"];
            string posCode = (string)Request.Properties["pos_code"];

            try
            {
                var dal = new POSCashManagmentDAL();

                if (dal.IsPOSOpened(posCode, shopId))
                {
                    return Request.CreateResponse(HttpStatusCode.OK, new
                    {
                        success      = true,
                        already_open = true,
                        opening      = dal.GetPOSOpeningCash(posCode, shopId),
                        pos_code     = posCode,
                        shop_id      = shopId
                    });
                }

                var now     = DateTime.Now;
                double opening = dal.GetPOSOpeningCash(posCode, shopId);

                var model = new POSCashManagment
                {
                    PosCashManagementID = 0,
                    ShopID              = shopId,
                    ShopClosingID       = 0,
                    POSCode             = posCode,
                    POSDate             = now,
                    IsClosed            = false,
                    Opening             = opening,
                    CashCounted         = 0,
                    CashSubmitted       = 0,
                    ClosingCash         = 0,
                    Notes               = string.Empty,
                    UserID              = userId,
                    ExchangeRate        = 1,
                    ActivityLog         = new ActivityLog { ScreenTitle = "POS Till Open", ShopID = shopId, UserID = userId }
                };

                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var trans = con.BeginTransaction();
                    try
                    {
                        dal.CloseShift(model, trans);
                        trans.Commit();
                    }
                    catch
                    {
                        trans.Rollback();
                        throw;
                    }
                }

                return Request.CreateResponse(HttpStatusCode.OK, new
                {
                    success      = true,
                    already_open = false,
                    opened_at    = now.ToString("yyyy-MM-dd HH:mm:ss"),
                    opening,
                    pos_code     = posCode,
                    shop_id      = shopId
                });
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // POST api/pos/cash-receive
        // Records a mid-shift cash addition to the till drawer (float top-up from shop safe).
        // Mirrors CashSkim() above exactly, with Detail.Type = Received.
        [HttpPost, Route("cash-receive")]
        public HttpResponseMessage ReceiveCash([FromBody] CashReceiveRequest req)
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
                    ScreenTitle = "POS Cash Receive",
                    ShopID      = shopId,
                    UserID      = userId
                };

                var detail = new POSCashManagmentDetail
                {
                    Amount                    = (double)req.Amount,
                    DetailDate                = now,
                    Notes                     = req.Notes ?? string.Empty,
                    POSCashManagementID       = 0,
                    POSCashManagementDetailID = 0,
                    ShopClosingID             = 0,
                    ShopId                    = shopId,
                    Type                      = POSCashManagmentDetailType.POSCashTypes.Received,
                    ActivityLog               = log
                };

                var model = new POSCashManagment
                {
                    PosCashManagementID = 0,
                    POSCode             = posCode,
                    POSDate             = now,
                    ShopID              = shopId,
                    ShopClosingID       = 0,
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
                    new { success     = true,
                          amount      = req.Amount,
                          notes       = req.Notes ?? string.Empty,
                          pos_code    = posCode,
                          shop_id     = shopId,
                          received_at = now.ToString("yyyy-MM-dd HH:mm:ss") });
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // POST api/pos/shift-close
        // Closes the currently open till shift: records physical cash counted (optionally
        // broken down by denomination), cash submitted, and computes CashDifference via
        // POSCashManagmentDAL.UpdateShiftClosing — exactly the frmPOSCashManagement.vb
        // "Cash / Shift Closing" formula: CashCounted - (Opening + CashReceived + NetSales - CashSkimmed).
        [HttpPost, Route("shift-close")]
        public HttpResponseMessage CloseShift([FromBody] ShiftCloseRequest req)
        {
            if (req == null || req.CashCounted < 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "cash_counted is required and must be >= 0" });

            CandelaBootstrap.PrepareRequest();

            int    userId  = (int)   Request.Properties["user_id"];
            int    shopId  = (int)   Request.Properties["shop_id"];
            string posCode = (string)Request.Properties["pos_code"];

            try
            {
                ShiftBreakdown bd;
                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    bd = ComputeShiftBreakdown(con, shopId, posCode);
                }

                if (!bd.ShiftOpen)
                    return Request.CreateResponse((HttpStatusCode)422,
                        new { error = "No open shift to close for this till." });

                var now           = req.ClosingDate ?? DateTime.Now;
                double cashSubmitted = req.CashSubmitted;
                double closingCash   = req.CashCounted - cashSubmitted;

                var model = new POSCashManagment
                {
                    PosCashManagementID = bd.PosCashManagementId,
                    ShopID              = shopId,
                    POSCode             = posCode,
                    POSDate             = now,
                    Opening             = bd.Opening,
                    CashReceived        = bd.CashReceived,
                    CashSkimmed         = bd.CashSkimmed,
                    NetSales            = bd.CashSales,
                    CashCounted         = req.CashCounted,
                    CashSubmitted       = cashSubmitted,
                    ClosingCash         = closingCash,
                    Notes               = req.Notes ?? string.Empty,
                    IsClosed            = true,
                    UserID              = userId,
                    ExchangeRate        = 1,
                    ActivityLog         = new ActivityLog { ScreenTitle = "POS Shift Close", ShopID = shopId, UserID = userId }
                };

                new POSCashManagmentDAL().UpdateShiftClosing(model);

                if (req.Denominations != null)
                    InsertShiftCashCount(model.PosCashManagementID, shopId, posCode, req.Denominations, now);

                return Request.CreateResponse(HttpStatusCode.OK, new
                {
                    success         = true,
                    closed_at       = now.ToString("yyyy-MM-dd HH:mm:ss"),
                    cash_counted    = model.CashCounted,
                    cash_submitted  = model.CashSubmitted,
                    closing_cash    = model.ClosingCash,
                    net_sales       = model.NetSales,
                    opening         = model.Opening,
                    cash_received   = model.CashReceived,
                    cash_skimmed    = model.CashSkimmed,
                    cash_difference = model.CashDifference,
                    pos_code        = posCode,
                    shop_id         = shopId
                });
            }
            catch (Exception)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "An internal error occurred." });
            }
        }

        // ── Shared helpers ──────────────────────────────────────────────────

        private class ShiftBreakdown
        {
            public bool     ShiftOpen;
            public int      PosCashManagementId;
            public double   Opening;
            public double   CashReceived;
            public double   CashSkimmed;
            public double   CashSales;
            public DateTime? OpeningTime;
        }

        // Same calculation as GetCashStatus() above, factored out so shift-status and
        // shift-close always agree on "system cash" (the CashDifference reconciliation
        // is only meaningful if both read it identically).
        private ShiftBreakdown ComputeShiftBreakdown(SqlConnection con, int shopId, string posCode)
        {
            var bd = new ShiftBreakdown();

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
            bd.Opening = openObj != null && openObj != DBNull.Value ? Convert.ToDouble(openObj) : 0;

            const string shiftSql = @"
SELECT TOP 1
    m.POSCashManagementID,
    m.OpeningTime,
    isnull(SUM(CASE WHEN d.Type = 'Received' THEN d.Amount ELSE 0 END), 0) AS cash_received,
    isnull(SUM(CASE WHEN d.Type = 'Skimmed'  THEN d.Amount ELSE 0 END), 0) AS cash_skimmed
FROM tblPOSCashManagement m
LEFT JOIN tblPOSCashManagementDetail d
    ON d.POSCashManagementID = m.POSCashManagementID AND d.ShopId = m.ShopID
WHERE m.IsClosed = 0
  AND m.POSCode  = @pos
  AND m.ShopID   = @sid
GROUP BY m.POSCashManagementID, m.OpeningTime";

            var shiftCmd = new SqlCommand(shiftSql, con);
            shiftCmd.Parameters.AddWithValue("@pos", posCode);
            shiftCmd.Parameters.AddWithValue("@sid", shopId);
            using (var rdr = shiftCmd.ExecuteReader())
            {
                if (rdr.Read())
                {
                    bd.ShiftOpen           = true;
                    bd.PosCashManagementId = Convert.ToInt32(rdr["POSCashManagementID"]);
                    bd.OpeningTime         = Convert.ToDateTime(rdr["OpeningTime"]);
                    bd.CashReceived        = Convert.ToDouble(rdr["cash_received"]);
                    bd.CashSkimmed         = Convert.ToDouble(rdr["cash_skimmed"]);
                }
            }

            if (bd.ShiftOpen)
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
                salesCmd.Parameters.AddWithValue("@from", bd.OpeningTime.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                salesCmd.Parameters.AddWithValue("@to",   DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                using (var dt = new DataTable())
                {
                    new SqlDataAdapter(salesCmd).Fill(dt);
                    foreach (DataRow row in dt.Rows)
                        bd.CashSales += row[0] != DBNull.Value ? Convert.ToDouble(row[0]) : 0;
                }
            }

            return bd;
        }

        private object GetLastClosedShiftSummary(SqlConnection con, int shopId, string posCode)
        {
            const string sql = @"
SELECT TOP 1 POSCashManagementID, POSDate, CashCounted, CashDifference
FROM   tblPOSCashManagement
WHERE  IsClosed = 1 AND POSCode = @pos AND ShopID = @sid
ORDER BY POSDate DESC";

            var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@pos", posCode);
            cmd.Parameters.AddWithValue("@sid", shopId);

            using (var rdr = cmd.ExecuteReader())
            {
                if (!rdr.Read()) return null;
                return new
                {
                    pos_cash_management_id = Convert.ToInt32(rdr["POSCashManagementID"]),
                    closed_at       = Convert.ToDateTime(rdr["POSDate"]).ToString("yyyy-MM-dd HH:mm:ss"),
                    cash_counted    = Convert.ToDouble(rdr["CashCounted"]),
                    cash_difference = Convert.ToDouble(rdr["CashDifference"])
                };
            }
        }

        private void InsertShiftCashCount(int posCashManagementId, int shopId, string posCode, DenominationsDto d, DateTime countedAt)
        {
            using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
            {
                con.Open();
                const string sql = @"
INSERT INTO tblPOSShiftCashCount
    (POSCashManagementID, ShopID, POSCode, Denom_5000, Denom_1000, Denom_500, Denom_100,
     Denom_50, Denom_20, Denom_10, Denom_5, Denom_Other_Amount, TotalCounted, CountedAt)
VALUES
    (@posCashId, @sid, @pos, @d5000, @d1000, @d500, @d100, @d50, @d20, @d10, @d5, @dother, @total, @countedAt)";

                double total = 5000d * d.D5000 + 1000d * d.D1000 + 500d * d.D500 + 100d * d.D100
                             + 50d * d.D50 + 20d * d.D20 + 10d * d.D10 + 5d * d.D5 + d.Other;

                var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@posCashId", posCashManagementId);
                cmd.Parameters.AddWithValue("@sid",       shopId);
                cmd.Parameters.AddWithValue("@pos",       posCode);
                cmd.Parameters.AddWithValue("@d5000",     d.D5000);
                cmd.Parameters.AddWithValue("@d1000",     d.D1000);
                cmd.Parameters.AddWithValue("@d500",      d.D500);
                cmd.Parameters.AddWithValue("@d100",      d.D100);
                cmd.Parameters.AddWithValue("@d50",       d.D50);
                cmd.Parameters.AddWithValue("@d20",       d.D20);
                cmd.Parameters.AddWithValue("@d10",       d.D10);
                cmd.Parameters.AddWithValue("@d5",        d.D5);
                cmd.Parameters.AddWithValue("@dother",    d.Other);
                cmd.Parameters.AddWithValue("@total",     total);
                cmd.Parameters.AddWithValue("@countedAt", countedAt);
                cmd.ExecuteNonQuery();
            }
        }
    }

    public class CashSkimRequest
    {
        public decimal Amount { get; set; }
        public string  Notes  { get; set; } // optional
    }

    public class CashReceiveRequest
    {
        public decimal Amount { get; set; }
        public string  Notes  { get; set; } // optional
    }

    public class ShiftCloseRequest
    {
        [JsonProperty("cash_counted")]
        public double CashCounted { get; set; }

        [JsonProperty("cash_submitted")]
        public double CashSubmitted { get; set; }

        public string Notes { get; set; }

        // Closing date/time as picked/generated on the frontend - used as the shift's closing
        // timestamp instead of the server's own clock. Falls back to DateTime.Now if omitted.
        [JsonProperty("closing_date")]
        public DateTime? ClosingDate { get; set; }

        public DenominationsDto Denominations { get; set; } // optional detailed count
    }

    public class DenominationsDto
    {
        public int    D5000 { get; set; }
        public int    D1000 { get; set; }
        public int    D500  { get; set; }
        public int    D100  { get; set; }
        public int    D50   { get; set; }
        public int    D20   { get; set; }
        public int    D10   { get; set; }
        public int    D5    { get; set; }
        public double Other { get; set; }
    }
}
