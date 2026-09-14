namespace Candela.Modules.Configuration.Products;

/// <summary>Registration for the Product Definition slice, growing tab by tab.</summary>
public static class ProductsModule
{
    public static IServiceCollection AddProducts(this IServiceCollection services)
    {
        services.AddScoped<IProductLookupsRepository, ProductLookupsRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IProductService, ProductService>();
        return services;
    }
}
