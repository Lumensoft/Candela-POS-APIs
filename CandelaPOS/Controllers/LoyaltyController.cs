using System;
using System.Data;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using CandelaPOS.Infrastructure;

namespace CandelaPOS.Controllers
{
    [RoutePrefix("api/customers")]
    public class LoyaltyController : ApiController
    {
        // GET /api/customers/{memberId}/loyalty-points
        // Returns the customer's available loyalty points balance, one-point value,
        // minimum points threshold, and birthday-points sub-total.
        // SQL mirrors frmPointRedemption.FetchCustomerPointsFromServer() (frmPointRedemption.vb:270-316).
        [HttpGet, Route("{memberId:int}/loyalty-points")]
        public HttpResponseMessage GetLoyaltyPoints(int memberId)
        {
            CandelaBootstrap.PrepareRequest();
            int shopId = (int)Request.Properties["shop_id"];
            try
            {
                const string sql = @"
DECLARE @cboBirthdayId INT
SELECT @cboBirthdayId = CboID
FROM   cbotablecollection
WHERE  cboTableName = 'cboPointsType' AND ENGL_US = 'Birthday'

DECLARE @BirthdayPoints NUMERIC(18,4)
SET @BirthdayPoints = (
    SELECT SUM(ISNULL(E.earned_points, 0)) - ISNULL(R.REDEEMED_POINTS, 0)
    FROM   tblMemberPointsEarnings AS E
    LEFT OUTER JOIN (
        SELECT SUM(ISNULL(Redeemed_points, 0)) AS REDEEMED_POINTS,
               member_id, Member_Shop_Id
        FROM   tblMemberPointsRedeemed
        WHERE  Points_Type = @cboBirthdayId
        GROUP BY member_id, Member_Shop_Id
    ) AS R ON E.Member_Id = R.member_id AND E.Member_Shop_Id = R.Member_Shop_Id
    WHERE  E.Member_Id = @memberId AND E.Member_Shop_Id = @shopId
      AND  E.Points_Type = @cboBirthdayId
    GROUP BY E.Member_Id, R.REDEEMED_POINTS
)

SELECT
    SUM(ISNULL(E.earned_points, 0)) - ISNULL(R.REDEEMED_POINTS, 0)  AS points_available,
    (SUM(ISNULL(E.earned_points, 0)) - ISNULL(R.REDEEMED_POINTS, 0))
        * ISNULL(P.One_point_equal, 0)                               AS value_available,
    ISNULL(P.One_point_equal, 0)                                     AS one_point_value,
    ISNULL(@BirthdayPoints, 0)                                       AS birthday_points,
    ISNULL(P.Min_PointOf_Redemption, 0)                              AS min_points_for_redemption
FROM   tblMemberPointsEarnings AS E
INNER JOIN tblMemberInfo           MI ON E.Member_Id      = MI.member_id
                                     AND E.Member_Shop_Id = MI.shop_id
INNER JOIN tblDefMemberTypes       MT ON MI.member_type_id = MT.member_type_id
INNER JOIN tblDefGroupPolicy        P ON MT.member_type_id  = P.member_type_id
LEFT OUTER JOIN (
    SELECT SUM(ISNULL(Redeemed_points, 0))       AS REDEEMED_POINTS,
           SUM(ISNULL(Redeemed_points_value, 0)) AS REDEEMED_VAL,
           member_id, Member_Shop_Id
    FROM   tblMemberPointsRedeemed
    GROUP BY member_id, Member_Shop_Id
) AS R ON E.Member_Id = R.member_id AND E.Member_Shop_Id = R.Member_Shop_Id
WHERE  E.Member_Id = @memberId AND E.Member_Shop_Id = @shopId
GROUP BY E.Member_Id, R.REDEEMED_POINTS, R.REDEEMED_VAL, P.One_point_equal,
         P.Min_PointOf_Redemption";

                using (var con = new SqlConnection(CandelaBootstrap.ConnectionString))
                {
                    con.Open();
                    var cmd = new SqlCommand(sql, con);
                    cmd.Parameters.AddWithValue("@memberId", memberId);
                    cmd.Parameters.AddWithValue("@shopId",   shopId);

                    using (var dt = new DataTable())
                    {
                        new SqlDataAdapter(cmd).Fill(dt);

                        if (dt.Rows.Count == 0)
                        {
                            // Member exists but has no earnings yet — return zeros
                            return Request.CreateResponse(HttpStatusCode.OK, new
                            {
                                success = true,
                                data = new
                                {
                                    points_available         = 0,
                                    value_available          = 0m,
                                    one_point_value          = 0m,
                                    birthday_points          = 0,
                                    min_points_for_redemption = 0,
                                }
                            });
                        }

                        var row = dt.Rows[0];
                        return Request.CreateResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new
                            {
                                points_available          = Convert.ToInt32(row["points_available"]),
                                value_available           = Convert.ToDecimal(row["value_available"]),
                                one_point_value           = Convert.ToDecimal(row["one_point_value"]),
                                birthday_points           = Convert.ToInt32(row["birthday_points"]),
                                min_points_for_redemption = Convert.ToInt32(row["min_points_for_redemption"]),
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }
        }
    }
}
