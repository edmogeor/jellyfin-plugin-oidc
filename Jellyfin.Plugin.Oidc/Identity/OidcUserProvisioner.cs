using System.Security.Claims;
using System.Security.Cryptography;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Oidc.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Cryptography;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Matches, provisions, and synchronizes Jellyfin Users after token validation.</summary>
public sealed class OidcUserProvisioner
{
    private readonly ICryptoProvider _cryptoProvider;
    private readonly ILogger<OidcUserProvisioner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
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

    /// <summary>Returns the eligible Jellyfin User ID, or <see langword="null"/> when denied.</summary>
    public async Task<Guid?> ProvisionAsync(ClaimsPrincipal principal)
    {
        var configuration = OidcPlugin.Instance?.Configuration;
        if (configuration is null || !configuration.Enabled || !IdentityClaims.TryCreate(principal, configuration, out var identity))
        {
            _logger.LogWarning("OIDC sign-in denied because required identity claims or allowed groups were absent.");
            return null;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var issuer = configuration.IssuerUrl.TrimEnd('/');
            var link = configuration.IdentityLinks.SingleOrDefault(item => item.Subject == identity.Subject && (item.Issuer == issuer || string.IsNullOrEmpty(item.Issuer)));
            var user = link is null ? null : _userManager.GetUserById(link.UserId);
            if (link is not null && user is null)
            {
                configuration.IdentityLinks.Remove(link);
            }
            else if (link is not null && string.IsNullOrEmpty(link.Issuer))
            {
                link.Issuer = issuer;
            }

            var matches = _userManager.GetUsers()
                .Where(candidate => string.Equals(candidate.Username, identity.Email, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (user is null && matches.Count > 1)
            {
                _logger.LogWarning("OIDC sign-in denied because the verified email matched multiple Jellyfin Users.");
                return null;
            }

            user ??= matches.SingleOrDefault();
            if (user is null)
            {
                user = await _userManager.CreateUserAsync(identity.Email).ConfigureAwait(false);
                user.Password = _cryptoProvider.CreatePasswordHash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))).ToString();
                await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
                _logger.LogInformation("Provisioned Jellyfin User for an eligible OIDC identity.");
            }
            else if (!string.Equals(user.Username, identity.Email, StringComparison.OrdinalIgnoreCase))
            {
                if (matches.Any(candidate => candidate.Id != user.Id))
                {
                    _logger.LogWarning("OIDC sign-in denied because the changed email is already used by another Jellyfin User.");
                    return null;
                }

                await _userManager.RenameUser(user.Id, user.Username, identity.Email).ConfigureAwait(false);
                user = _userManager.GetUserById(user.Id);
                _logger.LogInformation("Updated a linked Jellyfin User username after a verified OIDC email change.");
            }

            if (user is null)
            {
                return null;
            }

            if (link is null)
            {
                link = new IdentityLink { Issuer = issuer, Subject = identity.Subject, UserId = user.Id };
                configuration.IdentityLinks.Add(link);
            }

            var policy = _userManager.GetUserDto(user).Policy;
            if (policy.IsAdministrator != identity.IsAdministrator)
            {
                policy.IsAdministrator = identity.IsAdministrator;
                await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
                _logger.LogInformation("Synchronized Jellyfin administrator status from OIDC group membership.");
            }

            await PasswordLoginEnforcer.EnforceAsync(_userManager, user, configuration, _logger).ConfigureAwait(false);
            user = _userManager.GetUserById(user.Id);
            if (user is null)
            {
                return null;
            }

            if (configuration.SynchronizeProfileImages)
            {
                await _profileImageSynchronizer.SynchronizeAsync(user, link, principal.FindFirst("picture")?.Value, issuer).ConfigureAwait(false);
            }

            OidcPlugin.Instance!.UpdateConfiguration(configuration);
            return user.Id;
        }
        finally
        {
            _gate.Release();
        }
    }
}
