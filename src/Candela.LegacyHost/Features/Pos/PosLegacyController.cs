using System;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using System.Data.SqlClient;
using DAL;
using Model;
using Newtonsoft.Json;
using CandelaPOS.Shared.Data;
using CandelaPOS.Shared.Errors;

namespace CandelaPOS.Features.Pos
{
    /// <summary>
    /// The four PosController writes that go through POSCashManagmentDAL, moved behind
    /// /legacy/pos so Candela.Api can call them on .NET Framework where the DAL runs.
    ///
    ///   POST /legacy/pos/cash-skim     ReceiveSkimCash(Type = Skimmed)
    ///   POST /legacy/pos/cash-receive  ReceiveSkimCash(Type = Received)
    ///   POST /legacy/pos/shift-open    IsPOSOpened / GetPOSOpeningCash / CloseShift
    ///   POST /legacy/pos/shift-close   UpdateShiftClosing (+ tblPOSShiftCashCount insert)
    ///
    /// The model-building is copied verbatim from PosController. shop_id / user_id /
    /// pos_code come from Request.Properties, which LegacyContextHandler fills from the
    /// X-Ctx-* headers Candela.Api attaches after it has validated the cashier's JWT —
    /// the same values the net48 endpoints read straight off the token.
    ///
    /// The reads (cash-status, shift-status, shifts, shift-detail) and the shift-detail
    /// DELETE are NOT here: Candela.Api serves those from SQL directly, exactly as the
    /// old PosController did. The old api/pos PosController is left in place, unmapped,
    /// as instant rollback.
    /// </summary>
    [RoutePrefix("legacy/pos")]
    public class PosLegacyController : ApiController
    {
        // POST /legacy/pos/cash-skim
        [HttpPost, Route("cash-skim")]
        public HttpResponseMessage CashSkim([FromBody] CashMovementBody req)
        {
            if (req == null || req.Amount <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "amount is required and must be > 0" });

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
                    Amount                    = (double)req.Amount,
                    DetailDate                = now,
                    Notes                     = req.Notes ?? string.Empty,
                    POSCashManagementID       = 0,
                    POSCashManagementDetailID = 0,
                    ShopClosingID             = 0,
                    ShopId                    = shopId,
                    Type                      = POSCashManagmentDetailType.POSCashTypes.Skimmed,
                    ActivityLog               = log
                };

                var model = BuildMovementModel(detail, log, posCode, shopId, userId, now);

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
                return ApiError.Internal(Request, ex, "PosController.CashSkim");
            }
        }

        // POST /legacy/pos/cash-receive
        [HttpPost, Route("cash-receive")]
        public HttpResponseMessage ReceiveCash([FromBody] CashMovementBody req)
        {
            if (req == null || req.Amount <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "amount is required and must be > 0" });

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

                var model = BuildMovementModel(detail, log, posCode, shopId, userId, now);

                new POSCashManagmentDAL().ReceiveSkimCash(model);

                return Request.CreateResponse(HttpStatusCode.OK,
                    new { success     = true,
                          amount      = req.Amount,
                          notes       = req.Notes ?? string.Empty,
                          pos_code    = posCode,
                          shop_id     = shopId,
                          received_at = now.ToString("yyyy-MM-dd HH:mm:ss"),
                          pos_cash_management_id = model.PosCashManagementID });
            }
            catch (Exception ex)
            {
                return ApiError.Internal(Request, ex, "PosController.ReceiveCash");
            }
        }

        // POST /legacy/pos/shift-open
        [HttpPost, Route("shift-open")]
        public HttpResponseMessage OpenShift()
        {
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
            catch (Exception ex)
            {
                return ApiError.Internal(Request, ex, "PosController.OpenShift");
            }
        }

        // POST /legacy/pos/shift-close
        // Candela.Api has already computed the breakdown and checked a shift is open, so
        // this only builds the model from the figures it was handed and calls
        // UpdateShiftClosing, then writes the optional denomination count.
        [HttpPost, Route("shift-close")]
        public HttpResponseMessage CloseShift([FromBody] ShiftCloseBody req)
        {
            if (req == null)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "Request body is required" });

            int    userId  = (int)   Request.Properties["user_id"];
            int    shopId  = (int)   Request.Properties["shop_id"];
            string posCode = (string)Request.Properties["pos_code"];

            try
            {
                DateTime now = DateTime.TryParse(req.ClosedAt, out DateTime parsed) ? parsed : DateTime.Now;

                var model = new POSCashManagment
                {
                    PosCashManagementID = req.PosCashManagementId,
                    ShopID              = shopId,
                    POSCode             = posCode,
                    POSDate             = now,
                    Opening             = req.Opening,
                    CashReceived        = req.CashReceived,
                    CashSkimmed         = req.CashSkimmed,
                    NetSales            = req.NetSales,
                    CashCounted         = req.CashCounted,
                    CashSubmitted       = req.CashSubmitted,
                    ClosingCash         = req.ClosingCash,
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
            catch (Exception ex)
            {
                return ApiError.Internal(Request, ex, "PosController.CloseShift");
            }
        }

        // ── helpers (verbatim from PosController) ──────────────────────────────

        private static POSCashManagment BuildMovementModel(POSCashManagmentDetail detail,
            ActivityLog log, string posCode, int shopId, int userId, DateTime now)
        {
            return new POSCashManagment
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
        }

        private void InsertShiftCashCount(int posCashManagementId, int shopId, string posCode,
            DenominationsBody d, DateTime countedAt)
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

    public class CashMovementBody
    {
        [JsonProperty("amount")] public decimal Amount { get; set; }
        [JsonProperty("notes")]  public string  Notes  { get; set; }
    }

    public class DenominationsBody
    {
        [JsonProperty("d5000")] public int    D5000 { get; set; }
        [JsonProperty("d1000")] public int    D1000 { get; set; }
        [JsonProperty("d500")]  public int    D500  { get; set; }
        [JsonProperty("d100")]  public int    D100  { get; set; }
        [JsonProperty("d50")]   public int    D50   { get; set; }
        [JsonProperty("d20")]   public int    D20   { get; set; }
        [JsonProperty("d10")]   public int    D10   { get; set; }
        [JsonProperty("d5")]    public int    D5    { get; set; }
        [JsonProperty("other")] public double Other { get; set; }
    }

    public class ShiftCloseBody
    {
        [JsonProperty("pos_cash_management_id")] public int    PosCashManagementId { get; set; }
        [JsonProperty("opening")]                public double Opening             { get; set; }
        [JsonProperty("cash_received")]          public double CashReceived        { get; set; }
        [JsonProperty("cash_skimmed")]           public double CashSkimmed         { get; set; }
        [JsonProperty("net_sales")]              public double NetSales            { get; set; }
        [JsonProperty("cash_counted")]           public double CashCounted         { get; set; }
        [JsonProperty("cash_submitted")]         public double CashSubmitted       { get; set; }
        [JsonProperty("closing_cash")]           public double ClosingCash         { get; set; }
        [JsonProperty("notes")]                  public string Notes               { get; set; }
        [JsonProperty("closed_at")]              public string ClosedAt            { get; set; }
        [JsonProperty("denominations")]          public DenominationsBody Denominations { get; set; }
    }
}
