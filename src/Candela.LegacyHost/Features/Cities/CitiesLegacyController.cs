using System;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using CandelaPOS.Shared.Errors;
using CandelaPOS.Shared.Logging;
using DAL;
using Model;
using static Utility.Utility;

namespace CandelaPOS.Features.Cities
{
    /// <summary>
    /// The only way the .NET 10 API is allowed to write a City.
    ///
    /// Writes go through CityDAL rather than straight SQL because the DAL does more than
    /// the INSERT: it writes the SQL log that HO/shop replication replays, and the
    /// activity log the audit screens read. Bypassing it would save a hop and silently
    /// break both.
    ///
    /// Each endpoint runs the DAL's own validation and then the write in ONE request.
    /// The desktop calls IsValidateForSave and Add as two separate steps
    /// (frmDefCity.vb:394 then :460); doing both here also closes the gap where another
    /// user could take the same name in between.
    ///
    /// Reads are NOT here. They have no such requirement, so the API queries them
    /// directly and skips this hop entirely.
    /// </summary>
    [RoutePrefix("legacy/cities")]
    public class CitiesLegacyController : ApiController
    {
        // POST legacy/cities
        [HttpPost, Route("")]
        public HttpResponseMessage Create([FromBody] LegacyCityRequest req)
        {
            if (req == null) return Invalid("Request body is required.");

            try
            {
                var city = ToModel(req, 0);

                // frmDefCity.vb:394 — duplicate name, then duplicate code.
                new CityDAL().IsValidateForSave(city);

                if (!new CityDAL().Add(city))
                    throw new Exception("CityDAL.Add returned false.");

                AppLog.Info("City {0} created by user {1}", city.CityID, req.UserId);
                return Request.CreateResponse(HttpStatusCode.OK, new { city_id = city.CityID });
            }
            catch (Exception ex) { return Translate(ex, "CitiesLegacyController.Create"); }
        }

        // PUT legacy/cities/{id}
        [HttpPut, Route("{id:int}")]
        public HttpResponseMessage Update(int id, [FromBody] LegacyCityRequest req)
        {
            if (req == null) return Invalid("Request body is required.");
            if (id <= 0) return Invalid("A valid city id is required.");

            try
            {
                var city = ToModel(req, id);

                new CityDAL().IsValidateForSave(city);

                if (!new CityDAL().Update(city))
                    throw new Exception("CityDAL.Update returned false.");

                AppLog.Info("City {0} updated by user {1}", id, req.UserId);
                return Request.CreateResponse(HttpStatusCode.OK, new { city_id = id });
            }
            catch (Exception ex) { return Translate(ex, "CitiesLegacyController.Update"); }
        }

        // DELETE legacy/cities/{id}
        [HttpDelete, Route("{id:int}")]
        public HttpResponseMessage Delete(int id, [FromUri] int userId = 0)
        {
            if (id <= 0) return Invalid("A valid city id is required.");

            try
            {
                var city = new City { CityID = id };
                StampActivityLog(city, userId, "Delete");

                // frmDefCity.vb:389 — refuses while areas, shops or members still point here.
                new CityDAL().IsValidateForDelete(city);

                if (!new CityDAL().Deleted(city))
                    throw new Exception("CityDAL.Deleted returned false.");

                AppLog.Info("City {0} deleted by user {1}", id, userId);
                return Request.CreateResponse(HttpStatusCode.OK, new { city_id = id });
            }
            catch (Exception ex) { return Translate(ex, "CitiesLegacyController.Delete"); }
        }

        /// <summary>
        /// Builds the Model.City the DAL expects, including the ActivityLog fields the
        /// desktop sets in frmDefCity.FillModel. ShopID is -1 because a city is head
        /// office configuration, not shop data.
        ///
        /// CityPerference and IsReadOnly are left at their defaults: the desktop form has
        /// no inputs for them either, so setting them here would be inventing behaviour.
        /// </summary>
        private static City ToModel(LegacyCityRequest req, int cityId)
        {
            var city = new City
            {
                CityID = cityId,
                CityName = (req.CityName ?? "").Trim(),
                CityCode = (req.CityCode ?? "").Trim(),
                SortOrder = req.SortOrder,
                Comments = (req.Comments ?? "").Trim(),
                EnteredBy = req.UserId,
                EditedBy = req.UserId
            };

            // EnteredDate / EditedDate are written by the DAL with GetDate() (CR # 7312),
            // so the server clock decides, not the caller's.
            StampActivityLog(city, req.UserId, cityId == 0 ? "Save" : "Update");
            return city;
        }

        private static void StampActivityLog(City city, int userId, string action)
        {
            city.ActivityLog.ShopID = -1;
            city.ActivityLog.ScreenTitle = "City";
            city.ActivityLog.LogGroup = "Definition";
            city.ActivityLog.UserID = userId;
            city.ActivityLog.FormAction = action;
        }

        /// <summary>
        /// The DAL signals business rules by throwing with one of Candela's own message
        /// constants. Those are meant for the person at the screen, so they are passed
        /// through with 422. Anything else is a real fault: logged in full, answered
        /// generically.
        ///
        /// Matching on the constants rather than on substrings means a reworded message
        /// changes in one place and this keeps working.
        /// </summary>
        private HttpResponseMessage Translate(Exception ex, string context)
        {
            var msg = ex.Message ?? "";

            bool isBusinessRule =
                msg.IndexOf(gstrMsgDuplicateName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf(gstrMsgDuplicateCode, StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf(gstrMsgDependentRecordExist, StringComparison.OrdinalIgnoreCase) >= 0;

            if (isBusinessRule)
            {
                AppLog.Warn("{0} refused: {1}", context, msg);
                return Request.CreateResponse((HttpStatusCode)422, new { error = msg });
            }

            return ApiError.Internal(Request, ex, context);
        }

        private HttpResponseMessage Invalid(string message) =>
            Request.CreateResponse(HttpStatusCode.BadRequest, new { error = message });
    }

    /// <summary>Write payload for a city. Mirrors what frmDefCity.FillModel builds.</summary>
    public class LegacyCityRequest
    {
        public string CityName { get; set; }
        public string CityCode { get; set; }
        public int SortOrder { get; set; }
        public string Comments { get; set; }

        /// <summary>tblSecurityUser.user_id, for EnteredBy/EditedBy and the activity log.</summary>
        public int UserId { get; set; }
    }
}
