using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.Oidc.Configuration;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Records and revokes only positively identified OIDC authentication records.</summary>
public sealed class OidcSessionTracker(
    IDbContextFactory<JellyfinDbContext> database,
    IDeviceManager devices,
    ISessionManager sessions,
    ILogger<OidcSessionTracker> logger
)
{
    /// <summary>Creates a session and persists evidence before exposing its credentials. The caller holds the configuration gate.</summary>
    public async Task<AuthenticationResult> CreateAsync(Guid userId)
    {
        var plugin = OidcPlugin.Instance!;
        var result = await sessions
            .AuthenticateDirect(
                new AuthenticationRequest
                {
                    UserId = userId,
                    App = "Jellyfin Web",
                    AppVersion = "12",
                    DeviceId = Guid.NewGuid().ToString("N"),
                    DeviceName = "Web Browser",
                }
            )
            .ConfigureAwait(false);
        var record = devices
            .GetDevices(new DeviceQuery { AccessToken = result.AccessToken, UserId = userId })
            .Items.Single();
        var evidence = new OidcSessionProvenance
        {
            AuthenticationId = record.Id,
            UserId = userId,
            TokenFingerprint = Fingerprint(result.AccessToken),
        };
        try
        {
            await using var context = await database.CreateDbContextAsync().ConfigureAwait(false);
            var authenticationIds = (
                await context.Devices.Select(item => item.Id).ToListAsync().ConfigureAwait(false)
            ).ToHashSet();
            plugin.Configuration.OidcSessions.RemoveAll(item =>
                !authenticationIds.Contains(item.AuthenticationId)
            );
            plugin.Configuration.OidcSessions.Add(evidence);
            plugin.SaveConfiguration();
            return result;
        }
        catch
        {
            plugin.Configuration.OidcSessions.Remove(evidence);
            await RevokeAsync(evidence).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Retries only the immutable selection from an unlink, including after restart. The caller holds the gate.</summary>
    public async Task RetryAsync(OidcUserAdministration state)
    {
        var plugin = OidcPlugin.Instance!;
        var completed = new HashSet<OidcSessionProvenance>();
        foreach (var evidence in state.PendingRevocations)
        {
            try
            {
                await RevokeAsync(evidence).ConfigureAwait(false);
                completed.Add(evidence);
            }
            catch
            {
                logger.LogWarning(
                    "OIDC token revocation incomplete for Jellyfin User {UserId} and unlink operation {OperationId}.",
                    state.UserId,
                    state.UnlinkOperation
                );
            }
        }
        var oldSessions = plugin.Configuration.OidcSessions.ToList();
        var oldPending = state.PendingRevocations.ToList();
        var completedRecords = completed
            .Select(item => (item.AuthenticationId, item.TokenFingerprint))
            .ToHashSet();
        state.PendingRevocations.RemoveAll(completed.Contains);
        plugin.Configuration.OidcSessions.RemoveAll(item =>
            completedRecords.Contains((item.AuthenticationId, item.TokenFingerprint))
        );
        try
        {
            plugin.SaveConfiguration();
        }
        catch
        {
            state.PendingRevocations = oldPending;
            plugin.Configuration.OidcSessions = oldSessions;
            throw;
        }
    }

    private async Task RevokeAsync(OidcSessionProvenance evidence)
    {
        await using var context = await database.CreateDbContextAsync().ConfigureAwait(false);
        var record = await context
            .Devices.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == evidence.AuthenticationId)
            .ConfigureAwait(false);
        if (
            record is null
            || record.UserId != evidence.UserId
            || Fingerprint(record.AccessToken) != evidence.TokenFingerprint
        )
            return;

        // SessionManager.Logout logs bearer tokens. DeleteDevice uses the same supported token deletion and cache invalidation without that log.
        await devices.DeleteDevice(record).ConfigureAwait(false);
        if (await context.Devices.AnyAsync(item => item.Id == record.Id).ConfigureAwait(false))
            throw new InvalidOperationException("Authentication record was not removed.");
        foreach (
            var session in sessions
                .Sessions.Where(item =>
                    item.UserId == record.UserId && item.DeviceId == record.DeviceId
                )
                .ToList()
        )
        {
            await sessions.ReportSessionEnded(session.Id).ConfigureAwait(false);
        }
    }

    /// <summary>Fingerprints a token without retaining its bearer value.</summary>
    public static string Fingerprint(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
