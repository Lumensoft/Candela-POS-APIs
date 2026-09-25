namespace Candela.Modules.CustomerClub.Customers.Dtos;

/// <summary>
/// Body of PUT /api/customers/{id} — the fields the till's Customer dialog can edit.
/// Credit settings and member type are deliberately not editable here.
///
/// Like <see cref="CreateCustomerRequest"/>, no [JsonProperty]: Newtonsoft matches
/// member names case-insensitively, so the tablet's PascalCase payload binds as is.
/// </summary>
public sealed class UpdateCustomerRequest
{
    public string? MemberName { get; set; }
    public string? PhoneMobile { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? Ntn { get; set; }
    public string? Cnic { get; set; }
    public int? GroupId { get; set; }
    public string? StartDate { get; set; }
    public string? ExpiryDate { get; set; }
}
