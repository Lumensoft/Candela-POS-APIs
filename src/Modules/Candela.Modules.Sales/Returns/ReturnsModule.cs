namespace Candela.Modules.Sales.Returns;

/// <summary>Registrations for the Returns slice.</summary>
public static class ReturnsModule
{
    public static IServiceCollection AddReturns(this IServiceCollection services)
    {
        services.AddScoped<IReturnsRepository, ReturnsRepository>();
        services.AddScoped<IReturnsService, ReturnsService>();
        return services;
    }
}
