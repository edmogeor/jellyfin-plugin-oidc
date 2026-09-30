using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Bounded, single-use link intents and validated completion tickets.</summary>
public sealed class OidcLinkStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkIntent> _tickets = [];

    /// <summary>Creates a five-minute intent or completion ticket.</summary>
    public string? Create(LinkIntent intent)
    {
        lock (_gate)
        {
            foreach (var key in _tickets.Where(pair => pair.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray()) _tickets.Remove(key);
            if (_tickets.Count >= 1024) return null;
            var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _tickets[ticket] = intent;
            return ticket;
        }
    }

    /// <summary>Reads a ticket, optionally consuming it, without accepting expired state.</summary>
    public bool TryGet(string ticket, bool consume, [NotNullWhen(true)] out LinkIntent? intent)
    {
        lock (_gate)
        {
            if (!_tickets.TryGetValue(ticket, out intent)) return false;
            if (consume || intent.ExpiresAt <= DateTimeOffset.UtcNow) _tickets.Remove(ticket);
            if (intent.ExpiresAt > DateTimeOffset.UtcNow) return true;
            intent = null;
            return false;
        }
    }

    /// <summary>Server-side intent bound to the original authenticated Jellyfin session.</summary>
    public sealed record LinkIntent(Guid OwnerId, Guid TargetId, string Session, bool Administrator, string Issuer,
        string ReturnUrl, long Revision, DateTimeOffset ExpiresAt, ClaimsPrincipal? Principal = null);
}
