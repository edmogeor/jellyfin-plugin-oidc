using System.Security.Claims;
using Jellyfin.Plugin.Oidc.Configuration;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Configures the single OIDC handler from plugin configuration.</summary>
public sealed class OidcOptions : IConfigureNamedOptions<OpenIdConnectOptions>
{
    private readonly ILogger<OidcOptions> _logger;

    /// <summary>Initializes a new instance of the <see cref="OidcOptions"/> class.</summary>
    public OidcOptions(ILogger<OidcOptions> logger) => _logger = logger;

    /// <summary>The dedicated plugin authentication scheme.</summary>
    public const string Scheme = "JellyfinOidc";

    /// <inheritdoc />
    public void Configure(OpenIdConnectOptions options) => Configure(Scheme, options);

    /// <inheritdoc />
    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (!string.Equals(name, Scheme, StringComparison.Ordinal))
        {
            return;
        }

        var configuration = OidcPlugin.Instance?.Configuration;
        if (configuration is null || OidcConfigurationValidator.Validate(configuration) is not null)
        {
            return;
        }

        options.Authority = configuration.IssuerUrl.TrimEnd('/');
        options.ClientId = configuration.ClientId.Trim();
        options.ClientSecret = configuration.ClientSecret;
        options.CallbackPath = "/oidc/callback";
        options.MapInboundClaims = false;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.SaveTokens = false;
        options.RequireHttpsMetadata = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.Scope.Clear();
        foreach (var scope in RequestedScopes(configuration))
        {
            options.Scope.Add(scope);
        }

        options.Events.OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.RedirectUri = PublicUrls.Get(context.Request, configuration) + "/oidc/callback";
            return Task.CompletedTask;
        };
        options.Events.OnTokenValidated = context =>
        {
            if (!string.IsNullOrEmpty(context.TokenEndpointResponse?.IdToken))
            {
                context.Properties!.Items["oidc_id_token"] = context.TokenEndpointResponse.IdToken;
            }

            return Task.CompletedTask;
        };
        options.Events.OnUserInformationReceived = context =>
        {
            var identity = (ClaimsIdentity)context.Principal!.Identity!;
            CopyUserInfoClaim(context.User.RootElement, identity, "email");
            CopyUserInfoClaim(context.User.RootElement, identity, "email_verified");
            CopyUserInfoClaim(context.User.RootElement, identity, configuration.GroupClaim.Trim());
            CopyUserInfoClaim(context.User.RootElement, identity, "picture");
            return Task.CompletedTask;
        };
        options.Events.OnTicketReceived = async context =>
        {
            var provisioner = context.HttpContext.RequestServices.GetRequiredService<OidcUserProvisioner>();
            var result = await provisioner.ProvisionAsync(context.Principal!).ConfigureAwait(false);
            var returnUrl = context.Properties?.RedirectUri;
            var ticket = result is null ? null : context.HttpContext.RequestServices.GetRequiredService<OidcLoginStore>()
                .Create(result.Value, ReturnUrls.Local(returnUrl) ? returnUrl! : "/");
            if (string.IsNullOrEmpty(ticket))
            {
                context.Response.Redirect(PublicUrls.Get(context.Request, configuration) + "/web/index.html#!/login?oidcError=1");
                context.HandleResponse();
                return;
            }

            if (context.Properties!.Items.Remove("oidc_id_token", out var idToken) && !string.IsNullOrEmpty(idToken))
            {
                var protector = context.HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("Jellyfin.Plugin.Oidc.LogoutIdToken.v1");
                context.Response.Cookies.Append("oidc_logout", protector.Protect(idToken), new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    Path = PublicUrls.OidcPath(context.Request, configuration),
                });
            }

            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Redirect(PublicUrls.Get(context.Request, configuration) + "/web/index.html?oidcTicket=" + Uri.EscapeDataString(ticket));
            context.HandleResponse();
        };
        options.Events.OnRemoteFailure = context =>
        {
            _logger.LogWarning("OIDC remote authentication failed.");
            context.Response.Redirect(PublicUrls.Get(context.Request, configuration) + "/web/index.html#!/login?oidcError=1");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }

    private static void CopyUserInfoClaim(System.Text.Json.JsonElement userInfo, ClaimsIdentity identity, string claimType)
    {
        if (!userInfo.TryGetProperty(claimType, out var value))
        {
            return;
        }

        foreach (var claim in identity.FindAll(claimType).ToList())
        {
            identity.RemoveClaim(claim);
        }

        identity.AddClaim(new Claim(claimType, value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString()! : value.GetRawText()));
    }

    /// <summary>Gets the standard scopes plus administrator-configured provider scopes.</summary>
    public static IEnumerable<string> RequestedScopes(PluginConfiguration configuration)
        => new[] { "openid", "email", "profile" }
            .Concat(configuration.AdditionalScopes.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal);
}

/// <summary>Validates navigation targets before returning to Jellyfin Web.</summary>
public static class ReturnUrls
{
    /// <summary>Returns whether a URL is a local absolute path.</summary>
    public static bool Local(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.StartsWith("/", StringComparison.Ordinal)
            && !value.StartsWith("//", StringComparison.Ordinal)
            && !value.Contains("\\", StringComparison.Ordinal);
}

/// <summary>Resolves the public origin, using an explicit proxy override when configured.</summary>
public static class PublicUrls
{
    /// <summary>Gets the public origin for a browser request.</summary>
    public static string Get(Microsoft.AspNetCore.Http.HttpRequest request, PluginConfiguration configuration)
        => string.IsNullOrWhiteSpace(configuration.PublicUrl)
            ? $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/')
            : configuration.PublicUrl.TrimEnd('/');

    /// <summary>Gets the registered post-logout return URL.</summary>
    public static string LogoutReturnUrl(Microsoft.AspNetCore.Http.HttpRequest request, PluginConfiguration configuration)
        => Get(request, configuration) + "/web/index.html";

    /// <summary>Gets the OIDC cookie path for the public Jellyfin URL.</summary>
    public static string OidcPath(Microsoft.AspNetCore.Http.HttpRequest request, PluginConfiguration configuration)
        => new Uri(Get(request, configuration)).AbsolutePath.TrimEnd('/') + "/oidc";
}
