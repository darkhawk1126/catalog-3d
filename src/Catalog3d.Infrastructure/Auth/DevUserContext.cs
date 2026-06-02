using Catalog3d.Application.Abstractions;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Catalog3d.Infrastructure.Auth;

/// <summary>
/// Dev-mode IUserContext that reads claims placed on ClaimsPrincipal by
/// DevAuthHandler. The claim shape is OIDC-compatible so that swapping this
/// type for an OidcUserContext requires no changes in callers.
/// </summary>
internal sealed class DevUserContext : IUserContext
{
    private readonly ClaimsPrincipal _user;

    public DevUserContext(IHttpContextAccessor httpContextAccessor)
    {
        // HttpContext is non-null in all request-scoped usages; null guard here
        // prevents DI resolution failures in test scenarios where no context exists.
        _user = httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal();
    }

    public bool IsAuthenticated =>
        _user.Identity?.IsAuthenticated is true;

    public string UserId =>
        _user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    public string DisplayName =>
        _user.FindFirstValue(ClaimTypes.Name) ?? string.Empty;

    public IReadOnlyList<string> Groups =>
        _user.FindAll(DevAuthHandler.GroupsClaimType)
             .Select(c => c.Value)
             .ToList()
             .AsReadOnly();
}
