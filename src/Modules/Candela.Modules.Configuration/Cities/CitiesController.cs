using Candela.Modules.Configuration.Cities.Dtos;
using Candela.Platform.Api;
using Candela.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Candela.Modules.Configuration.Cities;

/// <summary>
/// City definitions — the web equivalent of frmDefCity.
///
/// Reads are served from SQL here. Writes are handed to the legacy host so CityDAL can do
/// them, because the DAL also writes the SQL log that HO/shop replication replays and the
/// activity log the audit screens read.
///
/// There is no try/catch: ExceptionHandlingMiddleware turns a NotFoundException into a 404
/// and a BusinessRuleException — "Name already exists", "Dependent Record Exists" straight
/// from Candela — into a 422 with that message intact.
/// </summary>
[Route("api/configuration/cities")]
public sealed class CitiesController(ICityService cities) : CandelaControllerBase
{
    /// <summary>GET api/configuration/cities — ordered as the desktop grid orders them.</summary>
    [HttpGet("")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await cities.ListAsync(ct);
        return new JsonResult(new { success = true, count = rows.Count, data = rows });
    }

    /// <summary>GET api/configuration/cities/{id}</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
        => new JsonResult(ApiResponse<CityResponse>.Ok(await cities.GetAsync(id, ct)));

    /// <summary>POST api/configuration/cities</summary>
    [HttpPost("")]
    public async Task<IActionResult> Create([FromBody] CityRequest request, CancellationToken ct)
    {
        var cityId = await cities.CreateAsync(request, UserId, ct);

        return new JsonResult(ApiResponse<CityResponse>.Ok(await cities.GetAsync(cityId, ct)))
        {
            StatusCode = StatusCodes.Status201Created
        };
    }

    /// <summary>PUT api/configuration/cities/{id}</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] CityRequest request, CancellationToken ct)
    {
        await cities.UpdateAsync(id, request, UserId, ct);
        return new JsonResult(ApiResponse<CityResponse>.Ok(await cities.GetAsync(id, ct)));
    }

    /// <summary>
    /// DELETE api/configuration/cities/{id}
    /// Refused with 422 while any area, shop or member still points at this city.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await cities.DeleteAsync(id, UserId, ct);
        return new JsonResult(new { success = true, deleted = id });
    }
}
