using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Oidc.Configuration;
using Jellyfin.Plugin.Oidc.Identity;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Oidc.Api;

/// <summary>Authenticated, scoped Identity Link management.</summary>
[ApiController]
[Authorize]
[Route("oidc/identity")]
public sealed class IdentityController : ControllerBase
{
    private readonly OidcUserProvisioner _identities;
    private readonly OidcLinkStore _tickets;
    private readonly IUserManager _users;

    /// <summary>Initializes a new instance of the <see cref="IdentityController"/> class.</summary>
    public IdentityController(OidcUserProvisioner identities, OidcLinkStore tickets, IUserManager users)
    {
        _identities = identities;
        _tickets = tickets;
        _users = users;
    }

    private Guid OwnerId => Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;
    private bool IsAdministrator => OwnerId != Guid.Empty && _users.GetUserById(OwnerId) is { } user && _users.GetUserDto(user).Policy.IsAdministrator;
    private string Session => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(User.FindFirst("Jellyfin-Token")?.Value ?? "")));
    private bool HasSession => OwnerId != Guid.Empty && !string.IsNullOrEmpty(User.FindFirst("Jellyfin-Token")?.Value);

    /// <summary>Reads only the authenticated Jellyfin User's link.</summary>
    [HttpGet("me")]
    public Task<IActionResult> Me() => Read(OwnerId, false);

    /// <summary>Reads an explicitly selected Jellyfin User as an administrator.</summary>
    [HttpGet("users/{userId:guid}")]
    public Task<IActionResult> UserStatus(Guid userId) => Read(userId, true);

    private async Task<IActionResult> Read(Guid userId, bool administrator)
    {
        Response.Headers.CacheControl = "no-store";
        if (!HasSession || (administrator && !IsAdministrator)) return Forbid();
        if (OidcPlugin.Instance?.Configuration.Enabled != true) return NotFound();
        try { return Ok(await _identities.StatusAsync(userId, administrator).ConfigureAwait(false)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException exception) { return Conflict(new { Error = exception.Message }); }
    }

    /// <summary>Starts self-service linking, bound to the authenticated session.</summary>
    [HttpPost("me/start")]
    public Task<IActionResult> StartSelf(StartRequest request) => Start(OwnerId, false, request);

    /// <summary>Starts administrator-directed linking to an explicitly selected user.</summary>
    [HttpPost("users/{userId:guid}/start")]
    public Task<IActionResult> StartAdmin(Guid userId, StartRequest request) => Start(userId, true, request);

    private async Task<IActionResult> Start(Guid userId, bool administrator, StartRequest request)
    {
        if (!HasSession || (administrator && !IsAdministrator)) return Forbid();
        var configuration = OidcPlugin.Instance!.Configuration;
        if (!configuration.Enabled || (!administrator && !configuration.AllowSelfServiceIdentityLinks)) return Forbid();
        if (!PublicUrls.UsesHttps(Request, configuration) || !ReturnUrls.Local(request.ReturnUrl)) return BadRequest();
        var statusResult = await Read(userId, administrator).ConfigureAwait(false);
        if (statusResult is not OkObjectResult { Value: OidcUserProvisioner.IdentityStatus status }) return statusResult;
        if (status.Linked || status.Revision != request.Revision) return Conflict(new { Error = "Identity state changed. Refresh and try again." });
        var ticket = _tickets.Create(new OidcLinkStore.LinkIntent(OwnerId, userId, Session, administrator, IdentityRules.Issuer(configuration), request.ReturnUrl, request.Revision, DateTimeOffset.UtcNow.AddMinutes(5)));
        return ticket is null ? StatusCode(503) : Ok(new { Url = PublicUrls.Get(Request, configuration) + "/oidc/identity/challenge?ticket=" + ticket });
    }

    /// <summary>Consumes an opaque start intent before navigating to the Identity Provider.</summary>
    [AllowAnonymous]
    [HttpGet("challenge")]
    public IActionResult ChallengeLink([FromQuery] string ticket)
    {
        var configuration = OidcPlugin.Instance!.Configuration;
        if (!_tickets.TryGet(ticket, true, out var intent) || intent.Principal is not null || !configuration.Enabled
            || intent.Issuer != IdentityRules.Issuer(configuration) || (!intent.Administrator && !configuration.AllowSelfServiceIdentityLinks)
            || !PublicUrls.UsesHttps(Request, configuration)) return BadRequest();
        var callbackIntent = _tickets.Create(intent);
        if (callbackIntent is null) return StatusCode(503);
        var properties = new AuthenticationProperties
        {
            RedirectUri = intent.ReturnUrl,
            Items = { ["oidc_link_intent"] = callbackIntent, ["oidc_link_return"] = intent.ReturnUrl },
        };
        return Challenge(properties, OidcOptions.Scheme);
    }

    /// <summary>Shows the validated identity before administrator confirmation.</summary>
    [HttpPost("review")]
    public IActionResult Review(TicketRequest request)
    {
        Response.Headers.CacheControl = "no-store";
        if (!TryCompletion(request.Ticket, false, out var intent)) return BadRequest(new { Error = "The linking attempt expired or belongs to another session. Start again." });
        if (!IdentityClaims.TryCreate(intent!.Principal!, OidcPlugin.Instance!.Configuration, out var identity)) return BadRequest();
        var target = _users.GetUserById(intent.TargetId);
        if (target is null) return NotFound();
        return Ok(new { intent.TargetId, TargetName = target.Username, intent.Administrator, identity.Email, identity.PreferredUsername, Subject = intent.Administrator ? identity.Subject : null });
    }

    /// <summary>Consumes the completion ticket in the initiating authenticated session.</summary>
    [HttpPost("complete")]
    public async Task<IActionResult> Complete(TicketRequest request)
    {
        if (!TryCompletion(request.Ticket, true, out var intent)) return BadRequest(new { Error = "The linking attempt expired or belongs to another session. Start again." });
        return await Mutate(() => _identities.LinkAsync(intent!.TargetId, intent.Principal!, intent.Administrator, intent.Issuer, intent.Revision)).ConfigureAwait(false);
    }

    /// <summary>Cancels a completion without modifying identity state.</summary>
    [HttpPost("cancel")]
    public IActionResult Cancel(TicketRequest request) => TryCompletion(request.Ticket, true, out _) ? NoContent() : BadRequest();

    private bool TryCompletion(string ticket, bool consume, out OidcLinkStore.LinkIntent? intent)
    {
        intent = null;
        if (!HasSession || !_tickets.TryGet(ticket, false, out var stored) || stored.Principal is null
            || stored.OwnerId != OwnerId || stored.Session != Session || (stored.Administrator && !IsAdministrator)) return false;
        var configuration = OidcPlugin.Instance!.Configuration;
        if (!configuration.Enabled || stored.Issuer != IdentityRules.Issuer(configuration) || (!stored.Administrator && !configuration.AllowSelfServiceIdentityLinks)) return false;
        return _tickets.TryGet(ticket, consume, out intent);
    }

    /// <summary>Unlinks only the authenticated Jellyfin User.</summary>
    [HttpPost("me/unlink")]
    public Task<IActionResult> UnlinkSelf(RevisionRequest request) => !HasSession ? Task.FromResult<IActionResult>(Forbid())
        : Mutate(() => _identities.UnlinkAsync(OwnerId, false, request.Revision));

    /// <summary>Explicitly unlinks the edited Jellyfin User.</summary>
    [HttpPost("users/{userId:guid}/unlink")]
    public Task<IActionResult> UnlinkAdmin(Guid userId, RevisionRequest request) => !IsAdministrator ? Task.FromResult<IActionResult>(Forbid())
        : Mutate(() => _identities.UnlinkAsync(userId, true, request.Revision));

    /// <summary>Creates, corrects, or cancels an administrator-directed pending match.</summary>
    [HttpPost("users/{userId:guid}/pending")]
    public Task<IActionResult> Pending(Guid userId, PendingRequest request) => !IsAdministrator ? Task.FromResult<IActionResult>(Forbid())
        : Mutate(() => _identities.PendingAsync(userId, request.Claim, request.Value, request.Revision));

    private async Task<IActionResult> Mutate(Func<Task> change)
    {
        try { await change().ConfigureAwait(false); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException exception) { return Conflict(new { Error = exception.Message }); }
        catch (IOException) { return StatusCode(503, new { Error = "Could not save identity state. Refresh before retrying." }); }
    }

    /// <summary>Revision-bound link start request.</summary>
    public sealed record StartRequest(string ReturnUrl, long Revision);
    /// <summary>An opaque, session-bound completion ticket.</summary>
    public sealed record TicketRequest(string Ticket);
    /// <summary>Expected identity revision for a management operation.</summary>
    public sealed record RevisionRequest(long Revision);
    /// <summary>An administrator's expected claim, or an empty value to cancel.</summary>
    public sealed record PendingRequest(FirstSignInMatching Claim, string Value, long Revision);
}
