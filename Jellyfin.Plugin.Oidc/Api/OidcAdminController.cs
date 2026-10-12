using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.Oidc.Configuration;
using Jellyfin.Plugin.Oidc.Identity;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oidc.Api;

/// <summary>Elevated administrator management of selected Jellyfin Users' Identity Links.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("oidc/admin/users/{userId:guid}")]
public sealed class OidcAdminController(
    IUserManager users,
    IAuthorizationContext authorization,
    OidcLinkManager links,
    ILogger<OidcAdminController> logger
) : ControllerBase
{
    /// <summary>Inspects links from any issuer, approvals, and persistent recovery state.</summary>
    [HttpGet]
    public Task<IActionResult> Status(Guid userId) =>
        ExecuteAsync(
            userId,
            null,
            state => Task.FromResult<IActionResult>(Ok(StatusValue(userId, state)))
        );

    /// <summary>Creates or replaces an approval for a future validated sign-in.</summary>
    [HttpPut("link-request")]
    public Task<IActionResult> Approve(Guid userId, [FromBody] Approval request) =>
        ExecuteAsync(
            userId,
            request.Version,
            async state =>
            {
                var plugin = OidcPlugin.Instance!;
                var configuration = plugin.Configuration;
                var email = request.Email?.Trim() ?? string.Empty;
                var subject = request.Subject?.Trim() ?? string.Empty;
                var username = request.Username?.Trim() ?? string.Empty;
                if (
                    new[] { email, subject, username }.Count(value => value.Length > 0) != 1
                    || email.Length > 320
                    || subject.Length > 1024
                    || username.Length > 1024
                    || (email.Length > 0 && !new EmailAddressAttribute().IsValid(email))
                )
                    return BadRequest();
                if (
                    !configuration.Enabled
                    || OidcConfigurationValidator.Validate(configuration) is not null
                )
                    return Conflict();
                var user = users.GetUserById(userId);
                if (
                    user is null
                    || users.GetUserDto(user).Policy.IsDisabled
                    || configuration.IdentityLinks.Any(link => link.UserId == userId)
                )
                    return Conflict();
                var approval = new PendingLinkRequest
                {
                    Issuer = configuration.IssuerUrl.TrimEnd('/'),
                    Email = email,
                    Subject = subject,
                    Username = username,
                    UsernameClaim =
                        username.Length > 0 ? configuration.UsernameClaim.Trim() : string.Empty,
                    CreatedAt = DateTime.UtcNow,
                };
                if (
                    configuration.UserAdministration.Any(item =>
                        item.UserId != userId
                        && item.Request is not null
                        && AdministrationRules.Overlaps(item.Request, approval)
                    )
                    || (
                        subject.Length > 0
                        && configuration.IdentityLinks.Any(link =>
                            link.Subject == subject
                            && (link.Issuer == approval.Issuer || link.Issuer.Length == 0)
                        )
                    )
                )
                    return Conflict();
                await PersistAsync(state, () => state.Request = approval).ConfigureAwait(false);
                return Ok(StatusValue(userId, state));
            }
        );

    /// <summary>Cancels an approval without clearing suppression.</summary>
    [HttpDelete("link-request")]
    public Task<IActionResult> Cancel(Guid userId, [FromQuery] Guid version) =>
        ExecuteAsync(
            userId,
            version,
            async state =>
            {
                if (state.Request is not null)
                    await PersistAsync(state, () => state.Request = null).ConfigureAwait(false);
                return Ok(StatusValue(userId, state));
            }
        );

    /// <summary>Persists unlink and suppression before targeted token revocation.</summary>
    [HttpDelete("link")]
    public Task<IActionResult> Unlink(Guid userId, [FromQuery] Guid version) =>
        ExecuteAsync(
            userId,
            version,
            async state =>
            {
                await links.UnlinkAsync(state).ConfigureAwait(false);
                return Ok(StatusValue(userId, state));
            },
            allowDuplicateUnlink: true
        );

    /// <summary>Retries a specific unlink operation without selecting newer sessions.</summary>
    [HttpPost("retry-sign-out")]
    public Task<IActionResult> Retry(Guid userId, [FromBody] RevocationRetry request) =>
        ExecuteAsync(
            userId,
            null,
            async state =>
            {
                if (!await links.RetryAsync(state, request.Operation).ConfigureAwait(false))
                    return Conflict();
                return Ok(StatusValue(userId, state));
            }
        );

    private object StatusValue(Guid userId, OidcUserAdministration state)
    {
        var configuration = OidcPlugin.Instance!.Configuration;
        var user = users.GetUserById(userId);
        var link = configuration.IdentityLinks.FirstOrDefault(link => link.UserId == userId);
        var oidcAvailable =
            configuration.Enabled && OidcConfigurationValidator.Validate(configuration) is null;
        return new
        {
            UserId = userId,
            Name = user?.Username,
            Linked = link is not null,
            link?.Email,
            link?.Username,
            state.Version,
            state.AutomaticReattachmentBlocked,
            state.Request,
            state.UnlinkOperation,
            PartialRevocation = state.PendingRevocations.Count > 0,
            CanCreateRequest = user is not null
                && !users.GetUserDto(user).Policy.IsDisabled
                && link is null
                && oidcAvailable,
            OidcAvailable = oidcAvailable,
            PasswordLoginMode = configuration.PasswordLoginMode.ToString(),
            AutomaticLinkingMode = configuration.AutomaticLinkingMode.ToString(),
            SoleAdministrator = user is not null
                && users.GetUserDto(user).Policy.IsAdministrator
                && users.GetUsers().Count(item => users.GetUserDto(item).Policy.IsAdministrator)
                    == 1,
        };
    }

    private async Task<IActionResult> ExecuteAsync(
        Guid userId,
        Guid? version,
        Func<OidcUserAdministration, Task<IActionResult>> operation,
        bool allowDuplicateUnlink = false
    )
    {
        Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrEmpty(Request.Headers.Authorization))
            return Unauthorized();
        var actor = await authorization.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (
            actor is not { IsAuthenticated: true, IsApiKey: false, HasToken: true }
            || actor.UserId == Guid.Empty
        )
            return Unauthorized();
        var actorUser = users.GetUserById(actor.UserId);
        if (actorUser is null || !users.GetUserDto(actorUser).Policy.IsAdministrator)
            return Forbid();
        var plugin = OidcPlugin.Instance;
        if (plugin is null)
            return NotFound();
        await plugin.ConfigurationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (
                users.GetUserById(userId) is null
                && plugin.Configuration.IdentityLinks.All(link => link.UserId != userId)
                && plugin.Configuration.UserAdministration.All(item => item.UserId != userId)
            )
                return NotFound();
            var state = AdministrationRules.State(plugin.Configuration, userId);
            if (
                version.HasValue
                && state.Version != version
                && !(
                    allowDuplicateUnlink
                    && AdministrationRules.CanUnlink(plugin.Configuration, state, version.Value)
                )
            )
                return Conflict();
            var result = await operation(state).ConfigureAwait(false);
            if (Request.Method != "GET")
                logger.LogInformation(
                    "OIDC administration {Method} for actor {ActorId} and target {TargetId}: {Outcome}.",
                    Request.Method,
                    actor.UserId,
                    userId,
                    result is OkObjectResult ? "completed" : "rejected"
                );
            return result;
        }
        catch
        {
            logger.LogWarning(
                "OIDC administration failed for actor {ActorId} and target {TargetId}.",
                actor.UserId,
                userId
            );
            return StatusCode(503);
        }
        finally
        {
            plugin.ConfigurationGate.Release();
        }
    }

    private async Task PersistAsync(OidcUserAdministration state, Action change)
    {
        var plugin = OidcPlugin.Instance!;
        var oldVersion = state.Version;
        var oldRequest = state.Request;
        var oldBlock = state.AutomaticReattachmentBlocked;
        var oldRevision = plugin.Configuration.IdentityRevision;
        try
        {
            change();
            state.Version = Guid.NewGuid();
            plugin.Configuration.IdentityRevision++;
            var user = users.GetUserById(state.UserId);
            if (user is not null)
                await PasswordLoginEnforcer
                    .EnforceAsync(users, user, plugin.Configuration, logger)
                    .ConfigureAwait(false);
            plugin.SaveConfiguration();
        }
        catch
        {
            state.Version = oldVersion;
            state.Request = oldRequest;
            state.AutomaticReattachmentBlocked = oldBlock;
            plugin.Configuration.IdentityRevision = oldRevision;
            throw;
        }
    }

    /// <summary>Version-checked approval criteria.</summary>
    public sealed record Approval(
        Guid Version,
        string? Email = null,
        string? Subject = null,
        string? Username = null
    );

    /// <summary>The persistent unlink operation to retry.</summary>
    public sealed record RevocationRetry(Guid Operation);
}
