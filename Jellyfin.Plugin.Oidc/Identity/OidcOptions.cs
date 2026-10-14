using System.Security.Claims;
using System.Security.Cryptography;
using Jellyfin.Plugin.Oidc.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Configures the single OIDC handler from plugin configuration.</summary>
public sealed class OidcOptions(ILogger<OidcOptions> logger)
    : IConfigureNamedOptions<OpenIdConnectOptions>
{
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
        options.CallbackPath = OidcConstants.CallbackPath;
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
            context.ProtocolMessage.RedirectUri =
                PublicUrls.Get(context.Request, configuration) + OidcConstants.CallbackPath;
            if (context.Properties.Items.ContainsKey("oidc_link"))
            {
                context.ProtocolMessage.Prompt = "login";
            }
            return Task.CompletedTask;
        };
        options.Events.OnMessageReceived = context =>
        {
            if (PublicUrls.UsesHttps(context.Request, configuration))
            {
                return Task.CompletedTask;
            }

            logger.LogWarning(
                "OIDC callback rejected because the public request URL is not HTTPS."
            );
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.HandleResponse();
            return context.Response.WriteAsync("OIDC sign-in requires an HTTPS public URL.");
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
            CopyUserInfoClaim(
                context.User.RootElement,
                identity,
                configuration.UsernameClaim.Trim()
            );
            CopyUserInfoClaim(context.User.RootElement, identity, configuration.GroupClaim.Trim());
            CopyUserInfoClaim(context.User.RootElement, identity, "picture");
            return Task.CompletedTask;
        };
        options.Events.OnTicketReceived = async context =>
        {
            if (context.Properties?.Items.TryGetValue("oidc_link", out var transaction) == true)
            {
                var completion = transaction is null
                    ? null
                    : context
                        .HttpContext.RequestServices.GetRequiredService<OidcLinkStore>()
                        .Validate(transaction, context.Principal!, configuration);
                var profileRoute = ReturnUrls.Local(context.ReturnUri)
                    ? context.ReturnUri!
                    : "/userprofile";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Redirect(
                    PublicUrls.Get(context.Request, configuration)
                        + OidcConstants.WebIndexPath
                        + (
                            completion is null
                                ? "?oidcLinkError=1"
                                : "?oidcLinkTicket=" + Uri.EscapeDataString(completion)
                        )
                        + "#!"
                        + profileRoute
                );
                context.HandleResponse();
                return;
            }
            var provisioner =
                context.HttpContext.RequestServices.GetRequiredService<OidcUserProvisioner>();
            var revision =
                context.Properties?.Items.TryGetValue("oidc_revision", out var value) == true
                && long.TryParse(value, out var parsed)
                    ? parsed
                    : -1;
            var result = await provisioner
                .ProvisionAsync(context.Principal!, configuration, revision)
                .ConfigureAwait(false);
            var returnUrl = context.ReturnUri;
            var browserNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var rememberMe =
                context.Properties?.Items.TryGetValue("oidc_remember", out var remember) == true
                && bool.TryParse(remember, out var parsedRemember)
                && parsedRemember;
            var ticket = result is null
                ? null
                : context
                    .HttpContext.RequestServices.GetRequiredService<OidcLoginStore>()
                    .Create(
                        result.Value,
                        ReturnUrls.Local(returnUrl) ? returnUrl! : "/home",
                        browserNonce,
                        rememberMe,
                        revision
                    );
            if (string.IsNullOrEmpty(ticket))
            {
                context.Response.Redirect(
                    PublicUrls.Get(context.Request, configuration)
                        + OidcConstants.WebIndexPath
                        + "#!/login?oidcError=1"
                );
                context.HandleResponse();
                return;
            }

            context.Response.Cookies.Append(
                OidcLoginStore.CookieName(ticket),
                browserNonce,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    Path = PublicUrls.WebPath(context.Request, configuration),
                    MaxAge = TimeSpan.FromMinutes(5),
                }
            );
            if (
                context.Properties!.Items.Remove("oidc_id_token", out var idToken)
                && !string.IsNullOrEmpty(idToken)
            )
            {
                var protector = context
                    .HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector(OidcConstants.LogoutTokenProtectorPurpose);
                new ChunkingCookieManager().AppendResponseCookie(
                    context.HttpContext,
                    OidcConstants.LogoutCookieName,
                    protector.Protect(idToken),
                    new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = true,
                        SameSite = SameSiteMode.Lax,
                        Path = PublicUrls.OidcPath(context.Request, configuration),
                    }
                );
            }

            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Redirect(
                PublicUrls.Get(context.Request, configuration)
                    + OidcConstants.WebIndexPath
                    + "?oidcTicket="
                    + Uri.EscapeDataString(ticket)
            );
            context.HandleResponse();
        };
        options.Events.OnRemoteFailure = context =>
        {
            logger.LogWarning("OIDC remote authentication failed.");
            if (context.Properties?.Items.ContainsKey("oidc_link") == true)
            {
                var profileRoute = ReturnUrls.Local(context.Properties.RedirectUri)
                    ? context.Properties.RedirectUri!
                    : "/userprofile";
                context.Response.Redirect(
                    PublicUrls.Get(context.Request, configuration)
                        + OidcConstants.WebIndexPath
                        + "?oidcLinkError=1#!"
                        + profileRoute
                );
                context.HandleResponse();
                return Task.CompletedTask;
            }
            context.Response.Redirect(
                PublicUrls.Get(context.Request, configuration)
                    + OidcConstants.WebIndexPath
                    + "?oidcError=1#!/login"
            );
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }

    private static void CopyUserInfoClaim(
        System.Text.Json.JsonElement userInfo,
        ClaimsIdentity identity,
        string claimType
    )
    {
        if (!userInfo.TryGetProperty(claimType, out var value))
        {
            return;
        }

        foreach (var claim in identity.FindAll(claimType).ToList())
        {
            identity.RemoveClaim(claim);
        }

        identity.AddClaim(
            new Claim(
                claimType,
                value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? value.GetString()!
                    : value.GetRawText(),
                value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? ClaimValueTypes.String
                    : "JSON"
            )
        );
    }

    /// <summary>Gets the standard scopes plus administrator-configured provider scopes.</summary>
    public static IEnumerable<string> RequestedScopes(PluginConfiguration configuration) =>
        new[] { "openid", "email", "profile" }
            .Concat(
                configuration.AdditionalScopes.Split(
                    ' ',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
                )
            )
            .Distinct(StringComparer.Ordinal);
}

