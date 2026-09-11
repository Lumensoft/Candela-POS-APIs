using Microsoft.Extensions.DependencyInjection;

namespace Candela.Modules.Utilities;

/// <summary>
/// Barcode and receipt designers, language translation, data sync, backup and integrations.
///
/// Level 6. No screens have been migrated into it yet - the registration exists so
/// the host is wired for every module from the start, and adding the first screen does
/// not mean editing Program.cs.
/// </summary>
public static class UtilitiesModule
{
    public static IServiceCollection AddUtilitiesModule(this IServiceCollection services)
    {
        return services;
    }
}
