namespace Jellyfin.Plugin.Oidc.Configuration;

/// <summary>Validates the security-sensitive OIDC configuration.</summary>
public static class OidcConfigurationValidator
{
    /// <summary>Returns a configuration error, or <see langword="null"/> when valid.</summary>
    public static string? Validate(PluginConfiguration configuration)
    {
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

        if (!Enum.IsDefined(configuration.FirstSignInMatching) || !ProviderBrands.Names.ContainsKey(configuration.ProviderBrand))
        {
            return "Select a supported first-sign-in matching rule and provider brand.";
        }

        if (configuration.PasswordLoginMode == PasswordLoginMode.DisableForAllUsers
            && configuration.AdministratorGroup.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length == 0)
        {
            return "Disabling local credentials for all users requires an Administrator Group.";
        }

        return null;
    }

    private static bool IsHttpsUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
