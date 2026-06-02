using Catalog3d.Infrastructure.Auth;
using Catalog3d.Web.Authorization;

namespace Catalog3d.Web.Auth;

internal static class DevAuthServiceRegistration
{
    internal static IServiceCollection AddDevAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAuthentication("DevScheme")
            .AddScheme<DevAuthSchemeOptions, DevAuthHandler>(
                "DevScheme",
                options => configuration.GetSection(DevAuthSchemeOptions.SectionName).Bind(options));

        services.AddAuthorization();

        services.AddDevUserContext();
        services.AddCollectionAuthorization();
        services.AddCollectionResourceAuthorization();

        return services;
    }
}
