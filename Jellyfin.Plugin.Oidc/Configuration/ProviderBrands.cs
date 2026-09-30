namespace Jellyfin.Plugin.Oidc.Configuration;

/// <summary>The explicitly selected, presentation-only Identity Provider catalog.</summary>
public static class ProviderBrands
{
    /// <summary>Gets supported brand identifiers and display names.</summary>
    public static IReadOnlyDictionary<string, string> Names { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Other"] = "SSO",
        ["auth0"] = "Auth0",
        ["authentik"] = "Authentik",
        ["authelia"] = "Authelia",
        ["fusionauth"] = "FusionAuth",
        ["google"] = "Google",
        ["keycloak"] = "Keycloak",
        ["microsoft-entra-id"] = "Microsoft Entra ID",
        ["okta"] = "Okta",
        ["pocket-id"] = "Pocket ID",
        ["zitadel"] = "ZITADEL",
    };

    /// <summary>Gets the configured custom label or the generated brand label.</summary>
    public static string LoginLabel(PluginConfiguration configuration) => string.IsNullOrWhiteSpace(configuration.LoginButtonText)
        ? "Sign in with " + Names[configuration.ProviderBrand] : configuration.LoginButtonText;
}
