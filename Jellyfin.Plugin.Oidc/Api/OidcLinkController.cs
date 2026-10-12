using Jellyfin.Plugin.Oidc.Configuration;
using Jellyfin.Plugin.Oidc.Identity;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Oidc.Api;

/// <summary>Self-service Identity Links for the authenticated Jellyfin User only.</summary>
[ApiController]
[Route("oidc/link")]
public sealed class OidcLinkController(IAuthorizationContext authorization, OidcLinkStore store, OidcUserProvisioner provisioner) : ControllerBase
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
            var configuration = EnabledConfiguration();
            if (configuration is null)
            {
                return NotFound();
            }
            var issuer = configuration.IssuerUrl.TrimEnd('/');
            var links = configuration.IdentityLinks.Where(link => link.UserId == auth.UserId).ToList();
            var link = links.SingleOrDefault(item => item.Issuer == issuer || string.IsNullOrEmpty(item.Issuer));
            var passwordLoginDisabled = configuration.PasswordLoginMode == PasswordLoginMode.DisableForAllUsers
                || (configuration.PasswordLoginMode == PasswordLoginMode.DisableForLinkedUsersOnly && links.Count > 0);
            return Ok(new { Linked = link is not null, link?.Email, link?.Username, CanLink = links.Count == 0, PasswordLoginDisabled = passwordLoginDisabled, PasswordLoginMode = configuration.PasswordLoginMode.ToString() });
        }
        finally
        {
            plugin.ConfigurationGate.Release();
        }
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
        return ticket is null ? StatusCode(503) : Ok(new { Url = PublicUrls.Get(Request, configuration) + "/oidc/link/start?ticket=" + ticket });
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
        var properties = new AuthenticationProperties { RedirectUri = returnUrl, Items = { ["oidc_link"] = transaction } };
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
        if (!store.TryComplete(request.Ticket, auth.UserId, auth.Token!, out var principal, out var configuration)
            || !await provisioner.LinkAsync(auth.UserId, principal!, configuration!).ConfigureAwait(false))
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
        return auth is { IsAuthenticated: true, IsApiKey: false, HasToken: true } && auth.UserId != Guid.Empty ? auth : null;
    }

    private static PluginConfiguration? EnabledConfiguration()
    {
        var configuration = OidcPlugin.Instance?.Configuration;
        return configuration is { Enabled: true } && OidcConfigurationValidator.Validate(configuration) is null ? configuration : null;
    }

    /// <summary>A single-use linking completion ticket, never a Jellyfin access token.</summary>
    public sealed record Completion(string Ticket);
}
