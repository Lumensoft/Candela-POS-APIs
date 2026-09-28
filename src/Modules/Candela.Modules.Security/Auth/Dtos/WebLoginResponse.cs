namespace Candela.Modules.Security.Auth.Dtos;

/// <summary>
/// The <c>data</c> body of POST /api/auth/web-login.
///
/// Unlike LoginResponse (the tablet's contract, frozen in snake_case because 47 call
/// sites already depend on it) this is a brand-new endpoint with no existing caller to
/// stay compatible with — so, like CityResponse, it uses plain properties and lets
/// AddNewtonsoftJson's CamelCasePropertyNamesContractResolver produce camelCase on the
/// wire: token, userId, userName, groupName, groupType.
///
/// No per-form control rights here on purpose — a back-office session may touch dozens of
/// screens, and baking every one of their rights into the JWT (or into this payload) would
/// mean re-issuing a token every time a new screen's rights are needed. Instead the web app
/// asks GET /api/auth/form-rights/{formName} once per screen it opens, and TanStack Query
/// caches the answer. See AuthService.GetFormRightsAsync.
///
/// ShopId/ShopName/PosCode reflect the tblComputerList seat the request's device_id claimed
/// or already held — the same seat AuthService.LoginAsync claims for a tablet, since
/// WebLoginAsync now runs through the same device-binding path.
/// </summary>
public sealed class WebLoginResponse
{
    public string Token { get; set; } = "";
    public int UserId { get; set; }
    public string UserName { get; set; } = "";
    public string GroupName { get; set; } = "";
    public int GroupType { get; set; }
    public int ShopId { get; set; }
    public string ShopName { get; set; } = "";
    public string PosCode { get; set; } = "";
}
