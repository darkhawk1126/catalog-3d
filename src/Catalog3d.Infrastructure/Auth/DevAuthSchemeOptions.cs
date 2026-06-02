using Microsoft.AspNetCore.Authentication;

namespace Catalog3d.Infrastructure.Auth;

public sealed class DevAuthSchemeOptions : AuthenticationSchemeOptions
{
    public const string SectionName = "DevUsers";

    /// <summary>
    /// Config-hardcoded users available in development.
    /// Key = username, value = user configuration (groups, display name, etc.).
    /// The auth agent populates the real binding and handler logic.
    /// </summary>
    public Dictionary<string, DevUserEntry> Users { get; set; } = new();
}

public sealed class DevUserEntry
{
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Groups { get; set; } = new();
}
