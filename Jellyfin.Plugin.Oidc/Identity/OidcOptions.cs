using Jellyfin.Plugin.Oidc.Configuration;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Configures the single OIDC handler from plugin configuration.</summary>
public sealed class OidcOptions : IConfigureNamedOptions<OpenIdConnectOptions>
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

        // ASP.NET Core validates every registered handler even while OIDC is disabled. These
        // inert HTTPS values keep the handler valid; the start endpoint remains unavailable.
        options.Authority = "https://disabled.invalid";
        options.ClientId = "disabled";

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
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("profile");

        options.Events.OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.RedirectUri = PublicUrls.Get(context.Request, configuration) + "/oidc/callback";
            return Task.CompletedTask;
        };
        options.Events.OnTokenValidated = async context =>
        {
            var provisioner = context.HttpContext.RequestServices.GetRequiredService<OidcUserProvisioner>();
            var result = await provisioner.ProvisionAsync(context.Principal!).ConfigureAwait(false);
            if (result is null)
            {
                context.Fail("Sign-in not permitted.");
                return;
            }

            if (!string.IsNullOrEmpty(context.TokenEndpointResponse?.IdToken))
            {
                var protector = context.HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("Jellyfin.Plugin.Oidc.LogoutIdToken.v1");
                context.Response.Cookies.Append("oidc_logout", protector.Protect(context.TokenEndpointResponse.IdToken), new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    Path = "/oidc",
                });
            }

            var returnUrl = context.Properties?.RedirectUri;
            var ticket = context.HttpContext.RequestServices.GetRequiredService<OidcLoginStore>()
                .Create(result.Value, ReturnUrls.Local(returnUrl) ? returnUrl! : "/");
            context.Properties!.Items["oidc_ticket"] = ticket;
        };
        options.Events.OnTicketReceived = context =>
        {
            var ticket = context.Properties?.Items["oidc_ticket"];
            if (string.IsNullOrEmpty(ticket))
            {
                context.Fail("Sign-in not permitted.");
                return Task.CompletedTask;
            }

            context.Response.Redirect(PublicUrls.Get(context.Request, configuration) + "/web/index.html?oidcTicket=" + Uri.EscapeDataString(ticket));
            context.HandleResponse();
            return Task.CompletedTask;
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.Response.Redirect(PublicUrls.Get(context.Request, configuration) + "/web/index.html#!/login?oidcError=1");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }
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
}
