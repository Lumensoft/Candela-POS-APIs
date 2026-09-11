using System.ComponentModel.DataAnnotations;

namespace Candela.Modules.Configuration.Cities.Dtos;

/// <summary>
/// Create/update payload for a city — only the fields frmDefCity actually edits.
///
/// Required-ness mirrors the desktop form's own checks (frmDefCity.vb:360-378).
/// tblDefCities.city_preference and .readonly are deliberately absent: the form has no
/// inputs for them either, so accepting them here would be inventing behaviour.
/// </summary>
public sealed class CityRequest
{
    [Required(ErrorMessage = "City name is required.")]
    [StringLength(100, ErrorMessage = "City name is too long.")]
    public string CityName { get; set; } = "";

    /// <summary>
    /// Optional on the wire. The desktop copies the name into an empty code as you leave
    /// the name field (frmDefCity.vb:807) and the service does the same, so a client that
    /// omits it gets exactly what a cashier would.
    /// </summary>
    [StringLength(50, ErrorMessage = "City code is too long.")]
    public string? CityCode { get; set; }

    /// <summary>Blank on the form becomes 0 (frmDefCity.vb:372-374).</summary>
    [Range(0, int.MaxValue, ErrorMessage = "Sort order cannot be negative.")]
    public int SortOrder { get; set; }

    [StringLength(500, ErrorMessage = "Comments are too long.")]
    public string? Comments { get; set; }
}
