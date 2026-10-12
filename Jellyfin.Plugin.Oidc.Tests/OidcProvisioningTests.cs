using System.Reflection;
using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Oidc.Configuration;
using Jellyfin.Plugin.Oidc.Identity;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jellyfin.Plugin.Oidc.Tests;

public sealed class OidcProvisioningTests
{
    [Theory]
    [InlineData(PasswordLoginMode.AllowForAllUsers)]
    [InlineData(PasswordLoginMode.DisableForLinkedUsersOnly)]
    [InlineData(PasswordLoginMode.DisableForAllUsers)]
    public async Task New_user_has_password_and_password_policy_at_first_database_save(PasswordLoginMode mode)
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.PasswordLoginMode = mode;
        User? firstSavedUser = null;
        fixture.BeforeSave = user =>
        {
            firstSavedUser = user;
            Assert.False(string.IsNullOrEmpty(user.Password));
            Assert.Equal(mode == PasswordLoginMode.AllowForAllUsers ? ProvisioningFixture.DefaultProvider : typeof(OidcPasswordDisabledProvider).FullName, user.AuthenticationProviderId);
        };

        var userId = await fixture.SignInAsync("subject");

        Assert.NotNull(firstSavedUser);
        Assert.Equal(firstSavedUser.Id, userId);
        Assert.Single(fixture.Users());
        Assert.Equal("user@example.test", fixture.Users().Single().Username);
    }

    [Fact]
    public async Task Password_hash_failure_does_not_create_a_user()
    {
        using var fixture = new ProvisioningFixture();
        fixture.Crypto.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SignInAsync("subject"));

        Assert.Empty(fixture.Users());
        Assert.Empty(fixture.Configuration.IdentityLinks);
    }

    [Fact]
    public async Task Database_save_failure_does_not_leave_a_passwordless_user()
    {
        using var fixture = new ProvisioningFixture();
        fixture.BeforeSave = _ => throw new InvalidOperationException("Injected database failure.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SignInAsync("subject"));

        Assert.Empty(fixture.Users());
        Assert.Empty(fixture.Configuration.IdentityLinks);
    }

    [Fact]
    public async Task Email_match_links_an_unlinked_user_but_rejects_another_subject()
    {
        using var fixture = new ProvisioningFixture();
        var original = await fixture.SeedUserAsync("USER@example.test");

        Assert.Equal(original.Id, await fixture.SignInAsync("original-subject"));
        Assert.Equal(original.Id, await fixture.SignInAsync("original-subject"));
        Assert.Null(await fixture.SignInAsync("different-subject"));
        var link = Assert.Single(fixture.Configuration.IdentityLinks);
        Assert.Equal("original-subject", link.Subject);
        Assert.Equal(original.Id, link.UserId);
        Assert.Single(fixture.Users());
    }

    [Fact]
    public async Task Configuration_update_during_sign_in_is_not_overwritten()
    {
        using var fixture = new ProvisioningFixture();
        await fixture.SeedUserAsync("user@example.test");
        var policyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPolicy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforePolicyUpdate = async () =>
        {
            policyStarted.SetResult();
            await finishPolicy.Task;
        };
        var signIn = fixture.SignInAsync("subject", administrator: true);
        await policyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var updateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disabled = new PluginConfiguration { Enabled = false };
        var plugin = fixture.Plugin;
        var update = Task.Run(() =>
        {
            updateStarted.SetResult();
            plugin.UpdateConfiguration(disabled);
        });

        try
        {
            await updateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(update.IsCompleted);
        }
        finally
        {
            finishPolicy.SetResult();
        }

        Assert.NotNull(await signIn.WaitAsync(TimeSpan.FromSeconds(10)));
        await update.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(disabled, fixture.Plugin.Configuration);
        Assert.Same(disabled, fixture.SavedConfiguration);
        Assert.Null(await fixture.SignInAsync("subject"));
    }

    [Fact]
    public async Task Callback_authenticated_with_old_configuration_is_denied()
    {
        using var fixture = new ProvisioningFixture();
        fixture.Plugin.UpdateConfiguration(new PluginConfiguration { Enabled = false });

        Assert.Null(await fixture.SignInAsync("subject"));
        Assert.Empty(fixture.Users());
    }

    private sealed class ProvisioningFixture : IDisposable, IDbContextFactory<JellyfinDbContext>
    {
        public const string DefaultProvider = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "oidc-provisioning-tests-" + Guid.NewGuid().ToString("N"));
        private readonly OidcUserProvisioner _provisioner;

        public ProvisioningFixture()
        {
            _connection.Open();
            using (var context = CreateDbContext())
            {
                context.Database.EnsureCreated();
            }
            var manager = Stub<IUserManager>((method, args) => method.Name switch
            {
                "GetUsers" => Users(),
                "GetUserById" => Users().SingleOrDefault(user => user.Id == (Guid)args![0]!),
                "GetUserDto" => new UserDto { Policy = new UserPolicy { IsAdministrator = ((User)args![0]!).HasPermission(PermissionKind.IsAdministrator) } },
                "UpdatePolicyAsync" => UpdatePolicyAsync((Guid)args![0]!, (UserPolicy)args[1]!),
                _ => throw new InvalidOperationException("Unexpected user-manager call: " + method.Name),
            });
            var paths = Stub<IApplicationPaths>((_, _) => _directory);
            var serializer = Stub<IXmlSerializer>((method, args) =>
            {
                if (method.Name == "DeserializeFromFile")
                {
                    return Configuration;
                }
                if (method.Name == "SerializeToFile")
                {
                    SavedConfiguration = (PluginConfiguration)args![0]!;
                    return null;
                }
                throw new InvalidOperationException("Unexpected serializer call: " + method.Name);
            });
            Plugin = new OidcPlugin(paths, serializer, manager, new OptionsCache<OpenIdConnectOptions>(), NullLogger<OidcPlugin>.Instance);
            var events = Stub<IEventManager>((method, _) => method.ReturnType == typeof(Task) ? Task.CompletedTask : null);
            _provisioner = new OidcUserProvisioner(manager, Crypto, null!, NullLogger<OidcUserProvisioner>.Instance, this, events);
        }

        public PluginConfiguration Configuration { get; } = new()
        {
            Enabled = true,
            IssuerUrl = "https://identity.example.test",
            ClientId = "client",
            ClientSecret = "test-only",
            UserGroup = "users",
            AdministratorGroup = "admins",
        };

        public OidcPlugin Plugin { get; }
        public PluginConfiguration? SavedConfiguration { get; private set; }
        public TestCrypto Crypto { get; } = new();
        public Action<User>? BeforeSave { get; set; }
        public Func<Task>? BeforePolicyUpdate { get; set; }

        public Task<Guid?> SignInAsync(string subject, bool administrator = false) => _provisioner.ProvisionAsync(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", subject),
            new Claim("email", "user@example.test"),
            new Claim("email_verified", "true"),
            new Claim("groups", administrator ? "admins" : "users"),
        ])), Configuration);

        public User[] Users()
        {
            using var context = CreateDbContext();
            return context.Users.Include(user => user.Permissions).AsNoTracking().ToArray();
        }

        public async Task<User> SeedUserAsync(string username)
        {
            await using var context = CreateDbContext();
            var user = new User(username, DefaultProvider, "reset") { Password = "existing-password" };
            user.AddDefaultPermissions();
            user.AddDefaultPreferences();
            context.Users.Add(user);
            await context.SaveChangesAsync();
            return user;
        }

        private async Task UpdatePolicyAsync(Guid userId, UserPolicy policy)
        {
            if (BeforePolicyUpdate is not null)
            {
                await BeforePolicyUpdate();
            }
            await using var context = CreateDbContext();
            var user = await context.Users.Include(user => user.Permissions).SingleAsync(user => user.Id == userId);
            user.SetPermission(PermissionKind.IsAdministrator, policy.IsAdministrator);
            user.AuthenticationProviderId = policy.AuthenticationProviderId ?? user.AuthenticationProviderId;
            await context.SaveChangesAsync();
        }

        public JellyfinDbContext CreateDbContext() => new TestDbContext(new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options, this);

        public void Dispose()
        {
            _connection.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class TestDbContext(DbContextOptions<JellyfinDbContext> options, ProvisioningFixture fixture)
        : JellyfinDbContext(options, NullLogger<JellyfinDbContext>.Instance, null!, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance))
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(entity => entity.ClrType != typeof(User) && entity.ClrType != typeof(Permission) && entity.ClrType != typeof(Preference)).ToArray())
            {
                modelBuilder.Ignore(entity.ClrType);
            }
            modelBuilder.Entity<User>().Ignore(user => user.ProfileImage).Ignore(user => user.AccessSchedules)
                .Ignore(user => user.DisplayPreferences).Ignore(user => user.ItemDisplayPreferences);
            modelBuilder.Entity<Permission>().HasKey(permission => permission.Id);
            modelBuilder.Entity<Preference>().HasKey(preference => preference.Id);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            foreach (var entry in ChangeTracker.Entries<User>().Where(entry => entry.State == EntityState.Added))
            {
                fixture.BeforeSave?.Invoke(entry.Entity);
            }
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }

    private sealed class TestCrypto : ICryptoProvider
    {
        public bool Fail { get; set; }
        public string DefaultHashMethod => "test";
        public PasswordHash CreatePasswordHash(ReadOnlySpan<char> password) => Fail
            ? throw new InvalidOperationException("Injected hash failure.")
            : PasswordHash.Parse("$PBKDF2$iterations=1000$616263$616263");
        public bool Verify(PasswordHash hash, ReadOnlySpan<char> password) => throw new NotSupportedException();
        public byte[] GenerateSalt() => throw new NotSupportedException();
        public byte[] GenerateSalt(int length) => throw new NotSupportedException();
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, TestProxy>();
        ((TestProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class TestProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
