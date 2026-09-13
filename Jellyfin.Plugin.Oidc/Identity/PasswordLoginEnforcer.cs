using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Oidc.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Applies the configured local credential policy without affecting Quick Connect sessions.</summary>
public static class PasswordLoginEnforcer
{
    private const string DefaultProvider = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";

    /// <summary>Updates the User authentication provider when the policy has changed.</summary>
    public static async Task EnforceAsync(IUserManager userManager, User user, PluginConfiguration configuration, ILogger logger)
    {
        var linked = configuration.IdentityLinks.Any(link => link.UserId == user.Id);
        var disabled = configuration.PasswordLoginMode == PasswordLoginMode.DisableForAllUsers
            || (configuration.PasswordLoginMode == PasswordLoginMode.DisableForLinkedUsersOnly && linked);
        var managed = string.Equals(user.AuthenticationProviderId, typeof(OidcPasswordDisabledProvider).FullName, StringComparison.Ordinal);
        if (disabled == managed)
        {
            return;
        }

        var policy = userManager.GetUserDto(user).Policy;
        policy.AuthenticationProviderId = disabled ? typeof(OidcPasswordDisabledProvider).FullName : DefaultProvider;
        await userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        logger.LogInformation("Updated local credential availability for a Jellyfin User.");
    }
}
