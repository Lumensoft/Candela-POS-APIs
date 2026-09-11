namespace Candela.Modules.Configuration.Cities.Dtos;

/// <summary>
/// A city as the client sees it.
///
/// Unlike the Products slice this is a NEW endpoint — no tablet screen depends on it yet
/// — so it returns a typed shape with camelCase names rather than the raw "[City ID]" /
/// "[City Name]" aliases CityDAL.GetAll produces for the WinForms grid. Where an existing
/// contract is being ported the old shape is preserved; where there is none, a clean one
/// is defined.
/// </summary>
public sealed class CityResponse
{
    public int CityId { get; set; }
    public string CityName { get; set; } = "";
    public string CityCode { get; set; } = "";
    public int SortOrder { get; set; }
    public string? Comments { get; set; }

    /// <summary>Read-only here: the desktop form does not edit these either.</summary>
    public int CityPreference { get; set; }

    public bool IsReadOnly { get; set; }

    public string? EnteredBy { get; set; }
    public DateTime? EnteredDate { get; set; }
    public string? EditedBy { get; set; }
    public DateTime? EditedDate { get; set; }
}
