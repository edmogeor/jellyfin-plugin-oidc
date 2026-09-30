using System.Globalization;
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
    /// <summary>Serializes configuration replacement and identity operations within this Jellyfin process.</summary>
    public static SemaphoreSlim IdentityGate { get; } = new(1, 1);
    private readonly ILogger<OidcPlugin> _logger;
    private readonly IUserManager _userManager;
    private readonly IOptionsMonitorCache<OpenIdConnectOptions> _optionsCache;

    /// <summary>Initializes a new instance of the <see cref="OidcPlugin"/> class.</summary>
    public OidcPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IUserManager userManager, IOptionsMonitorCache<OpenIdConnectOptions> optionsCache, ILogger<OidcPlugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _userManager = userManager;
        _optionsCache = optionsCache;
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

        IdentityGate.Wait();
        try
        {
            // Identity records are server-owned. A stale dashboard save cannot replace them.
            var state = Snapshot();
            oidcConfiguration.IdentityLinks = state.IdentityLinks;
            foreach (var link in oidcConfiguration.IdentityLinks.Where(link => string.IsNullOrEmpty(link.Issuer))) link.Issuer = Configuration.IssuerUrl.TrimEnd('/');
            oidcConfiguration.UnlinkOptOuts = state.UnlinkOptOuts;
            oidcConfiguration.PendingAdminMatches = state.PendingAdminMatches;
            oidcConfiguration.IdentityRevision = state.IdentityRevision;
            Commit(oidcConfiguration);
            ConfigurationChanged?.Invoke(this, oidcConfiguration);
            _optionsCache.TryRemove(OidcOptions.Scheme);
            foreach (var user in _userManager.GetUsers())
            {
                PasswordLoginEnforcer.EnforceAsync(_userManager, user, oidcConfiguration, _logger).GetAwaiter().GetResult();
            }
        }
        finally
        {
            IdentityGate.Release();
        }
    }

    /// <inheritdoc />
    public override void SaveConfiguration(PluginConfiguration config)
    {
        var path = ConfigurationFilePath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            XmlSerializer.SerializeToFile(config, path);
            File.Move(path, ConfigurationFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>Persists a staged configuration before publishing it. Caller holds IdentityGate.</summary>
    public void Commit(PluginConfiguration configuration)
    {
        SaveConfiguration(configuration);
        Configuration = configuration;
    }

    /// <summary>Creates an isolated working copy for an atomic identity update.</summary>
    public PluginConfiguration Snapshot() => System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>(System.Text.Json.JsonSerializer.Serialize(Configuration))!;

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
