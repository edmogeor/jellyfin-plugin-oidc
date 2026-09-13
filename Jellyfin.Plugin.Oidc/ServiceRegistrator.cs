using Jellyfin.Plugin.Oidc.Identity;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Oidc;

/// <summary>Registers OIDC services at Jellyfin startup.</summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<OidcLoginStore>();
        services.AddSingleton<OidcUserProvisioner>();
        services.AddSingleton<IAuthenticationProvider, OidcPasswordDisabledProvider>();
        services.AddSingleton<IStartupFilter, WebInjectionStartupFilter>();
        services.AddSingleton<IConfigureOptions<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>, OidcOptions>();
        services.PostConfigure<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>(OidcOptions.Scheme, options =>
        {
            if (string.IsNullOrEmpty(options.Authority))
            {
                options.Authority = "https://disabled.invalid";
            }

            if (string.IsNullOrEmpty(options.ClientId))
            {
                options.ClientId = "disabled";
            }
        });
        services.AddAuthentication().AddOpenIdConnect(OidcOptions.Scheme, _ => { });
    }
}
