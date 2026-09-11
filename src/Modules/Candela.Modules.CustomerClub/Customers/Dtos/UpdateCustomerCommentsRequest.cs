namespace Candela.Modules.CustomerClub.Customers.Dtos;

/// <summary>
/// Body of PUT /api/customers/{id}/comments — the customer's persistent note
/// (tblMemberInfo.comments), the web equivalent of frmCustomerComent's Update button.
/// Same single field, same name, as the net48 request class.
/// </summary>
public sealed class UpdateCustomerCommentsRequest
{
    public string? Comments { get; set; }
}
