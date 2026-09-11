using Newtonsoft.Json;

namespace Candela.Modules.CustomerClub.Loyalty.Dtos;

/// <summary>
/// The <c>data</c> body of GET /api/customers/{memberId}/loyalty-points.
///
/// Field names are snake_case and are set explicitly with [JsonProperty] rather than
/// left to the camelCase resolver: the legacy endpoint returned an anonymous object
/// whose members were already snake_case, so "pointsAvailable" would be a contract
/// break. The tablet reads these exact keys.
///
/// Types match the legacy Convert calls exactly:
///   points_available / birthday_points / min_points_for_redemption  -> int
///   value_available  / one_point_value                              -> decimal
/// </summary>
public sealed class LoyaltyPointsResponse
{
    [JsonProperty("points_available")]
    public int PointsAvailable { get; set; }

    [JsonProperty("value_available")]
    public decimal ValueAvailable { get; set; }

    [JsonProperty("one_point_value")]
    public decimal OnePointValue { get; set; }

    [JsonProperty("birthday_points")]
    public int BirthdayPoints { get; set; }

    [JsonProperty("min_points_for_redemption")]
    public int MinPointsForRedemption { get; set; }

    /// <summary>
    /// The zero body returned when the member has no earnings row yet. The legacy
    /// endpoint returns this rather than 404 (frmPointRedemption behaviour), so the
    /// tablet can show a customer with a zero balance instead of an error.
    /// </summary>
    public static LoyaltyPointsResponse Zero => new();
}
