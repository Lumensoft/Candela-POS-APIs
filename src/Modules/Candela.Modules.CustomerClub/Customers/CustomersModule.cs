namespace Candela.Modules.CustomerClub.Customers;

/// <summary>Registrations for the Customers slice.</summary>
public static class CustomersModule
{
    public static IServiceCollection AddCustomers(this IServiceCollection services)
    {
        services.AddScoped<ICustomersRepository, CustomersRepository>();
        return services;
    }
}
