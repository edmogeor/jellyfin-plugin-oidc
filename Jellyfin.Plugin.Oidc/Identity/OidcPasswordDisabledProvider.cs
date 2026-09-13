using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Rejects local credential authentication for a Jellyfin User selected by plugin policy.</summary>
public sealed class OidcPasswordDisabledProvider : IAuthenticationProvider
{
    /// <inheritdoc />
    public string Name => "OIDC only";

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
        => throw new AuthenticationException("Local credential login is disabled.");

    /// <inheritdoc />
    public Task ChangePassword(User user, string newPassword)
        => throw new AuthenticationException("Local credential login is disabled.");
}
