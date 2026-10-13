using Jellyfin.Plugin.Oidc.Identity;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;

namespace Jellyfin.Plugin.Oidc;

/// <summary>Registers OIDC services at Jellyfin startup.</summary>
// Jellyfin discovers service registrators by reflection.
// ReSharper disable once UnusedType.Global
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<OidcLoginStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<OidcLinkStore>();
        services.AddSingleton<OidcProfileImageSynchronizer>();
        services.AddSingleton<OidcUserProvisioner>();
        services.AddSingleton<OidcSessionTracker>();
        services.AddSingleton<OidcLinkManager>();
        services.AddSingleton<IAuthenticationProvider, OidcPasswordDisabledProvider>();
        services.AddSingleton<PasswordLoginPolicy>();
        services.AddHostedService(provider => provider.GetRequiredService<PasswordLoginPolicy>());
        services.AddSingleton<IEventConsumer<UserCreatedEventArgs>>(provider => provider.GetRequiredService<PasswordLoginPolicy>());
        services.AddSingleton<IStartupFilter, WebInjectionStartupFilter>();
        services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, OidcOptions>();
        services.PostConfigure<OpenIdConnectOptions>(OidcOptions.Scheme, options =>
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