/// <summary>Validates navigation targets before returning to Jellyfin Web.</summary>
public static class ReturnUrls
{
    /// <summary>Returns whether a URL is a local absolute path.</summary>
    public static bool Local(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith("/", StringComparison.Ordinal)
        && !value.StartsWith("//", StringComparison.Ordinal)
        && !value.Contains("\\", StringComparison.Ordinal);
}

/// <summary>Resolves the public origin, using an explicit proxy override when configured.</summary>
public static class PublicUrls
{
    /// <summary>Returns whether the configured or request-derived public URL uses HTTPS.</summary>
    public static bool UsesHttps(HttpRequest request, PluginConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.PublicUrl)
            ? Uri.TryCreate(configuration.PublicUrl, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
            : request.IsHttps;

    /// <summary>Gets the public origin for a browser request.</summary>
    public static string Get(HttpRequest request, PluginConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration.PublicUrl)
            ? $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/')
            : configuration.PublicUrl.TrimEnd('/');

    /// <summary>Gets the registered post-logout return URL.</summary>
    public static string LogoutReturnUrl(HttpRequest request, PluginConfiguration configuration) =>
        Get(request, configuration) + OidcConstants.WebIndexPath;

    /// <summary>Gets the OIDC cookie path for the public Jellyfin URL.</summary>
    public static string OidcPath(HttpRequest request, PluginConfiguration configuration) =>
        new Uri(Get(request, configuration)).AbsolutePath.TrimEnd('/') + "/oidc";

    /// <summary>Gets the browser handoff cookie path.</summary>
    public static string WebPath(HttpRequest request, PluginConfiguration configuration) =>
        new Uri(Get(request, configuration)).AbsolutePath.TrimEnd('/') + "/web";
}
