using Jellyfin.Plugin.Oidc.Configuration;
using Jellyfin.Plugin.Oidc.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Jellyfin.Plugin.Oidc.Api;

/// <summary>Starts OIDC sign-in and converts an approved browser flow into a Jellyfin session.</summary>
[ApiController]
[Route("oidc")]
public sealed class OidcController : ControllerBase
{
    private readonly IOptionsMonitor<OpenIdConnectOptions> _options;
    private readonly IDataProtector _logoutTokenProtector;
    private readonly ILogger<OidcController> _logger;

    /// <summary>Initializes a new instance of the <see cref="OidcController"/> class.</summary>
    public OidcController(IOptionsMonitor<OpenIdConnectOptions> options, IDataProtectionProvider dataProtectionProvider, ILogger<OidcController> logger)
    {
        _options = options;
        _logoutTokenProtector = dataProtectionProvider.CreateProtector(OidcConstants.LogoutTokenProtectorPurpose);
        _logger = logger;
    }

    /// <summary>Returns the non-secret settings needed by Jellyfin Web's injected script.</summary>
    [HttpGet("config")]
    public ActionResult<WebConfiguration> Config()
    {
        Response.Headers.CacheControl = "no-store";
        var configuration = EnabledConfiguration();
        if (configuration is null)
        {
            return NotFound();
        }

        var redirectsToProvider = configuration.PasswordLoginMode == PasswordLoginMode.DisableForAllUsers
            && configuration.RedirectSignInPageToProvider;
        return new WebConfiguration(configuration.ProviderName, configuration.PasswordLoginMode.ToString(), redirectsToProvider, configuration.RpInitiatedLogout, configuration.Provider.ToString());
    }

    /// <summary>Serves the small Jellyfin Web integration script.</summary>
    [HttpGet("web.js")]
    [Produces("application/javascript")]
    public IActionResult WebScript()
    {
        var resourceName = "Jellyfin.Plugin.Oidc.Web.oidc.js";
        using var stream = typeof(OidcController).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "application/javascript");
    }

    /// <summary>Serves a bundled monochrome Identity Provider logo.</summary>
    [AllowAnonymous]
    [HttpGet("icons/{provider}.svg")]
    [Produces("image/svg+xml")]
    public IActionResult Icon(IdentityProviderType provider)
    {
        var stream = typeof(OidcController).Assembly.GetManifestResourceStream($"Jellyfin.Plugin.Oidc.Web.Providers.{provider}.svg");
        if (stream is null) return NotFound();
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(stream, "image/svg+xml");
    }

    /// <summary>Serves a plugin configuration-page translation dictionary.</summary>
    [AllowAnonymous]
    [HttpGet("strings/{locale}")]
    [Produces("application/json")]
    public IActionResult Strings(string locale)
    {
        var resourceName = $"Jellyfin.Plugin.Oidc.Configuration.Strings.{locale}.json";
        using var stream = typeof(OidcController).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return NotFound();
        using var english = typeof(OidcController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Oidc.Configuration.Strings.en-us.json")!;
        var dictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(english)!;
        foreach (var entry in JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!) dictionary[entry.Key] = entry.Value;
        return new JsonResult(dictionary);
    }

    /// <summary>Starts the authorization-code flow.</summary>
    [HttpGet("start")]
    public IActionResult Start([FromQuery] string? returnUrl = null, [FromQuery] bool rememberMe = true)
    {
        var configuration = EnabledConfiguration();
        if (configuration is null)
        {
            return NotFound();
        }

        if (!PublicUrls.UsesHttps(Request, configuration))
        {
            _logger.LogWarning("OIDC sign-in start rejected because the public request URL is not HTTPS.");
            return BadRequest("OIDC sign-in requires an HTTPS public URL.");
        }

        return Challenge(new AuthenticationProperties { RedirectUri = ReturnUrls.Local(returnUrl) ? returnUrl : "/home", Items = { ["oidc_revision"] = configuration.IdentityRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), ["oidc_remember"] = rememberMe.ToString() } }, OidcOptions.Scheme);
    }

    /// <summary>Ends the Identity Provider session after Jellyfin Web has ended its own session.</summary>
    [AllowAnonymous]
    [HttpGet("logout")]
    public async Task<IActionResult> Logout()
    {
        Response.Headers.CacheControl = "no-store";
        var cookies = new ChunkingCookieManager();
        var protectedIdToken = cookies.GetRequestCookie(HttpContext, OidcConstants.LogoutCookieName);
        var configuration = OidcPlugin.Instance?.Configuration;
        cookies.DeleteCookie(HttpContext, OidcConstants.LogoutCookieName, new CookieOptions
        {
            Path = configuration is null ? "/oidc" : PublicUrls.OidcPath(Request, configuration),
        });
        var loginPage = configuration is null
            ? OidcConstants.WebIndexPath
            : PublicUrls.LogoutReturnUrl(Request, configuration);
        var redirectsToProvider = configuration is { PasswordLoginMode: PasswordLoginMode.DisableForAllUsers, RedirectSignInPageToProvider: true };
        var localLogoutPage = redirectsToProvider
            ? loginPage + "?oidcSignedOut=1#!/login"
            : loginPage + "#!/login";
        if (configuration is null || !configuration.RpInitiatedLogout || string.IsNullOrEmpty(protectedIdToken))
        {
            return Redirect(localLogoutPage);
        }

        try
        {
            var idToken = _logoutTokenProtector.Unprotect(protectedIdToken);
            var provider = await _options.Get(OidcOptions.Scheme).ConfigurationManager!.GetConfigurationAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(provider.EndSessionEndpoint))
            {
                return Redirect(localLogoutPage);
            }

            var parameters = new Dictionary<string, string?>
            {
                ["client_id"] = configuration.ClientId,
                ["id_token_hint"] = idToken,
            };
            if (!redirectsToProvider)
            {
                parameters["post_logout_redirect_uri"] = loginPage;
            }

            return Redirect(QueryHelpers.AddQueryString(provider.EndSessionEndpoint, parameters));
        }
        catch
        {
            return Redirect(localLogoutPage);
        }
    }

    private static PluginConfiguration? EnabledConfiguration()
    {
        var configuration = OidcPlugin.Instance?.Configuration;
        return configuration is not null && configuration.Enabled && OidcConfigurationValidator.Validate(configuration) is null
            ? configuration
            : null;
    }

    /// <summary>Non-secret settings used by the browser integration.</summary>
    // JSON serialization reads these endpoint fields.
    // ReSharper disable NotAccessedPositionalProperty.Global
    public sealed record WebConfiguration(string ProviderName, string PasswordLoginMode, bool RedirectSignInPageToProvider, bool RpInitiatedLogout, string Provider);
    // ReSharper restore NotAccessedPositionalProperty.Global
}
