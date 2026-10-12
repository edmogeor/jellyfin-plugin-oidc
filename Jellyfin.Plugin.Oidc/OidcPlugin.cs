using Jellyfin.Plugin.Oidc.Configuration;
using Jellyfin.Plugin.Oidc.Identity;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Oidc;

/// <summary>The OIDC authentication plugin.</summary>
// Jellyfin instantiates plugins by reflection.
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class OidcPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<OidcPlugin> _logger;
    private readonly IUserManager _userManager;
    private readonly IOptionsMonitorCache<OpenIdConnectOptions> _optionsCache;

    /// <summary>Initializes a new instance of the <see cref="OidcPlugin"/> class.</summary>
    public OidcPlugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IUserManager userManager,
        IOptionsMonitorCache<OpenIdConnectOptions> optionsCache,
        ILogger<OidcPlugin> logger
    )
        : base(applicationPaths, xmlSerializer)
    {
        _userManager = userManager;
        _optionsCache = optionsCache;
        _logger = logger;
        Instance = this;
    }

    /// <summary>Gets the loaded plugin instance.</summary>
    public static OidcPlugin? Instance { get; private set; }

    // ponytail: serialize provisioning and configuration saves; move profile downloads outside the gate if saves become slow.
    internal SemaphoreSlim ConfigurationGate { get; } = new(1, 1);

    /// <inheritdoc />
    public override string Name => "OIDC Authentication";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4");

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is not PluginConfiguration oidcConfiguration)
        {
            throw new ArgumentException(
                "Expected OIDC plugin configuration.",
                nameof(configuration)
            );
        }

        var error = OidcConfigurationValidator.Validate(oidcConfiguration);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(configuration));
        }

        ConfigurationGate.Wait();
        try
        {
            // Browser configuration saves must not replace server-owned recovery or session state.
            oidcConfiguration.IdentityLinks = Configuration.IdentityLinks;
            oidcConfiguration.UserAdministration = Configuration.UserAdministration;
            oidcConfiguration.OidcSessions = Configuration.OidcSessions;
            oidcConfiguration.IdentityRevision = Configuration.IdentityRevision + 1;
            base.UpdateConfiguration(configuration);
            _optionsCache.TryRemove(OidcOptions.Scheme);
            foreach (var user in _userManager.GetUsers())
            {
                PasswordLoginEnforcer
                    .EnforceAsync(_userManager, user, oidcConfiguration, _logger)
                    .GetAwaiter()
                    .GetResult();
            }
        }
        finally
        {
            ConfigurationGate.Release();
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
        [
            new()
            {
                Name = Name,
                DisplayName = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "vpn_key",
            },
        ];
}
