using Microsoft.Extensions.DependencyInjection;

namespace Candela.Modules.Accounting;

/// <summary>
/// Vouchers, opening and missing vouchers, chart of accounts and GL integration.
///
/// Level 5. No screens have been migrated into it yet - the registration exists so
/// the host is wired for every module from the start, and adding the first screen does
/// not mean editing Program.cs.
/// </summary>
public static class AccountingModule
{
    public static IServiceCollection AddAccountingModule(this IServiceCollection services)
    {
        return services;
    }
}
