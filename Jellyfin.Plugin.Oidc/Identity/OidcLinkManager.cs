using Jellyfin.Plugin.Oidc.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Shared unlink persistence and targeted revocation. Callers hold the configuration gate.</summary>
public sealed class OidcLinkManager(IUserManager users, OidcSessionTracker tracker, ILogger<OidcLinkManager> logger)
{
    /// <summary>Removes links before revoking their exact session selection, with rollback on persistence failure.</summary>
    public async Task UnlinkAsync(OidcUserAdministration state)
    {
        var plugin = OidcPlugin.Instance!;
        var configuration = plugin.Configuration;
        var removed = configuration.IdentityLinks.Where(link => link.UserId == state.UserId).ToList();
        if (removed.Count > 0)
        {
            var oldVersion = state.Version;
            var oldRequest = state.Request;
            var oldBlock = state.AutomaticReattachmentBlocked;
            var oldOperation = state.UnlinkOperation;
            var oldUnlinkVersion = state.UnlinkVersion;
            var oldRevocations = state.PendingRevocations;
            var oldRevision = configuration.IdentityRevision;
            try
            {
                configuration.IdentityLinks.RemoveAll(link => link.UserId == state.UserId);
                state.Request = null;
                state.AutomaticReattachmentBlocked = true;
                state.UnlinkOperation = Guid.NewGuid();
                state.UnlinkVersion = state.Version;
                state.Version = state.UnlinkOperation;
                state.PendingRevocations = state.PendingRevocations.Concat(configuration.OidcSessions.Where(item => item.UserId == state.UserId))
                    .DistinctBy(item => (item.AuthenticationId, item.TokenFingerprint)).ToList();
                configuration.IdentityRevision++;
                await EnforcePasswordPolicyAsync(state.UserId).ConfigureAwait(false);
                plugin.SaveConfiguration();
            }
            catch
            {
                state.Version = oldVersion; state.Request = oldRequest; state.AutomaticReattachmentBlocked = oldBlock;
                state.UnlinkOperation = oldOperation; state.UnlinkVersion = oldUnlinkVersion; state.PendingRevocations = oldRevocations;
                configuration.IdentityRevision = oldRevision;
                configuration.IdentityLinks.AddRange(removed);
                await EnforcePasswordPolicyAsync(state.UserId).ConfigureAwait(false);
                throw;
            }
        }
        await tracker.RetryAsync(state).ConfigureAwait(false);
    }

    /// <summary>Retries only the session selection belonging to the specified unlink.</summary>
    public async Task<bool> RetryAsync(OidcUserAdministration state, Guid operation)
    {
        if (operation == Guid.Empty || state.UnlinkOperation != operation) return false;
        await tracker.RetryAsync(state).ConfigureAwait(false);
        return true;
    }

    private async Task EnforcePasswordPolicyAsync(Guid userId)
    {
        var user = users.GetUserById(userId);
        if (user is not null) await PasswordLoginEnforcer.EnforceAsync(users, user, OidcPlugin.Instance!.Configuration, logger).ConfigureAwait(false);
    }
}
