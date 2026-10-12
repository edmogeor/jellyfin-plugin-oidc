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
        Assert.Equal("user@example.test", Assert.Single(fixture.Configuration.IdentityLinks).Email);
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

    [Theory]
    [InlineData(PasswordLoginMode.AllowForAllUsers)]
    [InlineData(PasswordLoginMode.DisableForLinkedUsersOnly)]
    [InlineData(PasswordLoginMode.DisableForAllUsers)]
    public async Task Explicit_link_preserves_username_and_permissions_and_applies_password_policy(PasswordLoginMode mode)
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.PasswordLoginMode = mode;
        var user = await fixture.SeedUserAsync("local-name");

        Assert.True(await fixture.LinkAsync(user.Id, "subject", administrator: true));
        Assert.True(await fixture.LinkAsync(user.Id, "subject", administrator: true));
        var savedUser = Assert.Single(fixture.Users());
        Assert.Equal("local-name", savedUser.Username);
        Assert.False(savedUser.HasPermission(PermissionKind.IsAdministrator));
        Assert.Equal("existing-password", savedUser.Password);
        Assert.Equal(mode == PasswordLoginMode.AllowForAllUsers ? ProvisioningFixture.DefaultProvider : typeof(OidcPasswordDisabledProvider).FullName, savedUser.AuthenticationProviderId);
        Assert.Equal(user.Id, Assert.Single(fixture.Configuration.IdentityLinks).UserId);
        Assert.Equal("user@example.test", Assert.Single(fixture.Configuration.IdentityLinks).Email);
    }

    [Fact]
    public async Task Explicit_link_and_returning_sign_in_work_without_email_and_never_rename_the_user()
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.RequireVerifiedEmail = false;
        var user = await fixture.SeedUserAsync("local-name");
        Assert.Null(await fixture.SignInAsync("subject", email: null));
        Assert.True(await fixture.LinkAsync(user.Id, "subject", email: null, username: "provider-user"));
        var link = Assert.Single(fixture.Configuration.IdentityLinks);
        Assert.Empty(link.Email);
        Assert.Equal("provider-user", link.Username);
        Assert.Equal(user.Id, await fixture.SignInAsync("subject", email: null));
        Assert.Equal(user.Id, await fixture.SignInAsync("subject", email: "changed@example.test"));
        Assert.Equal("changed@example.test", link.Email);
        Assert.Equal(user.Id, await fixture.SignInAsync("subject", verified: false));
        Assert.Equal(user.Id, await fixture.SignInAsync("subject", email: null));
        Assert.Equal("changed@example.test", link.Email);
        Assert.Equal("local-name", Assert.Single(fixture.Users()).Username);
    }

    [Fact]
    public async Task Explicit_link_does_not_retain_unverified_email_and_refreshes_only_supplied_verified_email()
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.RequireVerifiedEmail = false;
        var user = await fixture.SeedUserAsync("local-name");
        Assert.True(await fixture.LinkAsync(user.Id, "subject", verified: false));
        var link = Assert.Single(fixture.Configuration.IdentityLinks);
        Assert.Empty(link.Email);
        Assert.True(await fixture.LinkAsync(user.Id, "subject", email: "verified@example.test"));
        Assert.Equal("verified@example.test", link.Email);
        Assert.True(await fixture.LinkAsync(user.Id, "subject", email: null));
        Assert.True(await fixture.LinkAsync(user.Id, "subject", email: "unverified@example.test", verified: false));
        Assert.False(await fixture.LinkAsync(user.Id, "other-subject", email: "other@example.test"));
        Assert.Equal("verified@example.test", link.Email);
        Assert.Equal("local-name", Assert.Single(fixture.Users()).Username);
    }

    [Fact]
    public async Task Failed_configuration_saves_do_not_replace_retained_email()
    {
        using var fixture = new ProvisioningFixture();
        var user = await fixture.SeedUserAsync("local-name");
        Assert.True(await fixture.LinkAsync(user.Id, "subject", username: "provider-user"));
        var link = Assert.Single(fixture.Configuration.IdentityLinks);
        fixture.FailConfigurationSave = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.LinkAsync(user.Id, "subject", email: "changed@example.test", username: "changed-user"));
        Assert.Equal("user@example.test", link.Email);
        Assert.Equal("provider-user", link.Username);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SignInAsync("subject", email: "changed@example.test", username: "changed-user"));
        Assert.Equal("user@example.test", link.Email);
        Assert.Equal("provider-user", link.Username);
    }

    [Fact]
    public async Task Explicit_link_rejects_conflicting_links_and_serializes_competing_attempts()
    {
        using var fixture = new ProvisioningFixture();
        var first = await fixture.SeedUserAsync("first");
        var second = await fixture.SeedUserAsync("second");
        var results = await Task.WhenAll(fixture.LinkAsync(first.Id, "subject"), fixture.LinkAsync(second.Id, "subject"));
        Assert.Single(results, result => result);
        var link = Assert.Single(fixture.Configuration.IdentityLinks);
        Assert.False(await fixture.LinkAsync(link.UserId, "different-subject"));
        Assert.Equal("subject", link.Subject);
        Assert.Equal(2, fixture.Users().Length);
    }

    [Fact]
    public async Task Explicit_link_rejects_missing_user_ineligible_identity_and_replaced_configuration()
    {
        using var fixture = new ProvisioningFixture();
        var user = await fixture.SeedUserAsync("local-name");
        Assert.False(await fixture.LinkAsync(Guid.NewGuid(), "subject"));
        Assert.False(await fixture.LinkAsync(user.Id, ""));
        Assert.False(await fixture.LinkAsync(user.Id, "subject", group: "unapproved"));
        fixture.Plugin.UpdateConfiguration(new PluginConfiguration { Enabled = false });
        Assert.False(await fixture.LinkAsync(user.Id, "subject"));
        Assert.Empty(fixture.Configuration.IdentityLinks);
    }

    [Fact]
    public async Task Unlinked_sign_in_requires_a_username_or_verified_email_when_email_is_optional()
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.RequireVerifiedEmail = false;
        Assert.Null(await fixture.SignInAsync("subject", verified: false));
        Assert.Null(await fixture.SignInAsync("subject", email: null));
        Assert.Empty(fixture.Users());
        Assert.Empty(fixture.Configuration.IdentityLinks);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("unverified@example.test", false)]
    public async Task Required_verified_email_is_enforced_for_linking_and_linked_sign_in(string? email, bool verified)
    {
        using var fixture = new ProvisioningFixture();
        Assert.True(fixture.Configuration.RequireVerifiedEmail);
        var user = await fixture.SeedUserAsync("local-name");
        Assert.False(await fixture.LinkAsync(user.Id, "subject", email: email, verified: verified));
        Assert.Empty(fixture.Configuration.IdentityLinks);
        Assert.True(await fixture.LinkAsync(user.Id, "subject"));
        Assert.Null(await fixture.SignInAsync("subject", email: email, verified: verified));
        Assert.Equal("user@example.test", Assert.Single(fixture.Configuration.IdentityLinks).Email);
        Assert.Equal("local-name", Assert.Single(fixture.Users()).Username);
    }

    [Fact]
    public async Task Provider_username_is_metadata_and_refreshes_without_changing_the_identity_key()
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.RequireVerifiedEmail = false;
        var user = await fixture.SeedUserAsync("local-name");
        Assert.True(await fixture.LinkAsync(user.Id, "subject", email: null, username: "provider-name"));
        var link = Assert.Single(fixture.Configuration.IdentityLinks);
        Assert.Equal("provider-name", link.Username);
        Assert.Equal(user.Id, await fixture.SignInAsync("subject", email: null, username: "renamed-provider"));
        Assert.Equal("renamed-provider", link.Username);
        Assert.Equal(user.Id, await fixture.SignInAsync("subject", email: null, username: null));
        Assert.Equal("renamed-provider", link.Username);
        Assert.Equal("subject", link.Subject);
        Assert.Equal("local-name", Assert.Single(fixture.Users()).Username);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Provisioning_uses_preferred_username_and_preserves_it_on_returning_sign_in(bool requireVerifiedEmail)
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.RequireVerifiedEmail = requireVerifiedEmail;
        var email = requireVerifiedEmail ? "user@example.test" : null;
        var userId = await fixture.SignInAsync("subject", email: email, username: "preferred-name");
        Assert.NotNull(userId);
        Assert.Equal("preferred-name", Assert.Single(fixture.Users()).Username);
        Assert.Equal(userId, await fixture.SignInAsync("subject", email: "changed@example.test", username: "changed-name"));
        Assert.Equal("preferred-name", Assert.Single(fixture.Users()).Username);
        Assert.Equal("changed-name", Assert.Single(fixture.Configuration.IdentityLinks).Username);
    }

    [Fact]
    public async Task Preferred_username_collision_never_attaches_an_unrelated_user()
    {
        using var fixture = new ProvisioningFixture();
        var unrelated = await fixture.SeedUserAsync("PREFERRED-NAME");
        Assert.Null(await fixture.SignInAsync("subject", username: "preferred-name"));
        Assert.Empty(fixture.Configuration.IdentityLinks);
        Assert.Equal(unrelated.Id, Assert.Single(fixture.Users()).Id);
    }

    [Fact]
    public async Task Verified_email_match_still_precedes_preferred_username_provisioning()
    {
        using var fixture = new ProvisioningFixture();
        var matched = await fixture.SeedUserAsync("user@example.test");
        Assert.Equal(matched.Id, await fixture.SignInAsync("subject", username: "preferred-name"));
        Assert.Equal("user@example.test", Assert.Single(fixture.Users()).Username);
    }

    [Fact]
    public async Task Failed_link_save_removes_the_link_and_restores_password_login()
    {
        using var fixture = new ProvisioningFixture();
        var user = await fixture.SeedUserAsync("local-name");
        fixture.Configuration.PasswordLoginMode = PasswordLoginMode.DisableForLinkedUsersOnly;
        fixture.FailConfigurationSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.LinkAsync(user.Id, "subject"));

        Assert.Empty(fixture.Configuration.IdentityLinks);
        Assert.Equal(ProvisioningFixture.DefaultProvider, Assert.Single(fixture.Users()).AuthenticationProviderId);
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
                    if (FailConfigurationSave)
                    {
                        throw new InvalidOperationException("Injected configuration save failure.");
                    }
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
        public bool FailConfigurationSave { get; set; }

        public Task<Guid?> SignInAsync(string subject, bool administrator = false, string? email = "user@example.test", bool verified = true, string? username = null)
            => _provisioner.ProvisionAsync(Principal(subject, administrator, email, verified, null, username), Configuration);

        public Task<bool> LinkAsync(Guid userId, string subject, bool administrator = false, string? email = "user@example.test", string? group = null, bool verified = true, string? username = null)
            => _provisioner.LinkAsync(userId, Principal(subject, administrator, email, verified, group, username), Configuration);

        private static ClaimsPrincipal Principal(string subject, bool administrator, string? email, bool verified, string? group, string? username)
        {
            var claims = new List<Claim> { new("sub", subject), new("groups", group ?? (administrator ? "admins" : "users")) };
            if (username is not null)
            {
                claims.Add(new Claim("preferred_username", username));
            }
            if (email is not null)
            {
                claims.Add(new Claim("email", email));
                claims.Add(new Claim("email_verified", verified ? "true" : "false"));
            }
            return new ClaimsPrincipal(new ClaimsIdentity(claims));
        }

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
