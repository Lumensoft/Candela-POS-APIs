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

    /// <summary>
    /// The customer's card PIN (tblMemberInfo.card_pin_code, stored encrypted with key "f" — see
    /// MemberConfigurationDAL.GetPinCode), decrypted; null/empty when the customer has none.
    /// </summary>
    Task<string?> GetCardPinAsync(int memberId, int memberShopId, CancellationToken ct);
}
