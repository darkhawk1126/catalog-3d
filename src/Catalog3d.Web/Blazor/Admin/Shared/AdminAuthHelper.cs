using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Auth;
using Catalog3d.Infrastructure.Persistence;
using Catalog3d.Web.Blazor.Auth;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Catalog3d.Web.Blazor.Admin.Shared;

/// <summary>
/// Shared authorization helper for Blazor admin pages.
///
/// Resolves the current user from AuthenticationStateProvider (not IHttpContextAccessor —
/// HttpContext is null during Blazor Server SignalR rendering) and exposes admin-check
/// helpers. This is the ONLY place Blazor components need to call for authorization;
/// do NOT inject ICollectionAuthorizationService directly from pages.
///
/// Registration: scoped per-circuit (BlazorAdminServiceRegistration.AddBlazorAdmin).
///
/// USAGE PATTERN — every admin page that gates on admin status:
///
///   [Inject] AdminAuthHelper AuthHelper { get; set; } = null!;
///   [Inject] IDbContextFactory&lt;CatalogDbContext&gt; DbFactory { get; set; } = null!;
///   [Inject] NavigationManager Nav { get; set; } = null!;
///
///   bool _isAdmin;
///
///   protected override async Task OnInitializedAsync()
///   {
///       // Per-collection admin check (Admin = site-admin OR collection-Admin role):
///       await using var db = await DbFactory.CreateDbContextAsync();
///       var collection = await db.Collections
///           .AsNoTracking()
///           .FirstOrDefaultAsync(c => c.Slug == Slug);
///       if (collection is null) { Nav.NavigateTo("/collections"); return; }
///
///       _isAdmin = await AuthHelper.IsCollectionAdminAsync(collection.Id);
///       if (!_isAdmin) { Nav.NavigateTo($"/collections/{Slug}"); return; }
///   }
///
///   // Site-admin-only pages (e.g. create collection):
///   protected override async Task OnInitializedAsync()
///   {
///       await AuthHelper.InitializeAsync();  // must call before IsSiteAdmin()
///       if (!AuthHelper.IsSiteAdmin()) { Nav.NavigateTo("/collections"); return; }
///   }
///
/// RULE: Call InitializeAsync() once in OnInitializedAsync before any IsSiteAdmin() or
/// IsCollectionAdminAsync() calls. The initialization caches the auth state for the
/// lifetime of the component's OnInitializedAsync call.
/// </summary>
public sealed class AdminAuthHelper
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly ICollectionAuthorizationService _authService;
    private readonly CatalogAuthorizationOptions _authzOptions;
    private readonly IConfiguration _configuration;

    private ClaimsPrincipal? _user;

    public AdminAuthHelper(
        AuthenticationStateProvider authStateProvider,
        ICollectionAuthorizationService authService,
        IOptions<CatalogAuthorizationOptions> authzOptions,
        IConfiguration configuration)
    {
        _authStateProvider = authStateProvider;
        _authService = authService;
        _authzOptions = authzOptions.Value;
        _configuration = configuration;
    }

    /// <summary>
    /// Loads the current authentication state. Call once at the start of OnInitializedAsync.
    /// Subsequent calls are idempotent (state is cached for the component lifecycle).
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_user is not null) return;
        var state = await _authStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        _user = state.User;
    }

    /// <summary>Current user principal. Null until InitializeAsync is called.</summary>
    public ClaimsPrincipal? User => _user;

    /// <summary>True when the current user is authenticated.</summary>
    public bool IsAuthenticated => _user?.Identity?.IsAuthenticated is true;

    /// <summary>
    /// Returns true if the current user is a site-admin (member of any SiteAdminGroups config entry).
    /// Site-admins hold Admin on every collection without needing a RoleAssignment row.
    /// REQUIRES InitializeAsync() to have been called first.
    /// </summary>
    public bool IsSiteAdmin()
    {
        if (_user is null) throw new InvalidOperationException("Call InitializeAsync first.");
        if (!IsAuthenticated) return false;

        var adminGroups = _authzOptions.SiteAdminGroups;
        if (adminGroups is null or { Count: 0 }) return false;

        // Both OIDC and Dev schemes store groups under the literal "groups" claim type.
        // M1: compare OrdinalIgnoreCase; OidcUserContext/DevUserContext lower-case at read
        // but the raw claim values here may still be mixed-case.
        var groups = _user.FindAll("groups").Select(c => c.Value);

        foreach (var group in groups)
        {
            foreach (var adminGroup in adminGroups)
            {
                if (string.Equals(group, adminGroup, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the IUserContext-compatible principal set for use with ICollectionAuthorizationService.
    /// Builds a BlazorPrincipal from the current ClaimsPrincipal.
    /// REQUIRES InitializeAsync() to have been called first.
    /// </summary>
    private IUserContext BuildUserContext()
    {
        if (_user is null) throw new InvalidOperationException("Call InitializeAsync first.");
        var provider = _configuration["Auth:Provider"] ?? "Dev";
        return new ClaimsPrincipalUserContext(_user, provider);
    }

    /// <summary>
    /// Returns true if the current user holds CollectionRole.Admin on the specified collection.
    /// Applies the site-admin short-circuit via ICollectionAuthorizationService.
    /// REQUIRES InitializeAsync() to have been called first.
    /// </summary>
    public Task<bool> IsCollectionAdminAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        return _authService.AuthorizeAsync(
            collectionId, BuildUserContext(), CollectionRole.Admin, cancellationToken);
    }

    /// <summary>
    /// Returns true if the current user holds at least <paramref name="minimumRole"/> on
    /// the specified collection.
    /// REQUIRES InitializeAsync() to have been called first.
    /// </summary>
    public Task<bool> HasCollectionRoleAsync(
        Guid collectionId,
        CollectionRole minimumRole,
        CancellationToken cancellationToken = default)
    {
        return _authService.AuthorizeAsync(
            collectionId, BuildUserContext(), minimumRole, cancellationToken);
    }

    /// <summary>
    /// Gets the display name of the current user.
    /// REQUIRES InitializeAsync() to have been called first.
    /// </summary>
    public string DisplayName => BuildUserContext().DisplayName;

    /// <summary>
    /// Gets the UserId of the current user (stable identifier, matches RoleAssignment.Principal).
    /// REQUIRES InitializeAsync() to have been called first.
    /// </summary>
    public string UserId => BuildUserContext().UserId;
}
