using System.Security.Claims;
using Jellyfin.Plugin.Oidc.Configuration;
using Jellyfin.Plugin.Oidc.Identity;
using Xunit;

namespace Jellyfin.Plugin.Oidc.Tests;

public sealed class OidcLinkStoreTests
{
    private readonly PluginConfiguration _configuration = new() { UserGroup = "users", RequireVerifiedEmail = false };
    private readonly ClaimsPrincipal _principal = new(new ClaimsIdentity([new Claim("sub", "subject"), new Claim("groups", "users")]));

    [Fact]
    public void Each_stage_is_single_use_and_email_is_optional()
    {
        var store = new OidcLinkStore(TimeProvider.System);
        var userId = Guid.NewGuid();
        var start = store.Create(userId, "session-token", "/userprofile", _configuration)!;
        var callback = store.Start(start, _configuration, out var route)!;
        Assert.Equal("/userprofile", route);
        Assert.Null(store.Start(start, _configuration, out _));
        var completion = store.Validate(callback, _principal, _configuration)!;
        Assert.Null(store.Validate(callback, _principal, _configuration));
        Assert.True(store.TryComplete(completion, userId, "session-token", out var principal, out var configuration));
        Assert.Same(_principal, principal);
        Assert.Same(_configuration, configuration);
        Assert.False(store.TryComplete(completion, userId, "session-token", out _, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Completion_rejects_a_different_user_or_session(bool differentUser)
    {
        var store = new OidcLinkStore(TimeProvider.System);
        var userId = Guid.NewGuid();
        var start = store.Create(userId, "original-session", "/userprofile", _configuration)!;
        var callback = store.Start(start, _configuration, out _)!;
        var completion = store.Validate(callback, _principal, _configuration)!;
        Assert.False(store.TryComplete(completion, differentUser ? Guid.NewGuid() : userId, differentUser ? "original-session" : "another-session", out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Transactions_expire_at_every_stage(int stage)
    {
        var clock = new TestClock();
        var store = new OidcLinkStore(clock);
        var userId = Guid.NewGuid();
        var id = store.Create(userId, "session", "/userprofile", _configuration)!;
        if (stage > 0) id = store.Start(id, _configuration, out _)!;
        if (stage > 1) id = store.Validate(id, _principal, _configuration)!;
        clock.Now += TimeSpan.FromMinutes(5);
        if (stage == 0) Assert.Null(store.Start(id, _configuration, out _));
        else if (stage == 1) Assert.Null(store.Validate(id, _principal, _configuration));
        else Assert.False(store.TryComplete(id, userId, "session", out _, out _));
    }

    [Fact]
    public void Callback_requires_a_started_transaction_and_eligible_claims()
    {
        var store = new OidcLinkStore(TimeProvider.System);
        var start = store.Create(Guid.NewGuid(), "session", "/userprofile", _configuration)!;
        Assert.Null(store.Validate(start, _principal, _configuration));
        start = store.Create(Guid.NewGuid(), "session", "/userprofile", _configuration)!;
        var callback = store.Start(start, _configuration, out _)!;
        Assert.Null(store.Validate(callback, new ClaimsPrincipal(), _configuration));
    }

    [Fact]
    public void Callback_enforces_the_configured_verified_email_requirement()
    {
        var configuration = new PluginConfiguration { UserGroup = "users" };
        var store = new OidcLinkStore(TimeProvider.System);
        var start = store.Create(Guid.NewGuid(), "session", "/userprofile", configuration)!;
        var callback = store.Start(start, configuration, out _)!;
        Assert.Null(store.Validate(callback, _principal, configuration));
    }

    [Fact]
    public void Replaced_configuration_is_rejected_and_capacity_is_bounded()
    {
        var store = new OidcLinkStore(TimeProvider.System);
        var start = store.Create(Guid.NewGuid(), "session", "/userprofile", _configuration)!;
        Assert.Null(store.Start(start, new PluginConfiguration(), out _));
        start = store.Create(Guid.NewGuid(), "session", "/userprofile", _configuration)!;
        var callback = store.Start(start, _configuration, out _)!;
        Assert.Null(store.Validate(callback, _principal, new PluginConfiguration()));
        for (var index = 0; index < 1024; index++)
        {
            Assert.NotNull(store.Create(Guid.NewGuid(), "session", "/userprofile", _configuration));
        }
        Assert.Null(store.Create(Guid.NewGuid(), "session", "/userprofile", _configuration));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Administrator_mutations_invalidate_every_stage_and_allow_a_fresh_flow(int stage)
    {
        var store = new OidcLinkStore(TimeProvider.System);
        var userId = Guid.NewGuid();
        var id = store.Create(userId, "preserved-local-session", "/userprofile", _configuration)!;
        if (stage > 0) id = store.Start(id, _configuration, out _)!;
        if (stage > 1) id = store.Validate(id, _principal, _configuration)!;
        _configuration.IdentityRevision++;
        if (stage == 0) Assert.Null(store.Start(id, _configuration, out _));
        else if (stage == 1) Assert.Null(store.Validate(id, _principal, _configuration));
        else Assert.False(store.TryComplete(id, userId, "preserved-local-session", out _, out _));
        id = store.Create(userId, "preserved-local-session", "/userprofile", _configuration)!;
        id = store.Start(id, _configuration, out _)!;
        id = store.Validate(id, _principal, _configuration)!;
        Assert.True(store.TryComplete(id, userId, "preserved-local-session", out _, out _, out var revision));
        Assert.Equal(_configuration.IdentityRevision, revision);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
