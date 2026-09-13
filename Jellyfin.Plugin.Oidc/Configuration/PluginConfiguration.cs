using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Oidc.Configuration;

/// <summary>Local credential availability after OIDC is configured.</summary>
public enum PasswordLoginMode
{
    /// <summary>All Jellyfin Users can use local credentials.</summary>
    AllowForAllUsers,

    /// <summary>Only Jellyfin Users with an Identity Link cannot use local credentials.</summary>
    DisableForLinkedUsersOnly,

    /// <summary>No Jellyfin User can use local credentials.</summary>
    DisableForAllUsers,
}

/// <summary>A durable binding between an Identity Provider subject and a Jellyfin User.</summary>
public sealed class IdentityLink
{
    /// <summary>Gets or sets the OIDC issuer that owns the subject.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Gets or sets the OIDC subject.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin User ID.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the source URL of the synchronized profile image.</summary>
    public string ProfileImageUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the ETag returned for the synchronized profile image.</summary>
    public string ProfileImageETag { get; set; } = string.Empty;

    /// <summary>Gets or sets the last-modified time returned for the synchronized profile image.</summary>
    public DateTimeOffset? ProfileImageLastModified { get; set; }
}

/// <summary>Plugin configuration.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether OIDC routes and UI are active.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the fixed public HTTPS URL of Jellyfin.</summary>
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the HTTPS issuer URL used for discovery.</summary>
    public string IssuerUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the confidential client ID.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Gets or sets the confidential client secret.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Gets or sets the top-level claim containing group values.</summary>
    public string GroupClaim { get; set; } = "groups";

    /// <summary>Gets or sets optional space-separated OIDC scopes requested in addition to the standard scopes.</summary>
    public string AdditionalScopes { get; set; } = string.Empty;

    /// <summary>Gets or sets the group permitted standard Jellyfin User access.</summary>
    public string UserGroup { get; set; } = string.Empty;

    /// <summary>Gets or sets the group permitted administrator access.</summary>
    public string AdministratorGroup { get; set; } = string.Empty;

    /// <summary>Gets or sets the OIDC button label.</summary>
    public string LoginButtonText { get; set; } = "Login with SSO";

    /// <summary>Gets or sets local credential availability.</summary>
    public PasswordLoginMode PasswordLoginMode { get; set; }

    /// <summary>Gets or sets a value indicating whether RP-initiated logout is requested when available.</summary>
    public bool RpInitiatedLogout { get; set; }

    /// <summary>Gets or sets a value indicating whether profile images are synchronized from the OIDC picture claim.</summary>
    public bool SynchronizeProfileImages { get; set; }

    /// <summary>Gets or sets Identity Links owned by this plugin.</summary>
    public List<IdentityLink> IdentityLinks { get; set; } = [];
}
