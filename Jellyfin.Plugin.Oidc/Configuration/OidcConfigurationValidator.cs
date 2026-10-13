namespace Jellyfin.Plugin.Oidc.Configuration;

/// <summary>Validates the security-sensitive OIDC configuration.</summary>
public static class OidcConfigurationValidator
{
    /// <summary>Returns a configuration error, or <see langword="null"/> when valid.</summary>
    public static string? Validate(PluginConfiguration configuration)
    {
        if (!Enum.IsDefined(configuration.AutomaticLinkingMode))
        {
            return "Select a supported automatic linking mode.";
        }
        if (!Enum.IsDefined(configuration.ProvisioningUsernameMode))
        {
            return "Select a supported new-user username type.";
        }
        if (!Enum.IsDefined(configuration.PasswordLoginMode))
        {
            return "Select a supported local password sign-in mode.";
        }
        if (!configuration.Enabled)
        {
            return null;
        }

        if ((!string.IsNullOrWhiteSpace(configuration.PublicUrl) && !IsHttpsUrl(configuration.PublicUrl))
            || !IsHttpsUrl(configuration.IssuerUrl))
        {
            return "Issuer URL and an optional Public Jellyfin URL override must be absolute HTTPS URLs.";
        }

        if (string.IsNullOrWhiteSpace(configuration.ClientId) || string.IsNullOrWhiteSpace(configuration.ClientSecret))
        {
            return "Client ID and client secret are required.";
        }

        if (string.IsNullOrWhiteSpace(configuration.GroupClaim))
        {
            return "Group claim is required. Use a top-level claim such as groups.";
        }
        if (string.IsNullOrWhiteSpace(configuration.UsernameClaim))
        {
            return "Username claim is required. Use a top-level string claim such as preferred_username.";
        }

        if (!HasGroups(configuration.UserGroup) && !HasGroups(configuration.AdministratorGroup))
        {
            return "Configure a User Group, an Administrator Group, or both.";
        }

        if (configuration.PasswordLoginMode == PasswordLoginMode.DisableForAllUsers
            && !HasGroups(configuration.AdministratorGroup))
        {
            return "Disabling local credentials for all users requires an Administrator Group.";
        }

        return null;
    }

    private static bool IsHttpsUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(uri.Host) && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static bool HasGroups(string value)
        => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length > 0;
}
