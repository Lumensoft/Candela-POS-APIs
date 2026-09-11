namespace Candela.Modules.Configuration.Masters;

/// <summary>Registrations for the Masters slice.</summary>
public static class MastersModule
{
    public static IServiceCollection AddMasters(this IServiceCollection services)
    {
        services.AddScoped<IMastersRepository, MastersRepository>();
        return services;
    }
}
