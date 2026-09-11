using Microsoft.Extensions.DependencyInjection;

namespace Candela.Modules.Production;

/// <summary>
/// Work orders, job templates, material issuance and return, operators and cost sheets.
///
/// Level 3. No screens have been migrated into it yet - the registration exists so
/// the host is wired for every module from the start, and adding the first screen does
/// not mean editing Program.cs.
/// </summary>
public static class ProductionModule
{
    public static IServiceCollection AddProductionModule(this IServiceCollection services)
    {
        return services;
    }
}
