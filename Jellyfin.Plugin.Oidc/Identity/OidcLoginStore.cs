using System.Security.Cryptography;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Single-use server-side handoff tickets created after OIDC validation.</summary>
public sealed class OidcLoginStore
{
    private const int MaximumLogins = 1024;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Login> _logins = [];

    /// <summary>Creates an expiring login ticket for a Jellyfin User.</summary>
    public string? Create(Guid userId, string returnUrl, string browserNonce, bool rememberMe = true, long? revision = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(browserNonce);
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expiredTicket in _logins.Where(pair => pair.Value.ExpiresAt < now).Select(pair => pair.Key).ToArray())
            {
                _logins.Remove(expiredTicket);
            }
            if (_logins.Count >= MaximumLogins)
            {
                return null;
            }

            var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _logins[ticket] = new Login(userId, returnUrl, OidcSessionTracker.Fingerprint(browserNonce), rememberMe, now.AddMinutes(5), revision);
            return ticket;
        }
    }

    /// <summary>Consumes a valid login ticket.</summary>
    public bool TryTake(string ticket, string? browserNonce, out Guid userId, out string returnUrl, out bool rememberMe)
    {
        userId = Guid.Empty;
        returnUrl = "/";
        rememberMe = false;
        lock (_gate)
        {
            if (string.IsNullOrEmpty(browserNonce) || !_logins.TryGetValue(ticket, out var login)
                || login.BrowserFingerprint != OidcSessionTracker.Fingerprint(browserNonce)
                || login.ExpiresAt < DateTimeOffset.UtcNow
                || (login.Revision.HasValue && login.Revision != OidcPlugin.Instance?.Configuration.IdentityRevision))
            {
                return false;
            }

            _logins.Remove(ticket);
            userId = login.UserId;
            returnUrl = login.ReturnUrl;
            rememberMe = login.RememberMe;
            return true;
        }
    }

    /// <summary>Gets the browser-binding cookie name for an individual handoff.</summary>
    public static string CookieName(string ticket) => "oidc_handoff_" + ticket;

    private sealed record Login(Guid UserId, string ReturnUrl, string BrowserFingerprint, bool RememberMe, DateTimeOffset ExpiresAt, long? Revision);
}
