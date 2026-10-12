using Jellyfin.Plugin.Oidc.Configuration;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Shared approval and reservation rules, evaluated inside the configuration gate.</summary>
public static class AdministrationRules
{
    /// <summary>Accepts the current version or a harmless duplicate of the latest unlink.</summary>
    public static bool CanUnlink(PluginConfiguration configuration, OidcUserAdministration state, Guid version)
        => state.Version == version || (state.UnlinkVersion == version && state.Version == state.UnlinkOperation
            && configuration.IdentityLinks.All(link => link.UserId != state.UserId));

    /// <summary>Returns the current opaque version, including for pre-migration users.</summary>
    public static Guid Version(PluginConfiguration configuration, Guid userId)
        => configuration.UserAdministration.SingleOrDefault(item => item.UserId == userId)?.Version ?? Guid.Empty;

    /// <summary>Gets or creates durable state for a target.</summary>
    public static OidcUserAdministration State(PluginConfiguration configuration, Guid userId)
    {
        var state = configuration.UserAdministration.SingleOrDefault(item => item.UserId == userId);
        if (state is not null) return state;
        state = new OidcUserAdministration { UserId = userId };
        configuration.UserAdministration.Add(state);
        return state;
    }

    /// <summary>Matches the approved criterion using only current validated identity data.</summary>
    public static bool Matches(PendingLinkRequest request, string issuer, string subject, string email, string username = "", string usernameClaim = "")
        => request.Issuer == issuer
            && (request.Subject.Length == 0 || request.Subject == subject)
            && (request.Email.Length == 0 || string.Equals(request.Email, email, StringComparison.OrdinalIgnoreCase))
            && (request.Username.Length == 0 || (request.UsernameClaim == usernameClaim && string.Equals(request.Username, username, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Returns whether an identity touches a reserved criterion.</summary>
    public static bool Reserved(PendingLinkRequest request, string issuer, string subject, string email, string username = "")
        => request.Issuer == issuer && ((request.Subject.Length > 0 && request.Subject == subject)
            || (request.Email.Length > 0 && string.Equals(request.Email, email, StringComparison.OrdinalIgnoreCase))
            || (request.Username.Length > 0 && string.Equals(request.Username, username, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Rejects duplicate reservations.</summary>
    public static bool Overlaps(PendingLinkRequest first, PendingLinkRequest second)
        => first.Issuer == second.Issuer && ((first.Subject.Length > 0 && first.Subject == second.Subject)
            || (first.Email.Length > 0 && string.Equals(first.Email, second.Email, StringComparison.OrdinalIgnoreCase))
            || (first.Username.Length > 0 && string.Equals(first.Username, second.Username, StringComparison.OrdinalIgnoreCase)));
}
