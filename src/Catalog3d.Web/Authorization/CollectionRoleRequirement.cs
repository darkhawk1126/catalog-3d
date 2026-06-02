using Catalog3d.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace Catalog3d.Web.Authorization;

/// <summary>
/// Authorization requirement that asserts the caller holds at least
/// <see cref="MinimumRole"/> on the target collection. The resource passed to
/// <see cref="IAuthorizationService.AuthorizeAsync"/> must be a <see cref="Guid"/>
/// collection ID.
/// </summary>
public sealed class CollectionRoleRequirement : IAuthorizationRequirement
{
    public CollectionRoleRequirement(CollectionRole minimumRole)
    {
        MinimumRole = minimumRole;
    }

    public CollectionRole MinimumRole { get; }
}
