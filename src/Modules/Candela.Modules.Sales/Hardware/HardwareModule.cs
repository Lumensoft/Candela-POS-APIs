namespace Candela.Modules.Sales.Hardware;

/// <summary>Registrations for the Hardware slice.</summary>
public static class HardwareModule
{
    public static IServiceCollection AddHardware(this IServiceCollection services)
    {
        services.AddScoped<IDrawerService, DrawerService>();
        return services;
    }
}
