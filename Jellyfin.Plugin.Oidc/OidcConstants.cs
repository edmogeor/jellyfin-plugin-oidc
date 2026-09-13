namespace Jellyfin.Plugin.Oidc;

/// <summary>Shared OIDC route, cookie, and data-protection names.</summary>
public static class OidcConstants
{
    /// <summary>The OIDC callback path.</summary>
    public const string CallbackPath = "/oidc/callback";

    /// <summary>The OIDC logout cookie name.</summary>
    public const string LogoutCookieName = "oidc_logout";

    /// <summary>The data-protection purpose for a protected OIDC ID token.</summary>
    public const string LogoutTokenProtectorPurpose = "Jellyfin.Plugin.Oidc.LogoutIdToken.v1";

    /// <summary>The Jellyfin Web index path.</summary>
    public const string WebIndexPath = "/web/index.html";
}
