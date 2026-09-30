using System.Security.Claims;
using System.Security.Cryptography;
using Jellyfin.Plugin.Oidc.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Cryptography;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Resolves and manages Identity Links under the configuration write gate.</summary>
public sealed class OidcUserProvisioner
{
    private readonly ICryptoProvider _cryptoProvider;
    private readonly ILogger<OidcUserProvisioner> _logger;
    private readonly OidcProfileImageSynchronizer _profileImageSynchronizer;
    private readonly IUserManager _userManager;

    /// <summary>Initializes a new instance of the <see cref="OidcUserProvisioner"/> class.</summary>
    public OidcUserProvisioner(IUserManager userManager, ICryptoProvider cryptoProvider, OidcProfileImageSynchronizer profileImageSynchronizer, ILogger<OidcUserProvisioner> logger)
    {
        _userManager = userManager;
        _cryptoProvider = cryptoProvider;
        _profileImageSynchronizer = profileImageSynchronizer;
        _logger = logger;
    }

    /// <summary>Returns the eligible Jellyfin User ID, or null when denied.</summary>
    public async Task<Guid?> ProvisionAsync(ClaimsPrincipal principal, string authenticatedIssuer)
    {
        await OidcPlugin.IdentityGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var plugin = OidcPlugin.Instance!;
            var configuration = plugin.Snapshot();
            if (!configuration.Enabled || authenticatedIssuer != IdentityRules.Issuer(configuration) || !IdentityClaims.TryCreate(principal, configuration, out var identity)) return null;
            BindLegacyLinks(configuration);
            IdentityRules.Validate(configuration);
            var issuer = IdentityRules.Issuer(configuration);
            var link = configuration.IdentityLinks.SingleOrDefault(item => item.Subject == identity.Subject && item.Issuer == issuer);
            var user = link is null ? null : _userManager.GetUserById(link.UserId);
            if (link is not null && user is null)
            {
                configuration.IdentityLinks.Remove(link);
                plugin.Commit(configuration);
                link = null;
            }

            if (link is null)
            {
                var pending = IdentityRules.PendingMatch(configuration, identity);
                if (pending is null && configuration.UnlinkOptOuts.Any(item => item.Issuer == issuer && item.Subject == identity.Subject)) return null;
                var key = identity.MatchingKey(configuration.FirstSignInMatching);
                if (pending is not null)
                {
                    user = _userManager.GetUserById(pending.UserId);
                    if (user is null) return null;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(key)) return null;
                    var matches = _userManager.GetUsers().Where(candidate => string.Equals(candidate.Username, key, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length > 1) return null;
                    user = matches.SingleOrDefault();
                }

                if (user is not null) IdentityRules.EnsureAvailable(configuration, identity.Subject, user.Id, pending is not null);
                else
                {
                    user = await _userManager.CreateUserAsync(key).ConfigureAwait(false);
                    user.Password = _cryptoProvider.CreatePasswordHash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))).ToString();
                    await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
                }

                link = AddLink(configuration, identity, user.Id, pending is not null ? IdentityLinkOrigin.Explicit
                    : configuration.FirstSignInMatching == FirstSignInMatching.VerifiedEmail ? IdentityLinkOrigin.EmailMatch : IdentityLinkOrigin.PreferredUsernameMatch);
            }
            else if (link.Origin == IdentityLinkOrigin.EmailMatch && !string.IsNullOrWhiteSpace(identity.Email)
                && !string.Equals(user!.Username, identity.Email, StringComparison.OrdinalIgnoreCase))
            {
                if (_userManager.GetUsers().Any(candidate => candidate.Id != user.Id && string.Equals(candidate.Username, identity.Email, StringComparison.OrdinalIgnoreCase))) return null;
                await _userManager.RenameUser(user.Id, user.Username, identity.Email).ConfigureAwait(false);
                user = _userManager.GetUserById(user.Id);
            }

            if (user is null) return null;
            UpdateMetadata(link, identity);
            var policy = _userManager.GetUserDto(user).Policy;
            if (policy.IsAdministrator != identity.IsAdministrator)
            {
                policy.IsAdministrator = identity.IsAdministrator;
                await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
            }

            if (configuration.SynchronizeProfileImages)
                await _profileImageSynchronizer.SynchronizeAsync(user, link, principal.FindFirst("picture")?.Value, issuer).ConfigureAwait(false);
            configuration.IdentityRevision++;
            plugin.Commit(configuration);
            await PasswordLoginEnforcer.EnforceAsync(_userManager, user, configuration, _logger).ConfigureAwait(false);
            return user.Id;
        }
        catch (Exception)
        {
            _logger.LogWarning("OIDC sign-in denied because identity resolution or persistence failed.");
            return null;
        }
        finally { OidcPlugin.IdentityGate.Release(); }
    }

    /// <summary>Reads owner/admin identity status without provider credentials.</summary>
    public async Task<IdentityStatus> StatusAsync(Guid userId, bool administrator)
    {
        await OidcPlugin.IdentityGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var configuration = OidcPlugin.Instance!.Configuration;
            var user = _userManager.GetUserById(userId) ?? throw new KeyNotFoundException("The Jellyfin User was deleted.");
            var issuer = IdentityRules.Issuer(configuration);
            var links = configuration.IdentityLinks.Where(item => item.UserId == userId && (item.Issuer == issuer || string.IsNullOrEmpty(item.Issuer))).ToArray();
            if (links.Length > 1) throw new InvalidOperationException("Conflicting Identity Links require administrator resolution.");
            var link = links.SingleOrDefault();
            var pending = administrator ? configuration.PendingAdminMatches.SingleOrDefault(item => item.Issuer == issuer && item.UserId == userId) : null;
            return new IdentityStatus(userId, user.Username, link is not null, link?.Email ?? "", link?.PreferredUsername ?? "",
                administrator ? link?.Subject : null, administrator ? link?.Issuer : null, administrator ? link?.Origin.ToString() : null,
                pending, configuration.IdentityRevision, configuration.AllowSelfServiceIdentityLinks,
                administrator || (configuration.AllowSelfServiceIdentityLinks && configuration.PasswordLoginMode != PasswordLoginMode.DisableForAllUsers),
                administrator && configuration.UnlinkOptOuts.Any(item => item.Issuer == issuer && item.UserId == userId));
        }
        finally { OidcPlugin.IdentityGate.Release(); }
    }

    /// <summary>Creates a validated explicit link without signing in or changing administrator status.</summary>
    public Task LinkAsync(Guid userId, ClaimsPrincipal principal, bool administrator, string issuer, long revision) => ChangeAsync(userId, revision, administrator, configuration =>
    {
        if (issuer != IdentityRules.Issuer(configuration) || !IdentityClaims.TryCreate(principal, configuration, out var identity))
            throw new InvalidOperationException("The Identity Provider or group admission changed. Start linking again.");
        BindLegacyLinks(configuration);
        IdentityRules.EnsureAvailable(configuration, identity.Subject, userId, true);
        var link = configuration.IdentityLinks.SingleOrDefault(item => item.Issuer == issuer && item.Subject == identity.Subject);
        link ??= AddLink(configuration, identity, userId, IdentityLinkOrigin.Explicit);
        UpdateMetadata(link, identity);
        return Task.CompletedTask;
    });

    /// <summary>Explicitly unlinks a Jellyfin User and prevents automatic relinking.</summary>
    public Task UnlinkAsync(Guid userId, bool administrator, long revision) => ChangeAsync(userId, revision, administrator, configuration =>
    {
        if (!administrator && configuration.PasswordLoginMode == PasswordLoginMode.DisableForAllUsers)
            throw new InvalidOperationException("Self-service unlink is unavailable while all local passwords are disabled.");
        BindLegacyLinks(configuration);
        IdentityRules.Validate(configuration);
        var issuer = IdentityRules.Issuer(configuration);
        var link = configuration.IdentityLinks.SingleOrDefault(item => item.Issuer == issuer && item.UserId == userId);
        if (link is not null)
        {
            configuration.IdentityLinks.Remove(link);
            configuration.UnlinkOptOuts.Add(new UnlinkOptOut(issuer, link.Subject, userId));
        }
        return Task.CompletedTask;
    });

    /// <summary>Adds, replaces, or cancels a pending administrator-directed match.</summary>
    public Task PendingAsync(Guid userId, FirstSignInMatching claim, string value, long revision) => ChangeAsync(userId, revision, true, configuration =>
    {
        if (!Enum.IsDefined(claim) || value.Length > 320) throw new InvalidOperationException("Select a valid claim and value.");
        BindLegacyLinks(configuration);
        IdentityRules.Validate(configuration);
        var issuer = IdentityRules.Issuer(configuration);
        if (configuration.IdentityLinks.Any(item => item.Issuer == issuer && item.UserId == userId)) throw new InvalidOperationException("Unlink explicitly before entering an expected identity.");
        if (configuration.PendingAdminMatches.Any(item => item.Issuer == issuer && item.UserId != userId && item.Claim == claim && string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("That expected identity is already reserved for another Jellyfin User.");
        configuration.PendingAdminMatches.RemoveAll(item => item.Issuer == issuer && item.UserId == userId);
        if (!string.IsNullOrWhiteSpace(value)) configuration.PendingAdminMatches.Add(new PendingAdminMatch(issuer, userId, claim, value));
        return Task.CompletedTask;
    });

    private async Task ChangeAsync(Guid userId, long revision, bool administrator, Func<PluginConfiguration, Task> change)
    {
        await OidcPlugin.IdentityGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var plugin = OidcPlugin.Instance!;
            var configuration = plugin.Snapshot();
            if (!configuration.Enabled || (!administrator && !configuration.AllowSelfServiceIdentityLinks)) throw new InvalidOperationException("Self-service Identity Links are disabled.");
            if (revision != configuration.IdentityRevision) throw new InvalidOperationException("Identity state changed. Refresh and try again.");
            var user = _userManager.GetUserById(userId) ?? throw new KeyNotFoundException("The Jellyfin User was deleted.");
            await change(configuration).ConfigureAwait(false);
            configuration.IdentityRevision++;
            plugin.Commit(configuration);
            await PasswordLoginEnforcer.EnforceAsync(_userManager, user, configuration, _logger).ConfigureAwait(false);
        }
        finally { OidcPlugin.IdentityGate.Release(); }
    }

    private static void BindLegacyLinks(PluginConfiguration configuration)
    {
        foreach (var link in configuration.IdentityLinks.Where(item => string.IsNullOrEmpty(item.Issuer))) link.Issuer = IdentityRules.Issuer(configuration);
    }

    private static IdentityLink AddLink(PluginConfiguration configuration, OidcIdentity identity, Guid userId, IdentityLinkOrigin origin)
    {
        var issuer = IdentityRules.Issuer(configuration);
        var link = new IdentityLink { Issuer = issuer, Subject = identity.Subject, UserId = userId, Origin = origin };
        configuration.IdentityLinks.Add(link);
        configuration.PendingAdminMatches.RemoveAll(item => item.Issuer == issuer && item.UserId == userId);
        configuration.UnlinkOptOuts.RemoveAll(item => item.Issuer == issuer && item.Subject == identity.Subject && item.UserId == userId);
        return link;
    }

    private static void UpdateMetadata(IdentityLink link, OidcIdentity identity)
    {
        link.Email = identity.Email;
        link.PreferredUsername = identity.PreferredUsername;
    }

    /// <summary>Scoped identity status for an authenticated owner or administrator.</summary>
    // These fields are consumed by Jellyfin Web through JSON serialization.
    // ReSharper disable NotAccessedPositionalProperty.Global
    public sealed record IdentityStatus(Guid UserId, string UserName, bool Linked, string Email, string PreferredUsername,
        string? Subject, string? Issuer, string? Origin, PendingAdminMatch? PendingMatch, long Revision, bool AllowSelfService,
        bool CanUnlink, bool HasOptOut);
    // ReSharper restore NotAccessedPositionalProperty.Global
}
