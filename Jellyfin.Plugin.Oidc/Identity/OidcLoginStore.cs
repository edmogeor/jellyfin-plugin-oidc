using System.Security.Cryptography;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Single-use server-side handoff tickets created after OIDC validation.</summary>
public sealed class OidcLoginStore
{
    private const int MaximumLogins = 1024;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Login> _logins = [];

    /// <summary>Creates an expiring login ticket for a Jellyfin User.</summary>
    public string? Create(Guid userId, string returnUrl)
    {
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
            _logins[ticket] = new Login(userId, returnUrl, now.AddMinutes(5));
            return ticket;
        }
    }

    /// <summary>Consumes a valid login ticket.</summary>
    public bool TryTake(string ticket, out Guid userId, out string returnUrl)
    {
        userId = Guid.Empty;
        returnUrl = "/";
        lock (_gate)
        {
            if (!_logins.Remove(ticket, out var login) || login.ExpiresAt < DateTimeOffset.UtcNow)
            {
                return false;
            }

            userId = login.UserId;
            returnUrl = login.ReturnUrl;
            return true;
        }
    }

    private sealed record Login(Guid UserId, string ReturnUrl, DateTimeOffset ExpiresAt);
}
