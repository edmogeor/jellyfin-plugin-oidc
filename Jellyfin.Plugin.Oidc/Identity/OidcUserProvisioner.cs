using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jellyfin.Data;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Oidc.Configuration;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Matches, provisions, and synchronizes Jellyfin Users after token validation.</summary>
public sealed class OidcUserProvisioner
{
    private readonly ICryptoProvider _cryptoProvider;
    private readonly ILogger<OidcUserProvisioner> _logger;
    private readonly IDbContextFactory<JellyfinDbContext> _dbContextFactory;
    private readonly IEventManager _eventManager;
    private readonly OidcProfileImageSynchronizer _profileImageSynchronizer;
    private readonly IUserManager _userManager;

    /// <summary>Initializes a new instance of the <see cref="OidcUserProvisioner"/> class.</summary>
    public OidcUserProvisioner(
        IUserManager userManager,
        ICryptoProvider cryptoProvider,
        OidcProfileImageSynchronizer profileImageSynchronizer,
        ILogger<OidcUserProvisioner> logger,
        IDbContextFactory<JellyfinDbContext> dbContextFactory,
        IEventManager eventManager
    )
    {
        _userManager = userManager;
        _cryptoProvider = cryptoProvider;
        _profileImageSynchronizer = profileImageSynchronizer;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _eventManager = eventManager;
    }

