using Catalog3d.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;

namespace Catalog3d.Web.Authorization;

/// <summary>
/// Handles <see cref="CollectionRoleRequirement"/> for a resource of type <see cref="Guid"/>
/// (the collection ID). Delegates to <see cref="ICollectionAuthorizationService"/> which
/// is the single ACL choke-point for all access decisions.
/// </summary>
internal sealed class CollectionRoleHandler
    : AuthorizationHandler<CollectionRoleRequirement, Guid>
{
    private readonly ICollectionAuthorizationService _authService;
    private readonly IUserContext _userContext;

    public CollectionRoleHandler(
        ICollectionAuthorizationService authService,
        IUserContext userContext)
    {
        _authService = authService;
        _userContext = userContext;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CollectionRoleRequirement requirement,
        Guid collectionId)
    {
        var authorized = await _authService
            .AuthorizeAsync(collectionId, _userContext, requirement.MinimumRole)
            .ConfigureAwait(false);

        if (authorized)
            context.Succeed(requirement);
        // Do not call Fail — let other handlers run, and let the framework return
        // 403 vs 401 based on authentication state.
    }
}
