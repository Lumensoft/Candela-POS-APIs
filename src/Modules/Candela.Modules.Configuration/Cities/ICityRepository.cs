using Candela.Modules.Configuration.Cities.Dtos;

namespace Candela.Modules.Configuration.Cities;

/// <summary>
/// Data access for cities.
///
/// Reads query SQL directly. Writes do NOT: a city INSERT is not just an INSERT, because
/// CityDAL also writes the SQL log that HO/shop replication replays and the activity log
/// the audit screens read. So writes go through the legacy host and it calls the DAL.
/// </summary>
public interface ICityRepository
{
    Task<IReadOnlyList<CityResponse>> GetAllAsync(CancellationToken ct);
    Task<CityResponse?> GetByIdAsync(int cityId, CancellationToken ct);

    Task<int> CreateAsync(CityRequest request, int userId, CancellationToken ct);
    Task UpdateAsync(int cityId, CityRequest request, int userId, CancellationToken ct);
    Task DeleteAsync(int cityId, int userId, CancellationToken ct);
}
