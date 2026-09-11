using Candela.Modules.CustomerClub.Customers;
using Candela.Modules.CustomerClub.GiftCards;
using Candela.Modules.CustomerClub.Loyalty;

namespace Candela.Modules.CustomerClub;

/// <summary>
/// Customer Club: gift cards, member points, membership policy, redemption, claims.
///
/// Level 3 — may use Configuration; nothing above it. Loyalty, Customers and GiftCards
/// are the slices migrated from the net48 host so far.
/// </summary>
public static class CustomerClubModule
{
    public static IServiceCollection AddCustomerClubModule(this IServiceCollection services)
    {
        services.AddCustomers();
        services.AddGiftCards();
        services.AddLoyalty();
        return services;
    }
}
