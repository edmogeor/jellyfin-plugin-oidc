namespace Jellyfin.Plugin.Oidc.Configuration;

/// <summary>Durable administrator approval for the next validated sign-in, without expiry.</summary>
// Persisted fields are populated by Jellyfin's serializer.
// ReSharper disable PropertyCanBeMadeInitOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
public sealed class PendingLinkRequest
{
    /// <summary>Gets or sets the configured issuer.</summary>
    public string Issuer { get; set; } = string.Empty;
    /// <summary>Gets or sets the expected currently verified email.</summary>
    public string Email { get; set; } = string.Empty;
    /// <summary>Gets or sets the exact expected subject.</summary>
    public string Subject { get; set; } = string.Empty;
    /// <summary>Gets or sets the expected Identity Provider username.</summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>Gets or sets the username claim used when this approval was made.</summary>
    public string UsernameClaim { get; set; } = string.Empty;
    /// <summary>Gets or sets the approval creation time.</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>Server-owned authentication record evidence, never a bearer token.</summary>
public sealed class OidcSessionProvenance
{
    /// <summary>Gets or sets the authentication record ID.</summary>
    public int AuthenticationId { get; set; }
    /// <summary>Gets or sets the owning Jellyfin User.</summary>
    public Guid UserId { get; set; }
    /// <summary>Gets or sets a token fingerprint protecting against record ID reuse.</summary>
    public string TokenFingerprint { get; set; } = string.Empty;
}

/// <summary>Versioned recovery state for one Jellyfin User.</summary>
public sealed class OidcUserAdministration
{
    /// <summary>Gets or sets the target Jellyfin User.</summary>
    public Guid UserId { get; set; }
    /// <summary>Gets or sets the opaque mutation version.</summary>
    public Guid Version { get; set; }
    /// <summary>Gets or sets whether automatic matching is suppressed.</summary>
    public bool AutomaticReattachmentBlocked { get; set; }
    /// <summary>Gets or sets the active approval.</summary>
    public PendingLinkRequest? Request { get; set; }
    /// <summary>Gets or sets the outstanding unlink operation ID.</summary>
    public Guid UnlinkOperation { get; set; }
    /// <summary>Gets or sets the input version of the latest unlink, for safe duplicate requests.</summary>
    public Guid UnlinkVersion { get; set; }
    /// <summary>Gets or sets the exact records selected for revocation by that unlink.</summary>
    public List<OidcSessionProvenance> PendingRevocations { get; set; } = [];
}
// ReSharper restore PropertyCanBeMadeInitOnly.Global
// ReSharper restore UnusedAutoPropertyAccessor.Global
