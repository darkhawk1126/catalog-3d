using Microsoft.AspNetCore.Authorization;

namespace Catalog3d.Web.Authorization;

internal static class CollectionAuthorizationServiceRegistration
{
    /// <summary>
    /// Registers the collection resource-based authorization handler and the
    /// ICollectionAuthorizationService implementation.
    /// </summary>
    internal static IServiceCollection AddCollectionResourceAuthorization(
        this IServiceCollection services)
    {
        services.AddScoped<IAuthorizationHandler, CollectionRoleHandler>();
        return services;
    }
}
