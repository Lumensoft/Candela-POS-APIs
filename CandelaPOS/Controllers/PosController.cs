using System;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using CandelaPOS.Infrastructure;
using DAL.ShopActivities;
using Model;

namespace CandelaPOS.Controllers
{
    [RoutePrefix("api/pos")]
    public class PosController : ApiController
    {
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
                    new { error = ex.Message });
            }
        }
    }

    public class CashSkimRequest
    {
        public decimal Amount { get; set; }
        public string  Notes  { get; set; } // optional
    }
}
