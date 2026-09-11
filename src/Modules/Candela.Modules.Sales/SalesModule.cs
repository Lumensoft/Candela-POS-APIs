using Candela.Modules.Sales.Hardware;
using Candela.Modules.Sales.Holds;
using Candela.Modules.Sales.Invoices;
using Candela.Modules.Sales.Pos;
using Candela.Modules.Sales.Products;
using Candela.Modules.Sales.Returns;
using Microsoft.Extensions.DependencyInjection;

namespace Candela.Modules.Sales;

/// <summary>
/// Shop Activities: the till itself - sale and return, quote, holds, cash management,
/// closing, physical audit - plus the POS lookups the tablet needs while scanning.
///
/// Level 4. It may use Configuration, Inventory and CustomerClub; nothing above it.
/// </summary>
public static class SalesModule
{
    public static IServiceCollection AddSalesModule(this IServiceCollection services)
    {
        services.AddProducts();
        services.AddHolds();
        services.AddHardware();
        services.AddPos();
        services.AddReturns();
        services.AddInvoices();
        return services;
    }
}
