using Catalog3d.Application.Abstractions;
using Catalog3d.Infrastructure.Users;
using Catalog3d.Infrastructure.Provisioning;
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
    /// Registers the EF Core-backed ICollectionAuthorizationService and the model-level
    /// IModelAuthorizationService that builds on it. Scoped lifetime matches CatalogDbContext.
    /// </summary>
    public static IServiceCollection AddCollectionAuthorization(this IServiceCollection services)
    {
        services.AddScoped<ICollectionAuthorizationService, CollectionAuthorizationService>();
        services.AddScoped<IModelAuthorizationService, ModelAuthorizationService>();

        // Personal-collection provisioning: scoped service (owns a request DbContext) backed by
        // a singleton cache so it is a no-op after a principal's first authenticated request.
        services.AddSingleton<ProvisionedPrincipalCache>();
        services.AddScoped<IPersonalCollectionProvisioner, PersonalCollectionProvisioner>();

        // User directory: a convenience picker source populated lazily as users sign in. Singleton
        // because it uses IDbContextFactory (safe from both middleware and Blazor) and carries an
        // in-memory write throttle. NOT an authorization source — purely for friendly name lookup.
        services.AddSingleton<IUserDirectory, UserDirectory>();
        return services;
    }
}
