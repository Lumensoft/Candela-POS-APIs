using Candela.Modules.Configuration.Cities;
using Candela.Modules.Configuration.Masters;
using Candela.Modules.Configuration.Products;
using Microsoft.Extensions.DependencyInjection;

namespace Candela.Modules.Configuration;

/// <summary>
/// Everything the Configuration module contributes: master data - cities, suppliers,
/// products, shops, tax codes and the rest.
///
/// Level 1. It depends on nothing above it, and nothing above it may be referenced from
/// here: masters must never need a transaction module, which is what keeps the whole
/// module graph acyclic.
///
/// Each screen still owns its own registrations in its own folder; this only lists which
/// screens the module switches on, so adding one means touching a single folder plus one
/// line here.
/// </summary>
public static class ConfigurationModule
{
    public static IServiceCollection AddConfigurationModule(this IServiceCollection services)
    {
        services.AddCities();
        services.AddMasters();
        services.AddProducts();
        return services;
    }
}
