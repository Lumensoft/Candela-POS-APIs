using Candela.Modules.CustomerClub.Loyalty.Dtos;

namespace Candela.Modules.CustomerClub.Loyalty;

/// <summary>
/// Read-only. Loyalty points are earned and redeemed elsewhere (the desktop's member
/// points screens); this only reports the running balance for the checkout screen.
/// </summary>
public interface ILoyaltyRepository
{
    /// <summary>
    /// The member's points balance at <paramref name="shopId"/>, or null when the
    /// member has no earnings row there yet.
    /// </summary>
    Task<LoyaltyPointsResponse?> GetPointsAsync(int memberId, int shopId, CancellationToken ct);
}
