namespace Candela.Modules.Security.Auth;

/// <summary>Registrations for the Auth slice.</summary>
public static class AuthModule
{
    public static IServiceCollection AddAuth(this IServiceCollection services)
    {
        services.AddScoped<IAuthRepository, AuthRepository>();
        services.AddScoped<IAuthService, AuthService>();
        return services;
    }
}
