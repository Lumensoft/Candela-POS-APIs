using Candela.Modules.Configuration.Cities.Dtos;
using Candela.Shared.Exceptions;

namespace Candela.Modules.Configuration.Cities;

/// <summary>
/// See <see cref="ICityService"/> for what this owns and, more importantly, what it
/// deliberately does not.
///
/// This slice has a service where Products did not, because there are rules here worth
/// naming. A pure lookup does not earn one.
/// </summary>
public sealed class CityService(ICityRepository cities) : ICityService
{
    public Task<IReadOnlyList<CityResponse>> ListAsync(CancellationToken ct)
        => cities.GetAllAsync(ct);

    public async Task<CityResponse> GetAsync(int cityId, CancellationToken ct)
        => await cities.GetByIdAsync(cityId, ct)
           ?? throw new NotFoundException($"City {cityId} was not found.");

    public Task<int> CreateAsync(CityRequest request, int userId, CancellationToken ct)
        => cities.CreateAsync(Normalise(request), userId, ct);

    public async Task UpdateAsync(int cityId, CityRequest request, int userId, CancellationToken ct)
    {
        // Answer 404 rather than letting the legacy host run an UPDATE that matches no
        // rows and reports success.
        _ = await cities.GetByIdAsync(cityId, ct)
            ?? throw new NotFoundException($"City {cityId} was not found.");

        await cities.UpdateAsync(cityId, Normalise(request), userId, ct);
    }

    public async Task DeleteAsync(int cityId, int userId, CancellationToken ct)
    {
        _ = await cities.GetByIdAsync(cityId, ct)
            ?? throw new NotFoundException($"City {cityId} was not found.");

        // Whether it may actually go is CityDAL.IsValidateForDelete's call — it checks
        // areas, shops and members. That refusal comes back as a 422.
        await cities.DeleteAsync(cityId, userId, ct);
    }

    /// <summary>
    /// Trims, and copies the name into an empty code.
    ///
    /// The desktop does this on leaving the name field (frmDefCity.vb:807), so a city
    /// saved without a code gets name-as-code there. Doing the same here means the two
    /// front ends cannot produce different rows from the same input.
    /// </summary>
    private static CityRequest Normalise(CityRequest request)
    {
        var name = (request.CityName ?? "").Trim();
        if (name.Length == 0)
            throw new ValidationException("City name is required.");

        var code = (request.CityCode ?? "").Trim();
        if (code.Length == 0) code = name;

        return new CityRequest
        {
            CityName = name,
            CityCode = code,
            SortOrder = request.SortOrder,
            Comments = (request.Comments ?? "").Trim()
        };
    }
}
