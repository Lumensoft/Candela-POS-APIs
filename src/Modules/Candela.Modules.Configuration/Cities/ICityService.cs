using Candela.Modules.Configuration.Cities.Dtos;

namespace Candela.Modules.Configuration.Cities;

/// <summary>
/// The rules frmDefCity applies before it ever reaches the DAL: trimming, the
/// code-defaults-to-name behaviour, and what "not found" means.
///
/// Duplicate name and duplicate code are NOT here. CityDAL.IsValidateForSave owns those,
/// and a second copy would drift from it — and would race anyway, since the check and the
/// insert would be separated by a network hop.
/// </summary>
public interface ICityService
{
    Task<IReadOnlyList<CityResponse>> ListAsync(CancellationToken ct);
    Task<CityResponse> GetAsync(int cityId, CancellationToken ct);
    Task<int> CreateAsync(CityRequest request, int userId, CancellationToken ct);
    Task UpdateAsync(int cityId, CityRequest request, int userId, CancellationToken ct);
    Task DeleteAsync(int cityId, int userId, CancellationToken ct);
}
