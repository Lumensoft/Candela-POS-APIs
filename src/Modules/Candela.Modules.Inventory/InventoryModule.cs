using Microsoft.Extensions.DependencyInjection;

namespace Candela.Modules.Inventory;

/// <summary>
/// Stock transfer requisitions, blocked products, assemblies, inventory levels and stock audit.
///
/// Level 2. No screens have been migrated into it yet - the registration exists so
/// the host is wired for every module from the start, and adding the first screen does
/// not mean editing Program.cs.
/// </summary>
public static class InventoryModule
{
    public static IServiceCollection AddInventoryModule(this IServiceCollection services)
    {
        return services;
    }
}
