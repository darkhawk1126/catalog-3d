using Catalog3d.Application.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog3d.Infrastructure.Auth;

public static class AuthInfrastructureExtensions
{
    /// <summary>
    /// Registers the dev-mode IUserContext (config-hardcoded users).
    /// Call only in development environments; production wires OIDC instead.
    ///
    /// IHttpContextAccessor is registered here because DevUserContext depends on
    /// it; AddHttpContextAccessor is idempotent so calling it from Infrastructure
    /// does not conflict with Web's own registration.
    /// </summary>
    public static IServiceCollection AddDevUserContext(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, DevUserContext>();
        return services;
    }

    /// <summary>
    /// Registers the OIDC-backed IUserContext.
    /// Reads ClaimsPrincipal populated by the OpenIdConnect handler.
    /// Call when Auth:Provider = Oidc.
    /// </summary>
    public static IServiceCollection AddOidcUserContext(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, OidcUserContext>();
        return services;
    }

    /// <summary>
    /// Registers the EF Core-backed ICollectionAuthorizationService.
    /// Scoped lifetime matches CatalogDbContext.
    /// </summary>
    public static IServiceCollection AddCollectionAuthorization(this IServiceCollection services)
    {
        services.AddScoped<ICollectionAuthorizationService, CollectionAuthorizationService>();
        return services;
    }
}
