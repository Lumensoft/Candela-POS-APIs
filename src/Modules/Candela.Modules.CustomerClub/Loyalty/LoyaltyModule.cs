namespace Candela.Modules.CustomerClub.Loyalty;

/// <summary>Registrations for the Loyalty slice.</summary>
public static class LoyaltyModule
{
    public static IServiceCollection AddLoyalty(this IServiceCollection services)
    {
        services.AddScoped<ILoyaltyRepository, LoyaltyRepository>();
        return services;
    }
}
