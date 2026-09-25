namespace Candela.Modules.CustomerClub.Customers.Dtos;

/// <summary>
/// Body of POST /api/customers — the walk-in customer the cashier captures at the till.
///
/// Property names are byte-identical to the net48 CreateCustomerRequest and carry no
/// [JsonProperty] on purpose: Newtonsoft matches members case-insensitively, so the
/// tablet's existing payload binds exactly as it did before. Renaming any of these, or
/// adding a serializer attribute, would silently drop a field on the wire.
///
/// The three date fields stay strings: the legacy endpoint parsed them with
/// DateTime.TryParse and fell back to a default when parsing failed, rather than
/// rejecting the request. Typing them as DateTime? here would turn a malformed date
/// into a 400 the tablet has never had to handle.
/// </summary>
public sealed class CreateCustomerRequest
{
    public string? MemberName { get; set; }
    public int MemberTypeId { get; set; }
    public string? PhoneMobile { get; set; }
    public string? PhoneRes { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    /// <summary>tblMemberInfo.InvoiceNo (the desktop's NTN field).</summary>
    public string? Ntn { get; set; }
    /// <summary>tblMemberInfo.nic_no — optional, but when present must be xxxxx-xxxxxxx-x.</summary>
    public string? Cnic { get; set; }
    public bool AllowCredit { get; set; }
    public decimal CreditLimit { get; set; }
    public int? GroupId { get; set; }
    public string? StartDate { get; set; }
    public string? ExpiryDate { get; set; }
    public decimal OpeningBalance { get; set; }
    public string? OpeningDate { get; set; }
}
