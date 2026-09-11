namespace Candela.Modules.Sales.Invoices;

/// <summary>Registrations for the Sale Invoices slice.</summary>
public static class InvoicesModule
{
    public static IServiceCollection AddInvoices(this IServiceCollection services)
    {
        services.AddScoped<ISalesRepository, SalesRepository>();
        services.AddScoped<ISalesService, SalesService>();
        return services;
    }
}
