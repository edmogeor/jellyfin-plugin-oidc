using Jellyfin.Plugin.Oidc.Configuration;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Shared ownership checks for every Identity Link entry point.</summary>
public static class IdentityRules
{
    /// <summary>Gets the normalized configured issuer.</summary>
    public static string Issuer(PluginConfiguration configuration) => configuration.IssuerUrl.TrimEnd('/');

    /// <summary>Rejects corrupt or ambiguous identity state before resolving it.</summary>
    public static void Validate(PluginConfiguration configuration)
    {
        var issuer = Issuer(configuration);
        var links = configuration.IdentityLinks.Where(link => link.Issuer == issuer).ToArray();
        if (links.Any(link => string.IsNullOrWhiteSpace(link.Subject) || link.UserId == Guid.Empty || !Enum.IsDefined(link.Origin))
            || links.GroupBy(link => link.Subject).Any(group => group.Count() > 1)
            || links.GroupBy(link => link.UserId).Any(group => group.Count() > 1)
            || configuration.UnlinkOptOuts.Where(item => item.Issuer == issuer).GroupBy(item => item.Subject).Any(group => group.Count() > 1)
            || configuration.PendingAdminMatches.Where(item => item.Issuer == issuer).GroupBy(item => item.UserId).Any(group => group.Count() > 1)
            || configuration.PendingAdminMatches.Where(item => item.Issuer == issuer).GroupBy(item => (item.Claim, item.Value.ToUpperInvariant())).Any(group => group.Count() > 1)
            || links.Any(link => configuration.UnlinkOptOuts.Any(item => item.Issuer == issuer && item.Subject == link.Subject)))
        {
            throw new InvalidOperationException("Conflicting identity records require administrator resolution.");
        }
    }

    /// <summary>Checks whether a target may receive the identity, including explicit relinking.</summary>
    public static void EnsureAvailable(PluginConfiguration configuration, string subject, Guid userId, bool explicitLink)
    {
        Validate(configuration);
        var issuer = Issuer(configuration);
        if (configuration.IdentityLinks.Any(link => link.Issuer == issuer && ((link.Subject == subject && link.UserId != userId) || (link.UserId == userId && link.Subject != subject)))
            || configuration.UnlinkOptOuts.Any(item => item.Issuer == issuer && (item.Subject == subject || item.UserId == userId) && (!explicitLink || item.Subject != subject || item.UserId != userId))
            || (!explicitLink && configuration.PendingAdminMatches.Any(item => item.Issuer == issuer && item.UserId == userId)))
        {
            throw new InvalidOperationException("This identity or Jellyfin User is already linked or reserved. Refresh and resolve the conflict.");
        }
    }

    /// <summary>Gets exactly one matching pending directive, or rejects an ambiguous claim.</summary>
    public static PendingAdminMatch? PendingMatch(PluginConfiguration configuration, OidcIdentity identity)
    {
        var matches = configuration.PendingAdminMatches.Where(item => item.Issuer == Issuer(configuration)
            && !string.IsNullOrWhiteSpace(identity.MatchingKey(item.Claim))
            && string.Equals(item.Value, identity.MatchingKey(item.Claim), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Ambiguous pending identity matches.");
        return matches.SingleOrDefault();
    }
}
