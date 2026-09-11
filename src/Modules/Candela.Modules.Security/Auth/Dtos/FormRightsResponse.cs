namespace Candela.Modules.Security.Auth.Dtos;

/// <summary>
/// The <c>data</c> body of GET /api/auth/form-rights/{formName} — the same fact the
/// desktop reads through GetFormSecurityControls(Me.Name) and uses to enable or disable
/// New/Save/Update/Delete on a screen. The web app applies it the same way: it never
/// blocks the API call on this alone (see the note on CitiesController), it drives which
/// buttons render.
///
/// New endpoint, no legacy caller — plain properties, camelCase on the wire (formName,
/// controls), same convention as CityResponse and WebLoginResponse.
/// </summary>
public sealed class FormRightsResponse
{
    public string FormName { get; set; } = "";
    public List<string> Controls { get; set; } = new();
}
