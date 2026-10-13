using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Oidc.Configuration;

/// <summary>The configured Identity Provider product, used as display metadata.</summary>
public enum IdentityProviderType
{
    /// <summary>An unspecified or other Identity Provider.</summary>
    Other,
    /// <summary>Authentik.</summary>
    Authentik,
    /// <summary>Authelia.</summary>
    Authelia,
    /// <summary>Keycloak.</summary>
    Keycloak,
    /// <summary>Pocket ID.</summary>
    PocketId,
    /// <summary>ZITADEL.</summary>
    Zitadel,
}

/// <summary>Automatic attachment of an eligible identity to an existing Jellyfin User.</summary>
public enum AutomaticLinkingMode
{
    /// <summary>Match current verified email to a Jellyfin username.</summary>
    EmailToUsername,
    /// <summary>Require explicit linking for existing Jellyfin Users.</summary>
    None,
    /// <summary>Match the configured Identity Provider username claim to a Jellyfin username.</summary>
    UsernameToUsername,
}

/// <summary>Names assigned only when provisioning a new Jellyfin User.</summary>
public enum ProvisioningUsernameMode
{
    /// <summary>Use the configured username claim, falling back to verified email.</summary>
    UsernameClaim,
    /// <summary>Use only a currently verified email.</summary>
    VerifiedEmail,
}

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
// Persisted identity links are populated by Jellyfin's serializer.
// ReSharper disable PropertyCanBeMadeInitOnly.Global
public sealed class IdentityLink
{
    /// <summary>Gets or sets the OIDC issuer that owns the subject.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Gets or sets the OIDC subject.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin User ID.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the last supplied verified email, retained as optional metadata only.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets the last supplied Identity Provider username, used only for display.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the source URL of the synchronized profile image.</summary>
    public string ProfileImageUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the ETag returned for the synchronized profile image.</summary>
    public string ProfileImageETag { get; set; } = string.Empty;

    /// <summary>Gets or sets the last-modified time returned for the synchronized profile image.</summary>
    public DateTimeOffset? ProfileImageLastModified { get; set; }
}
// ReSharper restore PropertyCanBeMadeInitOnly.Global

/// <summary>Plugin configuration.</summary>
// Persisted configuration is populated by Jellyfin's serializer.
// ReSharper disable PropertyCanBeMadeInitOnly.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether OIDC routes and UI are active.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the selected Identity Provider product.</summary>
    public IdentityProviderType Provider { get; set; } = IdentityProviderType.Other;

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

    /// <summary>Gets or sets the top-level claim used for provisioning names and linked username display.</summary>
    public string UsernameClaim { get; set; } = "preferred_username";

    /// <summary>Gets or sets the automatic matching rule, preserving email matching by default.</summary>
    public AutomaticLinkingMode AutomaticLinkingMode { get; set; }

    /// <summary>Gets or sets whether users can link or unlink their Provider from their own profile.</summary>
    public bool AllowSelfServiceLinking { get; set; } = true;

    /// <summary>Gets or sets the source of new Jellyfin usernames.</summary>
    public ProvisioningUsernameMode ProvisioningUsernameMode { get; set; }

    /// <summary>Gets or sets optional space-separated OIDC scopes requested in addition to the standard scopes.</summary>
    public string AdditionalScopes { get; set; } = string.Empty;

    /// <summary>Gets or sets the group permitted standard Jellyfin User access.</summary>
    public string UserGroup { get; set; } = string.Empty;

    /// <summary>Gets or sets the group permitted administrator access.</summary>
    public string AdministratorGroup { get; set; } = string.Empty;

    /// <summary>Gets or sets whether linking and sign-in require a supplied verified email.</summary>
    public bool RequireVerifiedEmail { get; set; } = true;

    /// <summary>Gets or sets the display name for an Other Identity Provider.</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Gets or sets local credential availability.</summary>
    public PasswordLoginMode PasswordLoginMode { get; set; }

    /// <summary>Gets or sets whether the Jellyfin sign-in page redirects directly to the Identity Provider.</summary>
    public bool RedirectSignInPageToProvider { get; set; }

    /// <summary>Gets or sets a value indicating whether RP-initiated logout is requested when available.</summary>
    public bool RpInitiatedLogout { get; set; }

    /// <summary>Gets or sets a value indicating whether profile images are synchronized from the OIDC picture claim.</summary>
    public bool SynchronizeProfileImages { get; set; }

    /// <summary>Gets or sets Identity Links owned by this plugin.</summary>
    public List<IdentityLink> IdentityLinks { get; set; } = [];

    /// <summary>Gets or sets versioned approvals, suppression, and recovery state.</summary>
    public List<OidcUserAdministration> UserAdministration { get; set; } = [];

    /// <summary>Gets or sets positively identified OIDC authentication records.</summary>
    public List<OidcSessionProvenance> OidcSessions { get; set; } = [];

    /// <summary>Gets or sets the revision invalidating callbacks predating administrator mutations.</summary>
    public long IdentityRevision { get; set; }
}
