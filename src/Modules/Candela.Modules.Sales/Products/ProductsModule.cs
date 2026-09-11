namespace Candela.Modules.Sales.Products;

/// <summary>
/// Registration for the Products slice.
///
/// Each feature owns its own registrations so Program.cs stays a list of features
/// rather than a growing list of every class in the project. With 22 Candela modules
/// still to come, that difference matters.
///
/// Scoped, not singleton: a repository holds a connection factory and is used within
/// one request. Singleton would be wrong the moment anything per-request is added.
/// </summary>
public static class ProductsModule
{
    public static IServiceCollection AddProducts(this IServiceCollection services)
    {
        services.AddScoped<IProductRepository, ProductRepository>();
        return services;
    }
}
