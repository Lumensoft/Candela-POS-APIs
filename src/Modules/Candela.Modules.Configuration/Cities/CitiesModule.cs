namespace Candela.Modules.Configuration.Cities;

/// <summary>
/// Registration for the Cities slice. Program.cs stays a list of features rather than a
/// list of classes, which is what keeps it readable as the other Candela modules arrive.
/// </summary>
public static class CitiesModule
{
    public static IServiceCollection AddCities(this IServiceCollection services)
    {
        services.AddScoped<ICityRepository, CityRepository>();
        services.AddScoped<ICityService, CityService>();
        return services;
    }
}
