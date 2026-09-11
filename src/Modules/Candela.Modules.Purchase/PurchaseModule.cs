using Microsoft.Extensions.DependencyInjection;

namespace Candela.Modules.Purchase;

/// <summary>
/// Purchase orders, GRN, return to vendor, supplier payments, RPO and kitting.
///
/// Level 3. No screens have been migrated into it yet - the registration exists so
/// the host is wired for every module from the start, and adding the first screen does
/// not mean editing Program.cs.
/// </summary>
public static class PurchaseModule
{
    public static IServiceCollection AddPurchaseModule(this IServiceCollection services)
    {
        return services;
    }
}
