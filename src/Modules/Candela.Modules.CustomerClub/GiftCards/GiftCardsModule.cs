namespace Candela.Modules.CustomerClub.GiftCards;

/// <summary>Registrations for the GiftCards slice.</summary>
public static class GiftCardsModule
{
    public static IServiceCollection AddGiftCards(this IServiceCollection services)
    {
        services.AddScoped<IGiftCardsRepository, GiftCardsRepository>();
        return services;
    }
}
