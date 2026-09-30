using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Oidc.Configuration;

/// <summary>The claim trusted for an identity's first sign-in.</summary>
public enum FirstSignInMatching
{
    /// <summary>Require a verified email.</summary>
    VerifiedEmail,
    /// <summary>Trust a provider-managed preferred username.</summary>
    PreferredUsername,
}

/// <summary>The durable username synchronization policy of a link.</summary>
public enum IdentityLinkOrigin
{
    /// <summary>Legacy and email-matched links follow verified email changes.</summary>
    EmailMatch,
    /// <summary>Preferred username matches preserve the local username.</summary>
    PreferredUsernameMatch,
    /// <summary>Explicit links preserve the local username.</summary>
    Explicit,
}

/// <summary>An explicitly unlinked identity and its reserved Jellyfin User.</summary>
public sealed record UnlinkOptOut(string Issuer, string Subject, Guid UserId)
{
    /// <summary>Initializes a record for XML serialization.</summary>
    // ReSharper disable once UnusedMember.Global
    public UnlinkOptOut() : this(string.Empty, string.Empty, Guid.Empty) { }
}

/// <summary>A single-use administrator-directed claim match.</summary>
public sealed record PendingAdminMatch(string Issuer, Guid UserId, FirstSignInMatching Claim, string Value)
{
    /// <summary>Initializes a record for XML serialization.</summary>
    // ReSharper disable once UnusedMember.Global
    public PendingAdminMatch() : this(string.Empty, Guid.Empty, FirstSignInMatching.VerifiedEmail, string.Empty) { }
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

    /// <summary>Gets or sets the link's username policy.</summary>
    public IdentityLinkOrigin Origin { get; set; }

    /// <summary>Gets or sets the last verified email observed at eligible authentication.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets the last preferred username observed at eligible authentication.</summary>
    public string PreferredUsername { get; set; } = string.Empty;

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
    public string LoginButtonText { get; set; } = string.Empty;

    /// <summary>Gets or sets the selected presentation-only provider brand.</summary>
    public string ProviderBrand { get; set; } = "Other";

    /// <summary>Gets or sets the first-sign-in matching rule.</summary>
    public FirstSignInMatching FirstSignInMatching { get; set; }

    /// <summary>Gets or sets whether Jellyfin Users may manage their own Identity Link.</summary>
    public bool AllowSelfServiceIdentityLinks { get; set; } = true;

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

    /// <summary>Gets or sets explicitly unlinked identities.</summary>
    public List<UnlinkOptOut> UnlinkOptOuts { get; set; } = [];

    /// <summary>Gets or sets single-use administrator-directed matches.</summary>
    public List<PendingAdminMatch> PendingAdminMatches { get; set; } = [];

    /// <summary>Gets or sets the revision used to reject stale identity-management writes.</summary>
    public long IdentityRevision { get; set; }
}
