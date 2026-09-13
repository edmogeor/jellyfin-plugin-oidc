using Jellyfin.Plugin.Oidc.Configuration;
using Jellyfin.Plugin.Oidc.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        var configuration = EnabledConfiguration();
        if (configuration is null)
        {
            return NotFound();
        }

        return new WebConfiguration(configuration.LoginButtonText, configuration.PasswordLoginMode.ToString(), configuration.RpInitiatedLogout);
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

    /// <summary>Serves a plugin configuration-page translation dictionary.</summary>
    [AllowAnonymous]
    [HttpGet("strings/{locale}")]
    [Produces("application/json")]
    public IActionResult Strings(string locale)
    {
        var resourceName = $"Jellyfin.Plugin.Oidc.Configuration.Strings.{locale}.json";
        var stream = typeof(OidcController).Assembly.GetManifestResourceStream(resourceName);
        return stream is null ? NotFound() : File(stream, "application/json");
    }

    /// <summary>Starts the authorization-code flow.</summary>
    [HttpGet("start")]
    public IActionResult Start([FromQuery] string? returnUrl = null)
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

        return Challenge(new AuthenticationProperties { RedirectUri = ReturnUrls.Local(returnUrl) ? returnUrl : "/" }, OidcOptions.Scheme);
    }

    /// <summary>Ends the Identity Provider session after Jellyfin Web has ended its own session.</summary>
    [AllowAnonymous]
    [HttpGet("logout")]
    public async Task<IActionResult> Logout()
    {
        var protectedIdToken = Request.Cookies[OidcConstants.LogoutCookieName];
        var configuration = OidcPlugin.Instance?.Configuration;
        Response.Cookies.Delete(OidcConstants.LogoutCookieName, new CookieOptions
        {
            Path = configuration is null ? "/oidc" : PublicUrls.OidcPath(Request, configuration),
        });
        if (configuration is null || !configuration.RpInitiatedLogout || string.IsNullOrEmpty(protectedIdToken))
        {
            var loginReturn = configuration is null
                ? OidcConstants.WebIndexPath + "#!/login"
                : PublicUrls.LogoutReturnUrl(Request, configuration) + "#!/login";
            return Redirect(loginReturn);
        }

        var loginPage = PublicUrls.LogoutReturnUrl(Request, configuration);

        try
        {
            var idToken = _logoutTokenProtector.Unprotect(protectedIdToken);
            var provider = await _options.Get(OidcOptions.Scheme).ConfigurationManager!.GetConfigurationAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(provider.EndSessionEndpoint))
            {
                return Redirect(loginPage);
            }

            return Redirect(QueryHelpers.AddQueryString(provider.EndSessionEndpoint, new Dictionary<string, string?>
            {
                ["client_id"] = configuration.ClientId,
                ["id_token_hint"] = idToken,
                ["post_logout_redirect_uri"] = loginPage,
            }));
        }
        catch
        {
            return Redirect(loginPage);
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
    public sealed record WebConfiguration(string LoginButtonText, string PasswordLoginMode, bool RpInitiatedLogout);
    // ReSharper restore NotAccessedPositionalProperty.Global
}
