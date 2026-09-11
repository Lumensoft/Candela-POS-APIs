using Candela.Modules.CustomerClub.Customers.Dtos;

namespace Candela.Modules.CustomerClub.Customers;

/// <summary>
/// Walk-in customer capture and credit lookup.
///
/// All three operations are raw SQL, exactly as the net48 CustomersController did them —
/// they never went through CustomerDAL, so nothing here needs the legacy host. That is
/// the whole reason this slice could move to .NET 10 in one piece: no DAL call means no
/// SQL log / activity log side effects to preserve.
/// </summary>
public interface ICustomersRepository
{
    /// <summary>
    /// Inserts the member (and, when credit is on with a non-zero opening balance, its
    /// seed row in tblMemberClosing) in one transaction, and returns the ids assigned.
    /// </summary>
    Task<CreateCustomerResponse> CreateAsync(CreateCustomerRequest req, int shopId, int userId,
        CancellationToken ct);

    /// <summary>
    /// Live credit position for a customer, or null when no member row matches the id.
    /// Deliberately not filtered by the caller's shop — see the SQL for why.
    /// </summary>
    Task<CreditOutstandingResponse?> GetCreditOutstandingAsync(int memberId, CancellationToken ct);

    /// <summary>
    /// Overwrites the customer's persistent note. False when no row matched id+shop,
    /// which the caller turns into the same 404 the net48 endpoint returned.
    /// </summary>
    Task<bool> UpdateCommentsAsync(int memberId, int shopId, string? comments, CancellationToken ct);
}
