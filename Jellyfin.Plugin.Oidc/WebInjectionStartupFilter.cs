using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MediaBrowser.Controller.Authentication;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Oidc.Identity;
using Jellyfin.Plugin.Oidc.Configuration;

namespace Jellyfin.Plugin.Oidc;

/// <summary>Injects the small OIDC integration script into Jellyfin Web's index document only.</summary>
public sealed class WebInjectionStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var configuration = OidcPlugin.Instance?.Configuration;
                if (!HttpMethods.IsGet(context.Request.Method)
                    || !IsIndexRequest(context.Request.Path.Value)
                    || configuration is null)
                {
                    await nextMiddleware().ConfigureAwait(false);
                    return;
                }

                var enabled = configuration.Enabled && OidcConfigurationValidator.Validate(configuration) is null;
                var allPasswordsDisabled = enabled && configuration.PasswordLoginMode == PasswordLoginMode.DisableForAllUsers;
                var redirectsToProvider = allPasswordsDisabled && configuration.RedirectSignInPageToProvider;
                context.Request.Headers.Remove("Accept-Encoding");
                context.Request.Headers.Remove("Range");
                context.Request.Headers.Remove("If-Range");
                var originalBody = context.Response.Body;
                await using var bufferedBody = new MemoryStream();
                context.Response.Body = bufferedBody;
                await nextMiddleware().ConfigureAwait(false);
                context.Response.Body = originalBody;

                if (context.Response.StatusCode != StatusCodes.Status200OK
                    || context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) != true)
                {
                    bufferedBody.Position = 0;
                    await bufferedBody.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
                    return;
                }

                bufferedBody.Position = 0;
                using var reader = new StreamReader(bufferedBody, Encoding.UTF8, leaveOpen: true);
                var html = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
                if (enabled && context.Request.Query.TryGetValue("oidcTicket", out var ticket))
                {
                    context.Response.Headers["Referrer-Policy"] = "no-referrer";
                    var plugin = OidcPlugin.Instance!;
                    var created = false;
                    await plugin.ConfigurationGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (ReferenceEquals(configuration, plugin.Configuration) && context.RequestServices.GetRequiredService<OidcLoginStore>().TryTake(ticket.ToString(), out var userId, out _))
                        {
                            var session = await context.RequestServices.GetRequiredService<OidcSessionTracker>().CreateAsync(userId).ConfigureAwait(false);
                            html = html.Replace("</head>", SessionTag(session, PublicUrls.Get(context.Request, configuration)) + "</head>", StringComparison.OrdinalIgnoreCase);
                            created = true;
                        }
                    }
                    finally { plugin.ConfigurationGate.Release(); }
                    if (!created)
                    {
                        context.Response.ContentLength = null;
                        context.Response.Redirect(PublicUrls.Get(context.Request, configuration) + OidcConstants.WebIndexPath + "#!/login?oidcError=1");
                        return;
                    }
                }
                else if (redirectsToProvider && context.Request.Query.ContainsKey("oidcSignedOut"))
                {
                    html = html.Replace("</head>", "<style id=\"oidc-signed-out-style\">#loginPage{visibility:hidden}</style></head>", StringComparison.OrdinalIgnoreCase);
                }
                else if (redirectsToProvider && !context.Request.Query.ContainsKey("oidcError"))
                {
                    html = html.Replace("</head>", "<style id=\"oidc-login-redirect-style\">#loginPage{visibility:hidden}</style></head>", StringComparison.OrdinalIgnoreCase);
                }
                var scriptTag = $"<script>window.oidcEnabled={enabled.ToString().ToLowerInvariant()};window.oidcPasswordLoginMode='{(enabled ? configuration.PasswordLoginMode : PasswordLoginMode.AllowForAllUsers)}';window.oidcRedirectSignInPageToProvider={redirectsToProvider.ToString().ToLowerInvariant()};</script><script src=\"{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(context.Request.PathBase.Value ?? string.Empty)}/oidc/web.js?v={typeof(WebInjectionStartupFilter).Assembly.ManifestModule.ModuleVersionId}\"></script>";
                if (!html.Contains(scriptTag, StringComparison.Ordinal))
                {
                    html = html.Replace("</head>", scriptTag + "</head>", StringComparison.OrdinalIgnoreCase);
                }

                var bytes = Encoding.UTF8.GetBytes(html);
                context.Response.ContentLength = bytes.Length;
                context.Response.Headers.Remove("ETag");
                context.Response.Headers.Remove("Last-Modified");
                context.Response.Headers.Remove("Accept-Ranges");
                await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            });
            next(app);
        };
    }

    private static bool IsIndexRequest(string? path)
    {
        return path?.EndsWith(OidcConstants.WebIndexPath, StringComparison.OrdinalIgnoreCase) == true
            || path?.EndsWith("/web/", StringComparison.OrdinalIgnoreCase) == true
            || string.Equals(path, "/web", StringComparison.OrdinalIgnoreCase);
    }

    private static string SessionTag(AuthenticationResult session, string publicUrl)
    {
        var result = JsonSerializer.Serialize(session, JsonSerializerOptions.Web);
        var address = JsonSerializer.Serialize(publicUrl);
        return $"<script>const oidcSession={result};const oidcUser=oidcSession.user;const oidcServerId=oidcUser.serverId;const oidcPublicUrl={address};localStorage.setItem('_deviceId2',oidcSession.deviceId);oidcUser.enableAutoLogin=true;localStorage.setItem('user-'+oidcUser.id+'-'+oidcServerId,JSON.stringify(oidcUser));localStorage.setItem('jellyfin_credentials',JSON.stringify({{Servers:[{{Id:oidcServerId,ManualAddress:oidcPublicUrl,AccessToken:oidcSession.accessToken,UserId:oidcUser.id,DateLastAccessed:Date.now(),LastConnectionMode:2}}]}}));localStorage.setItem('enableAutoLogin','true');history.replaceState(null,'',oidcPublicUrl+'{OidcConstants.WebIndexPath}');</script>";
    }
}
