namespace Candela.Modules.Sales.Pos;

/// <summary>Registrations for the Pos (till cash management) slice.</summary>
public static class PosModule
{
    public static IServiceCollection AddPos(this IServiceCollection services)
    {
        services.AddScoped<IPosRepository, PosRepository>();
        services.AddScoped<IPosService, PosService>();
        return services;
    }
}
