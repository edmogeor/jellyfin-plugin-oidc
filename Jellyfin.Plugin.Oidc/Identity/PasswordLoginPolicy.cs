using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Oidc.Configuration;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Reconciles password availability at startup and when Jellyfin creates a user.</summary>
public sealed class PasswordLoginPolicy(IUserManager users, ILogger<PasswordLoginPolicy> logger)
    : IHostedService, IEventConsumer<UserCreatedEventArgs>
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var plugin = OidcPlugin.Instance;
        if (plugin?.Configuration.Enabled != true) return;
        await plugin.ConfigurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var user in users.GetUsers())
                await PasswordLoginEnforcer.EnforceAsync(users, user, plugin.Configuration, logger).ConfigureAwait(false);
        }
        finally { plugin.ConfigurationGate.Release(); }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        var plugin = OidcPlugin.Instance;
        if (plugin is null || plugin.Configuration.PasswordLoginMode != PasswordLoginMode.DisableForAllUsers
            || eventArgs.Argument.AuthenticationProviderId == typeof(OidcPasswordDisabledProvider).FullName) return;
        await plugin.ConfigurationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var user = users.GetUserById(eventArgs.Argument.Id);
            if (user is not null)
                await PasswordLoginEnforcer.EnforceAsync(users, user, plugin.Configuration, logger).ConfigureAwait(false);
        }
        finally { plugin.ConfigurationGate.Release(); }
    }
}
