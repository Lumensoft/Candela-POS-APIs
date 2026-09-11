using Candela.Modules.Configuration.Cities.Dtos;
using Candela.Platform.Data;
using Candela.Platform.Legacy;

namespace Candela.Modules.Configuration.Cities;

/// <summary>
/// Reads go straight to SQL through <see cref="IDb"/>; writes go through the legacy host.
/// That split is the whole architecture in one class — see <see cref="ICityRepository"/>.
/// </summary>
public sealed class CityRepository(IDb db, ILegacyHostClient legacy) : ICityRepository
{
    public Task<IReadOnlyList<CityResponse>> GetAllAsync(CancellationToken ct)
    {
        // Mirrors CityDAL.GetAll, joins and ORDER BY included, but with names a JSON
        // client can use. readonly is a varchar holding the literal "ReadOnly" rather
        // than a bit — Candela's storage choice, converted here instead of leaked out.
        const string sql = @"
SELECT  c.city_id                                           AS CityId,
        c.field_name                                        AS CityName,
        c.field_code                                        AS CityCode,
        ISNULL(c.sort_order, 0)                             AS SortOrder,
        c.comments                                          AS Comments,
        ISNULL(c.city_preference, 0)                        AS CityPreference,
        CASE WHEN c.readonly = 'ReadOnly' THEN 1 ELSE 0 END AS IsReadOnly,
        EnteredUser.user_name                               AS EnteredBy,
        c.EnteredDate                                       AS EnteredDate,
        EditedUser.user_name                                AS EditedBy,
        c.EditedDate                                        AS EditedDate
FROM tblDefCities c
LEFT OUTER JOIN tblsecurityuser EnteredUser ON EnteredUser.user_id = c.enteredby
LEFT OUTER JOIN tblsecurityuser EditedUser  ON EditedUser.user_id  = c.editedby
ORDER BY c.sort_order, c.field_name";

        return db.QueryAsync<CityResponse>(sql, ct: ct);
    }

    public Task<CityResponse?> GetByIdAsync(int cityId, CancellationToken ct)
    {
        const string sql = @"
SELECT  c.city_id                                           AS CityId,
        c.field_name                                        AS CityName,
        c.field_code                                        AS CityCode,
        ISNULL(c.sort_order, 0)                             AS SortOrder,
        c.comments                                          AS Comments,
        ISNULL(c.city_preference, 0)                        AS CityPreference,
        CASE WHEN c.readonly = 'ReadOnly' THEN 1 ELSE 0 END AS IsReadOnly,
        EnteredUser.user_name                               AS EnteredBy,
        c.EnteredDate                                       AS EnteredDate,
        EditedUser.user_name                                AS EditedBy,
        c.EditedDate                                        AS EditedDate
FROM tblDefCities c
LEFT OUTER JOIN tblsecurityuser EnteredUser ON EnteredUser.user_id = c.enteredby
LEFT OUTER JOIN tblsecurityuser EditedUser  ON EditedUser.user_id  = c.editedby
WHERE c.city_id = @cityId";

        return db.QuerySingleOrDefaultAsync<CityResponse>(sql, new { cityId }, ct);
    }

    public async Task<int> CreateAsync(CityRequest request, int userId, CancellationToken ct)
    {
        var result = await legacy.PostAsync<LegacyCityResult>("cities", ToLegacy(request, userId), ct);
        return result.CityId;
    }

    public Task UpdateAsync(int cityId, CityRequest request, int userId, CancellationToken ct)
        => legacy.PutAsync<LegacyCityResult>($"cities/{cityId}", ToLegacy(request, userId), ct);

    public Task DeleteAsync(int cityId, int userId, CancellationToken ct)
        => legacy.DeleteAsync<LegacyCityResult>($"cities/{cityId}?userId={userId}", ct);

    private static object ToLegacy(CityRequest request, int userId) => new
    {
        cityName = request.CityName,
        cityCode = request.CityCode,
        sortOrder = request.SortOrder,
        comments = request.Comments,
        userId
    };

    /// <summary>What the legacy host returns from a write: the id it settled on.</summary>
    private sealed class LegacyCityResult
    {
        [Newtonsoft.Json.JsonProperty("city_id")]
        public int CityId { get; set; }
    }
}
