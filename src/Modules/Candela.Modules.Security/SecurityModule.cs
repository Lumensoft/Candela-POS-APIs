using Candela.Modules.Security.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Candela.Modules.Security;

/// <summary>
/// Users, groups, group rights, shop rights and the activity log.
///
/// Level 1. Auth is the first slice migrated into it: login, refresh, logout, supervisor
/// override and the per-user adjustment flags.
/// </summary>
public static class SecurityModule
{
    public static IServiceCollection AddSecurityModule(this IServiceCollection services)
    {
        services.AddAuth();
        return services;
    }
}
