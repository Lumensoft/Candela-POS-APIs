using Candela.Modules.CustomerClub.Loyalty.Dtos;
using Candela.Platform.Data;

namespace Candela.Modules.CustomerClub.Loyalty;

/// <summary>
/// The SQL is copied verbatim from the net48 LoyaltyController, which itself mirrors
/// frmPointRedemption.FetchCustomerPointsFromServer() (frmPointRedemption.vb:270-316).
/// Not reformatted: the birthday-points sub-select and the two redemption LEFT JOINs
/// decide the numbers, and any tidy-up risks changing them.
/// </summary>
public sealed class LoyaltyRepository(IDb db) : ILoyaltyRepository
{
    public async Task<LoyaltyPointsResponse?> GetPointsAsync(int memberId, int shopId, CancellationToken ct)
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

        // Legacy: `if (dt.Rows.Count == 0) return zeros; else var row = dt.Rows[0];`.
        // QueryFirstOrDefault, not QuerySingleOrDefault: the GROUP BY makes more than one
        // row possible in theory, and the legacy code would silently take the first —
        // QuerySingleOrDefault would turn that into a 500.
        var row = await db.QueryFirstOrDefaultAsync<Row>(sql, new { memberId, shopId }, ct);
        if (row is null) return null;

        // Convert.ToInt32 / Convert.ToDecimal in the legacy code — a NUMERIC column can
        // come back as decimal, so round-then-cast matches "Convert.ToInt32(decimal)"
        // (banker's rounding, same as the CLR conversion the legacy code used).
        return new LoyaltyPointsResponse
        {
            PointsAvailable        = (int)decimal.Round(row.points_available),
            ValueAvailable         = row.value_available,
            OnePointValue          = row.one_point_value,
            BirthdayPoints         = (int)decimal.Round(row.birthday_points),
            MinPointsForRedemption = (int)decimal.Round(row.min_points_for_redemption)
        };
    }

    // Column names match the SELECT aliases so Dapper binds by name without mapping.
    private sealed class Row
    {
        public decimal points_available { get; set; }
        public decimal value_available { get; set; }
        public decimal one_point_value { get; set; }
        public decimal birthday_points { get; set; }
        public decimal min_points_for_redemption { get; set; }
    }
}