    /// <summary>Returns the eligible Jellyfin User ID, or <see langword="null"/> when denied.</summary>
    public async Task<Guid?> ProvisionAsync(
        ClaimsPrincipal principal,
        PluginConfiguration authenticatedConfiguration,
        long? expectedRevision = null
    )
    {
        var plugin = OidcPlugin.Instance;
        if (plugin is null)
        {
            return null;
        }

        await plugin.ConfigurationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var configuration = plugin.Configuration;
            if (
                !ReferenceEquals(configuration, authenticatedConfiguration)
                || (expectedRevision.HasValue && expectedRevision != configuration.IdentityRevision)
                || !configuration.Enabled
                || !IdentityClaims.TryCreate(principal, configuration, out var identity)
            )
            {
                _logger.LogWarning(
                    "OIDC sign-in denied because configuration changed or required identity claims or allowed groups were absent."
                );
                return null;
            }

            var issuer = configuration.IssuerUrl.TrimEnd('/');
            var resolution = await ResolveUserAsync(principal, identity, configuration, issuer)
                .ConfigureAwait(false);
            if (resolution is null)
            {
                return null;
            }

            return await SynchronizeAndSaveAsync(
                    plugin,
                    principal,
                    identity,
                    issuer,
                    resolution.Value
                )
                .ConfigureAwait(false);
        }
        finally
        {
            plugin.ConfigurationGate.Release();
        }
    }

    private async Task<(
        User User,
        IdentityLink? Link,
        OidcUserAdministration? Approval
    )?> ResolveUserAsync(
        ClaimsPrincipal principal,
        OidcIdentity identity,
        PluginConfiguration configuration,
        string issuer
    )
    {
        var link = configuration.IdentityLinks.SingleOrDefault(item =>
            item.Subject == identity.Subject
            && (item.Issuer == issuer || string.IsNullOrEmpty(item.Issuer))
        );
        var user = link is null ? null : _userManager.GetUserById(link.UserId);
        if (link is not null && user is null)
        {
            configuration.IdentityLinks.Remove(link);
            link = null;
        }
        else if (link is not null && string.IsNullOrEmpty(link.Issuer))
        {
            link.Issuer = issuer;
        }

        var reservations = configuration
            .UserAdministration.Where(item =>
                item.Request is not null
                && AdministrationRules.Reserved(
                    item.Request,
                    issuer,
                    identity.Subject,
                    principal.FindFirst("email")?.Value ?? string.Empty,
                    identity.Username
                )
            )
            .ToList();
        OidcUserAdministration? approval = null;
        if (link is null && reservations.Count > 0)
        {
            if (
                reservations.Count != 1
                || !AdministrationRules.Matches(
                    reservations[0].Request!,
                    issuer,
                    identity.Subject,
                    identity.Email,
                    identity.Username,
                    configuration.UsernameClaim.Trim()
                )
            )
                return null;
            approval = reservations[0];
            user = _userManager.GetUserById(approval.UserId);
            if (
                user is null
                || _userManager.GetUserDto(user).Policy.IsDisabled
                || configuration.IdentityLinks.Any(item => item.UserId == user.Id)
            )
                return null;
        }

        if (
            user is null
            && string.IsNullOrEmpty(identity.Email)
            && string.IsNullOrEmpty(identity.Username)
        )
        {
            _logger.LogWarning(
                "OIDC sign-in denied because an unlinked identity requires a username or verified email for provisioning."
            );
            return null;
        }

        if (user is null)
        {
            var matchingValue = configuration.AutomaticLinkingMode switch
            {
                AutomaticLinkingMode.EmailToUsername => identity.Email,
                AutomaticLinkingMode.UsernameToUsername => identity.Username,
                _ => string.Empty,
            };
            var matches = _userManager
                .GetUsers()
                .Where(candidate =>
                    !string.IsNullOrEmpty(matchingValue)
                    && string.Equals(
                        candidate.Username,
                        matchingValue,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .ToList();
            if (matches.Count > 1)
            {
                _logger.LogWarning(
                    "OIDC sign-in denied because automatic matching found multiple Jellyfin Users."
                );
                return null;
            }

            var matched = matches.SingleOrDefault();
            var administration = matched is null
                ? null
                : configuration.UserAdministration.SingleOrDefault(item =>
                    item.UserId == matched.Id
                );
            if (administration?.Request is not null)
                return null;
            if (administration?.AutomaticReattachmentBlocked != true)
                user = matched;
        }
        if (user is not null && _userManager.GetUserDto(user).Policy.IsDisabled)
            return null;
        if (
            link is null
            && user is not null
            && configuration.IdentityLinks.Any(item => item.UserId == user.Id)
        )
        {
            _logger.LogWarning(
                "OIDC sign-in denied because the matched Jellyfin User already has an Identity Link."
            );
            return null;
        }

        if (user is null)
        {
            var username =
                configuration.ProvisioningUsernameMode == ProvisioningUsernameMode.VerifiedEmail
                || string.IsNullOrEmpty(identity.Username)
                    ? identity.Email
                    : identity.Username;
            if (string.IsNullOrEmpty(username))
            {
                _logger.LogWarning(
                    "OIDC provisioning denied because the selected username source was unavailable."
                );
                return null;
            }
            if (
                _userManager
                    .GetUsers()
                    .Any(candidate =>
                        string.Equals(
                            candidate.Username,
                            username,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
            )
            {
                _logger.LogWarning(
                    "OIDC provisioning denied because the requested username is already used by a Jellyfin User."
                );
                return null;
            }
            user = await CreateUserAsync(username, configuration).ConfigureAwait(false);
            _logger.LogInformation("Provisioned Jellyfin User for an eligible OIDC identity.");
        }

        return (user, link, approval);
    }

    private async Task<Guid> SynchronizeAndSaveAsync(
        OidcPlugin plugin,
        ClaimsPrincipal principal,
        OidcIdentity identity,
        string issuer,
        (User User, IdentityLink? Link, OidcUserAdministration? Approval) resolution
    )
    {
        var configuration = plugin.Configuration;
        var (user, link, approval) = resolution;
        var newLink = link is null;
        if (newLink)
        {
            link = new IdentityLink
            {
                Issuer = issuer,
                Subject = identity.Subject,
                UserId = user.Id,
            };
            configuration.IdentityLinks.Add(link);
        }

        var administrationState = AdministrationRules.State(configuration, user.Id);
        var oldRequest = administrationState.Request;
        var oldBlock = administrationState.AutomaticReattachmentBlocked;
        var oldVersion = administrationState.Version;
        var oldEmail = link!.Email;
        var oldUsername = link.Username;
        try
        {
            var policy = _userManager.GetUserDto(user).Policy;
            if (policy.IsAdministrator != identity.IsAdministrator)
            {
                policy.IsAdministrator = identity.IsAdministrator;
                await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
                _logger.LogInformation(
                    "Synchronized Jellyfin administrator status from OIDC group membership."
                );
            }

            await PasswordLoginEnforcer
                .EnforceAsync(_userManager, user, configuration, _logger)
                .ConfigureAwait(false);
            user = _userManager.GetUserById(user.Id);
            if (user is null)
            {
                throw new InvalidOperationException(
                    "The target Jellyfin User was deleted during sign-in."
                );
            }

            if (configuration.SynchronizeProfileImages)
            {
                await _profileImageSynchronizer
                    .SynchronizeAsync(user, link, principal.FindFirst("picture")?.Value, issuer)
                    .ConfigureAwait(false);
            }

            UpdateLinkMetadata(link, identity);
            if (newLink)
            {
                administrationState.Version = Guid.NewGuid();
                if (approval is not null)
                {
                    administrationState.Request = null;
                    administrationState.AutomaticReattachmentBlocked = false;
                }
            }
            EnsureUserAvailable(user.Id);
            plugin.SaveConfiguration();
        }
        catch
        {
            link.Email = oldEmail;
            link.Username = oldUsername;
            administrationState.Request = oldRequest;
            administrationState.AutomaticReattachmentBlocked = oldBlock;
            administrationState.Version = oldVersion;
            if (newLink)
            {
                configuration.IdentityLinks.Remove(link);
                var currentUser = _userManager.GetUserById(link.UserId);
                if (currentUser is not null)
                    await PasswordLoginEnforcer
                        .EnforceAsync(_userManager, currentUser, configuration, _logger)
                        .ConfigureAwait(false);
            }
            throw;
        }
        return user.Id;
    }

    /// <summary>Links a validated identity to an explicitly authenticated Jellyfin User without provisioning or synchronization.</summary>
    public async Task<bool> LinkAsync(
        Guid userId,
        ClaimsPrincipal principal,
        PluginConfiguration authenticatedConfiguration,
        long? expectedRevision = null
    )
    {
        var plugin = OidcPlugin.Instance;
        if (plugin is null)
        {
            return false;
        }

        await plugin.ConfigurationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var configuration = plugin.Configuration;
            var user = _userManager.GetUserById(userId);
            if (
                !ReferenceEquals(configuration, authenticatedConfiguration)
                || !configuration.Enabled
                || (expectedRevision.HasValue && expectedRevision != configuration.IdentityRevision)
                || user is null
                || _userManager.GetUserDto(user).Policy.IsDisabled
                || !IdentityClaims.TryCreate(principal, configuration, out var identity)
            )
            {
                return false;
            }

            var issuer = configuration.IssuerUrl.TrimEnd('/');
            var state = AdministrationRules.State(configuration, userId);
            if (
                (
                    state.Request is not null
                    && !AdministrationRules.Matches(
                        state.Request,
                        issuer,
                        identity.Subject,
                        identity.Email,
                        identity.Username,
                        configuration.UsernameClaim.Trim()
                    )
                )
                || configuration.UserAdministration.Any(item =>
                    item.UserId != userId
                    && item.Request is not null
                    && AdministrationRules.Reserved(
                        item.Request,
                        issuer,
                        identity.Subject,
                        principal.FindFirst("email")?.Value ?? string.Empty,
                        identity.Username
                    )
                )
            )
                return false;
            var existing = configuration.IdentityLinks.SingleOrDefault(item =>
                item.UserId == userId
            );
            if (
                (
                    existing is not null
                    && (
                        existing.Subject != identity.Subject
                        || (existing.Issuer != issuer && !string.IsNullOrEmpty(existing.Issuer))
                    )
                )
                || configuration.IdentityLinks.Any(item =>
                    item.UserId != userId
                    && item.Subject == identity.Subject
                    && (item.Issuer == issuer || string.IsNullOrEmpty(item.Issuer))
                )
            )
            {
                return false;
            }

            var link =
                existing
                ?? new IdentityLink
                {
                    Issuer = issuer,
                    Subject = identity.Subject,
                    UserId = userId,
                };
            if (existing is null)
            {
                configuration.IdentityLinks.Add(link);
            }

            var oldIssuer = link.Issuer;
            var oldEmail = link.Email;
            var oldUsername = link.Username;
            var oldRequest = state.Request;
            var oldBlock = state.AutomaticReattachmentBlocked;
            var oldVersion = state.Version;
            try
            {
                await PasswordLoginEnforcer
                    .EnforceAsync(_userManager, user, configuration, _logger)
                    .ConfigureAwait(false);
                link.Issuer = issuer;
                UpdateLinkMetadata(link, identity);
                state.Request = null;
                state.AutomaticReattachmentBlocked = false;
                if (existing is null)
                    state.Version = Guid.NewGuid();
                EnsureUserAvailable(userId);
                plugin.SaveConfiguration();
                return true;
            }
            catch
            {
                link.Issuer = oldIssuer;
                link.Email = oldEmail;
                link.Username = oldUsername;
                state.Request = oldRequest;
                state.AutomaticReattachmentBlocked = oldBlock;
                state.Version = oldVersion;
                if (existing is null)
                {
                    configuration.IdentityLinks.Remove(link);
                    var currentUser = _userManager.GetUserById(userId);
                    if (currentUser is not null)
                    {
                        await PasswordLoginEnforcer
                            .EnforceAsync(_userManager, currentUser, configuration, _logger)
                            .ConfigureAwait(false);
                    }
                }
                throw;
            }
        }
        finally
        {
            plugin.ConfigurationGate.Release();
        }
    }

    private static void UpdateLinkMetadata(IdentityLink link, OidcIdentity identity)
    {
        if (!string.IsNullOrEmpty(identity.Email))
        {
            link.Email = identity.Email;
        }
        if (!string.IsNullOrEmpty(identity.Username))
        {
            link.Username = identity.Username;
        }
    }

    private void EnsureUserAvailable(Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null || _userManager.GetUserDto(user).Policy.IsDisabled)
        {
            throw new InvalidOperationException("The target Jellyfin User is unavailable.");
        }
    }

    private async Task<User> CreateUserAsync(string username, PluginConfiguration configuration)
    {
        if (
            !Regex.IsMatch(
                username,
                @"^(?!\s)[\w\ \-'._@+]+(?<!\s)$",
                RegexOptions.CultureInvariant
            ) || username is "." or ".."
        )
        {
            throw new ArgumentException(
                "The Identity Provider supplied an invalid Jellyfin username.",
                nameof(username)
            );
        }

        var password = _cryptoProvider
            .CreatePasswordHash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)))
            .ToString();
        await using var dbContext = await _dbContextFactory
            .CreateDbContextAsync()
            .ConfigureAwait(false);
        var user = new User(
            username,
            configuration.PasswordLoginMode == PasswordLoginMode.AllowForAllUsers
                ? "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider"
                : typeof(OidcPasswordDisabledProvider).FullName!,
            "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider"
        )
        {
            Password = password,
            InternalId =
                (
                    await dbContext
                        .Users.Select(candidate => (long?)candidate.InternalId)
                        .MaxAsync()
                        .ConfigureAwait(false)
                    ?? 0
                ) + 1,
        };
        user.AddDefaultPermissions();
        user.AddDefaultPreferences();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        await _eventManager.PublishAsync(new UserCreatedEventArgs(user)).ConfigureAwait(false);
        return user;
    }
}
