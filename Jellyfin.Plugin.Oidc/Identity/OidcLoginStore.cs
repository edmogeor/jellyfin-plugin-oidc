using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Single-use server-side handoff tickets created after OIDC validation.</summary>
public sealed class OidcLoginStore
{
    private readonly ConcurrentDictionary<string, Login> _logins = new();

    /// <summary>Creates an expiring login ticket for a Jellyfin User.</summary>
    public string Create(Guid userId, string returnUrl)
    {
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _logins[ticket] = new Login(userId, returnUrl, DateTimeOffset.UtcNow.AddMinutes(5));
        return ticket;
    }

    /// <summary>Consumes a valid login ticket.</summary>
    public bool TryTake(string ticket, out Guid userId, out string returnUrl)
    {
        userId = Guid.Empty;
        returnUrl = "/";
        if (!_logins.TryRemove(ticket, out var login) || login.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return false;
        }

        userId = login.UserId;
        returnUrl = login.ReturnUrl;
        return true;
    }

    private sealed record Login(Guid UserId, string ReturnUrl, DateTimeOffset ExpiresAt);
}
