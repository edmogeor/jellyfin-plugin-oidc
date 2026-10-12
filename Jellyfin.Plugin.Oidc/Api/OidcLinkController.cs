using Jellyfin.Plugin.Oidc.Configuration;
using Jellyfin.Plugin.Oidc.Identity;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oidc.Api;

/// <summary>Self-service Identity Links for the authenticated Jellyfin User only.</summary>
[ApiController]
[Route("oidc/link")]
public sealed class OidcLinkController(
    IAuthorizationContext authorization,
    OidcLinkStore store,
    OidcUserProvisioner provisioner,
    IUserManager users,
    OidcLinkManager links,
    ILogger<OidcLinkController> logger
) : ControllerBase
{
    /// <summary>Returns only the current Jellyfin User's link status.</summary>
    [Authorize]
    [HttpGet]
    public async Task<IActionResult> Status()
    {
        Response.Headers.CacheControl = "no-store";
        var auth = await SessionAsync().ConfigureAwait(false);
        var plugin = OidcPlugin.Instance;
        if (auth is null)
        {
            return Unauthorized();
        }
        if (plugin is null)
        {
            return NotFound();
        }
        await plugin.ConfigurationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = AdministrationRules.State(plugin.Configuration, auth.UserId);
            if (
                EnabledConfiguration() is null
                && plugin.Configuration.IdentityLinks.All(link => link.UserId != auth.UserId)
                && state.PendingRevocations.Count == 0
                && plugin.Configuration.PasswordLoginMode != PasswordLoginMode.DisableForAllUsers
            )
            {
                return NotFound();
            }
            return Ok(StatusValue(auth.UserId, state));
        }
        finally
        {
            plugin.ConfigurationGate.Release();
        }
    }

    /// <summary>Unlinks only the authenticated Jellyfin User, preserving unrelated authentication records.</summary>
    [Authorize]
    [HttpDelete]
    public Task<IActionResult> Unlink([FromQuery] Guid version) => MutateAsync(version, null);

    /// <summary>Retries the authenticated user's outstanding unlink operation.</summary>
    [Authorize]
    [HttpPost("retry-sign-out")]
    public Task<IActionResult> Retry([FromBody] OidcAdminController.RevocationRetry request) =>
        MutateAsync(null, request.Operation);

    private async Task<IActionResult> MutateAsync(Guid? version, Guid? operation)
    {
        Response.Headers.CacheControl = "no-store";
        var auth = await SessionAsync().ConfigureAwait(false);
        if (auth is null)
            return Unauthorized();
        var plugin = OidcPlugin.Instance;
        if (plugin is null)
            return NotFound();
        await plugin.ConfigurationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (version.HasValue && !plugin.Configuration.AllowSelfServiceLinking)
                return Forbid();
            var state = AdministrationRules.State(plugin.Configuration, auth.UserId);
            if (version.HasValue)
            {
                if (!AdministrationRules.CanUnlink(plugin.Configuration, state, version.Value))
                    return Conflict();
                await links.UnlinkAsync(state).ConfigureAwait(false);
            }
            else if (
                !await links.RetryAsync(state, operation.GetValueOrDefault()).ConfigureAwait(false)
            )
                return Conflict();
            logger.LogInformation(
                "OIDC profile operation for Jellyfin User {UserId} completed.",
                auth.UserId
            );
            return Ok(StatusValue(auth.UserId, state));
        }
        catch
        {
            logger.LogWarning(
                "OIDC profile operation failed for Jellyfin User {UserId}.",
                auth.UserId
            );
            return StatusCode(503);
        }
        finally
        {
            plugin.ConfigurationGate.Release();
        }
    }

    private object StatusValue(Guid userId, OidcUserAdministration state)
    {
        var configuration = OidcPlugin.Instance!.Configuration;
        var link = configuration.IdentityLinks.FirstOrDefault(link => link.UserId == userId);
        var user = users.GetUserById(userId);
        return new
        {
            Name = user?.Username,
            Linked = link is not null,
            link?.Email,
            link?.Username,
            CanLink = EnabledConfiguration() is not null && link is null,
            CanUnlink = configuration.AllowSelfServiceLinking && link is not null,
            PasswordLoginDisabled = configuration.PasswordLoginMode
                == PasswordLoginMode.DisableForAllUsers
                || (
                    configuration.PasswordLoginMode == PasswordLoginMode.DisableForLinkedUsersOnly
                    && link is not null
                ),
            PasswordLoginMode = configuration.PasswordLoginMode.ToString(),
            state.Version,
            state.AutomaticReattachmentBlocked,
            state.UnlinkOperation,
            PartialRevocation = state.PendingRevocations.Count > 0,
            SoleAdministrator = user is not null
                && users.GetUserDto(user).Policy.IsAdministrator
                && users.GetUsers().Count(item => users.GetUserDto(item).Policy.IsAdministrator)
                    == 1,
        };
    }

    /// <summary>Issues a navigation ticket bound to a header-authenticated Jellyfin session.</summary>
    [Authorize]
    [HttpPost]
    public async Task<IActionResult> Initiate([FromQuery] string? returnUrl = null)
    {
        Response.Headers.CacheControl = "no-store";
        var auth = await SessionAsync().ConfigureAwait(false);
        var configuration = EnabledConfiguration();
        if (auth is null)
        {
            return Unauthorized();
        }
        if (configuration is null)
        {
            return NotFound();
        }
        if (!PublicUrls.UsesHttps(Request, configuration) || !ReturnUrls.Local(returnUrl))
        {
            return BadRequest();
        }
        var ticket = store.Create(auth.UserId, auth.Token!, returnUrl!, configuration);
        return ticket is null
            ? StatusCode(503)
            : Ok(
                new
                {
                    Url = PublicUrls.Get(Request, configuration)
                        + "/oidc/link/start?ticket="
                        + ticket,
                }
            );
    }

    /// <summary>Consumes the navigation ticket and starts the correlated linking flow.</summary>
    [AllowAnonymous]
    [HttpGet("start")]
    public IActionResult Start([FromQuery] string ticket)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        var configuration = EnabledConfiguration();
        if (configuration is null || !PublicUrls.UsesHttps(Request, configuration))
        {
            return BadRequest();
        }
        var transaction = store.Start(ticket, configuration, out var returnUrl);
        if (transaction is null)
        {
            return BadRequest();
        }
        var properties = new AuthenticationProperties
        {
            RedirectUri = returnUrl,
            Items = { ["oidc_link"] = transaction },
        };
        return Challenge(properties, OidcOptions.Scheme);
    }

    /// <summary>Persists the validated Identity Link only while the initiating session is still authenticated.</summary>
    [Authorize]
    [HttpPost("complete")]
    public async Task<IActionResult> Complete([FromBody] Completion request)
    {
        Response.Headers.CacheControl = "no-store";
        var auth = await SessionAsync().ConfigureAwait(false);
        if (auth is null)
        {
            return Unauthorized();
        }
        if (
            !store.TryComplete(
                request.Ticket,
                auth.UserId,
                auth.Token!,
                out var principal,
                out var configuration,
                out var revision
            )
            || !await provisioner
                .LinkAsync(auth.UserId, principal!, configuration!, revision)
                .ConfigureAwait(false)
        )
        {
            return Conflict();
        }
        return NoContent();
    }

    private async Task<AuthorizationInfo?> SessionAsync()
    {
        // Jellyfin uses header tokens, not ambient cookies, for these authenticated mutations.
        if (string.IsNullOrEmpty(Request.Headers.Authorization))
        {
            return null;
        }
        var auth = await authorization.GetAuthorizationInfo(Request).ConfigureAwait(false);
        return
            auth is { IsAuthenticated: true, IsApiKey: false, HasToken: true }
            && auth.UserId != Guid.Empty
            ? auth
            : null;
    }

    private static PluginConfiguration? EnabledConfiguration()
    {
        var configuration = OidcPlugin.Instance?.Configuration;
        return
            configuration is { Enabled: true, AllowSelfServiceLinking: true }
            && OidcConfigurationValidator.Validate(configuration) is null
            ? configuration
            : null;
    }

    /// <summary>A single-use linking completion ticket, never a Jellyfin access token.</summary>
    public sealed record Completion(string Ticket);
}
