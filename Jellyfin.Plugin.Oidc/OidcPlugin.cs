using System.Globalization;
using Jellyfin.Plugin.Oidc.Configuration;
using Jellyfin.Plugin.Oidc.Identity;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oidc;

/// <summary>The OIDC authentication plugin.</summary>
// Jellyfin instantiates plugins by reflection.
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class OidcPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<OidcPlugin> _logger;
    private readonly IUserManager _userManager;

    /// <summary>Initializes a new instance of the <see cref="OidcPlugin"/> class.</summary>
    public OidcPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IUserManager userManager, ILogger<OidcPlugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _userManager = userManager;
        _logger = logger;
        Instance = this;
    }

    /// <summary>Gets the loaded plugin instance.</summary>
    public static OidcPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "OIDC Authentication";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("4c5b9b96-80cd-4c3d-9e3d-23fa4ebf6ce4");

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is not PluginConfiguration oidcConfiguration)
        {
            throw new ArgumentException("Expected OIDC plugin configuration.", nameof(configuration));
        }

        var error = OidcConfigurationValidator.Validate(oidcConfiguration);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(configuration));
        }

        base.UpdateConfiguration(configuration);
        foreach (var user in _userManager.GetUsers())
        {
            PasswordLoginEnforcer.EnforceAsync(_userManager, user, oidcConfiguration, _logger).GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new()
        {
            Name = Name,
            DisplayName = Name,
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "vpn_key",
        },
    ];
}
