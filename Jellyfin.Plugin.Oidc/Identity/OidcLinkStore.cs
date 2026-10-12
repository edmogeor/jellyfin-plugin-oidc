using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Oidc.Configuration;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Bounded, short-lived linking transactions tied to the initiating Jellyfin session.</summary>
public sealed class OidcLinkStore(TimeProvider clock)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Transaction> _transactions = [];

    /// <summary>Creates a linking request without retaining the session token.</summary>
    public string? Create(Guid userId, string token, string returnUrl, PluginConfiguration configuration)
    {
        lock (_gate)
        {
            foreach (var key in _transactions.Where(pair => pair.Value.ExpiresAt <= clock.GetUtcNow()).Select(pair => pair.Key).ToArray())
            {
                _transactions.Remove(key);
            }
            if (_transactions.Count >= 1024)
            {
                return null;
            }
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _transactions.Add(id, new Transaction(userId, Fingerprint(token), returnUrl, configuration, clock.GetUtcNow().AddMinutes(5)));
            return id;
        }
    }

    /// <summary>Consumes the navigation ticket and creates a separate callback transaction.</summary>
    public string? Start(string id, PluginConfiguration configuration, out string returnUrl)
    {
        returnUrl = "/userprofile";
        lock (_gate)
        {
            if (!_transactions.Remove(id, out var transaction) || transaction.ExpiresAt <= clock.GetUtcNow()
                || transaction.Started || !ReferenceEquals(configuration, transaction.Configuration))
            {
                return null;
            }
            returnUrl = transaction.ReturnUrl;
            var callbackId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _transactions.Add(callbackId, transaction with { Started = true });
            return callbackId;
        }
    }

    /// <summary>Consumes the callback transaction and creates a single-use authenticated completion ticket.</summary>
    public string? Validate(string id, ClaimsPrincipal principal, PluginConfiguration configuration)
    {
        lock (_gate)
        {
            if (!_transactions.Remove(id, out var transaction) || transaction.ExpiresAt <= clock.GetUtcNow()
                || !transaction.Started || transaction.Principal is not null
                || !ReferenceEquals(configuration, transaction.Configuration)
                || !IdentityClaims.TryCreate(principal, configuration, out _))
            {
                return null;
            }
            var completionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _transactions.Add(completionId, transaction with { Principal = principal });
            return completionId;
        }
    }

    /// <summary>Consumes a completion ticket only for the original authenticated session.</summary>
    public bool TryComplete(string id, Guid userId, string token, out ClaimsPrincipal? principal, out PluginConfiguration? configuration)
    {
        principal = null;
        configuration = null;
        lock (_gate)
        {
            if (!_transactions.Remove(id, out var transaction) || transaction.ExpiresAt <= clock.GetUtcNow()
                || transaction.Principal is null || transaction.UserId != userId || transaction.TokenFingerprint != Fingerprint(token))
            {
                return false;
            }
            principal = transaction.Principal;
            configuration = transaction.Configuration;
            return true;
        }
    }

    private static string Fingerprint(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed record Transaction(Guid UserId, string TokenFingerprint, string ReturnUrl, PluginConfiguration Configuration, DateTimeOffset ExpiresAt, bool Started = false, ClaimsPrincipal? Principal = null);
}
