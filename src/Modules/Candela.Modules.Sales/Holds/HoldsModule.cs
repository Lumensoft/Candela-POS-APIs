namespace Candela.Modules.Sales.Holds;

/// <summary>Registrations for the Holds slice.</summary>
public static class HoldsModule
{
    public static IServiceCollection AddHolds(this IServiceCollection services)
    {
        services.AddScoped<IHoldsRepository, HoldsRepository>();
        return services;
    }
}
