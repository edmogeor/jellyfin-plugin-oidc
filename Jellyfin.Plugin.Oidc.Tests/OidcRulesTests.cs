using System.Security.Claims;
using Jellyfin.Plugin.Oidc.Configuration;
using Jellyfin.Plugin.Oidc.Identity;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Oidc.Tests;

public sealed class OidcRulesTests
{
    [Theory]
    [InlineData("https://jellyfin.example.test", "https://identity.example.test", "client", "secret", "users", "", PasswordLoginMode.AllowForAllUsers, null)]
    [InlineData("", "https://identity.example.test", "client", "secret", "", "admins", PasswordLoginMode.AllowForAllUsers, null)]
    [InlineData("http://jellyfin.example.test", "https://identity.example.test", "client", "secret", "users", "", PasswordLoginMode.AllowForAllUsers, "HTTPS")]
    [InlineData("https://jellyfin.example.test", "http://identity.example.test", "client", "secret", "users", "", PasswordLoginMode.AllowForAllUsers, "HTTPS")]
    [InlineData("https://jellyfin.example.test", "https://identity.example.test", "", "secret", "users", "", PasswordLoginMode.AllowForAllUsers, "Client ID")]
    [InlineData("https://jellyfin.example.test", "https://identity.example.test", "client", "secret", "", "", PasswordLoginMode.AllowForAllUsers, "Group")]
    [InlineData("https://jellyfin.example.test", "https://identity.example.test", "client", "secret", "users", "", PasswordLoginMode.DisableForAllUsers, "Administrator Group")]
    public void Enabled_configuration_validates_required_security_settings(
        string publicUrl,
        string issuerUrl,
        string clientId,
        string clientSecret,
        string userGroup,
        string administratorGroup,
        PasswordLoginMode passwordLoginMode,
        string? expectedError)
    {
        var configuration = new PluginConfiguration
        {
            Enabled = true,
            PublicUrl = publicUrl,
            IssuerUrl = issuerUrl,
            ClientId = clientId,
            ClientSecret = clientSecret,
            UserGroup = userGroup,
            AdministratorGroup = administratorGroup,
            PasswordLoginMode = passwordLoginMode,
        };

        var error = OidcConfigurationValidator.Validate(configuration);

        if (expectedError is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.Contains(expectedError, error);
        }
    }

    [Fact]
    public void Disabled_configuration_does_not_require_oidc_settings()
    {
        Assert.Null(OidcConfigurationValidator.Validate(new PluginConfiguration()));
    }

    [Fact]
    public void Verified_administrator_group_is_eligible()
    {
        var configuration = new PluginConfiguration { AdministratorGroup = "jellyfin-admins" };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "subject"),
            new Claim("email", "user@example.test"),
            new Claim("email_verified", "true"),
            new Claim("groups", "jellyfin-admins"),
        ]));

        Assert.True(IdentityClaims.TryCreate(principal, configuration, out var identity));
        Assert.True(identity!.IsAdministrator);
    }

    [Fact]
    public void Unverified_email_is_denied()
    {
        var configuration = new PluginConfiguration { UserGroup = "jellyfin-users" };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "subject"),
            new Claim("email", "user@example.test"),
            new Claim("email_verified", "false"),
            new Claim("groups", "jellyfin-users"),
        ]));

        Assert.False(IdentityClaims.TryCreate(principal, configuration, out _));
    }

    [Fact]
    public void Json_group_claims_allow_users_and_administrators()
    {
        var configuration = new PluginConfiguration
        {
            UserGroup = "jellyfin-users, other-users",
            AdministratorGroup = "jellyfin-admins",
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "subject"),
            new Claim("email", "user@example.test"),
            new Claim("email_verified", "true"),
            new Claim("groups", "[\"jellyfin-users\",\"jellyfin-admins\"]"),
        ]));

        Assert.True(IdentityClaims.TryCreate(principal, configuration, out var identity));
        Assert.Equal(new OidcIdentity("subject", "user@example.test", true), identity);
    }

    [Fact]
    public void Custom_group_claim_and_multiple_claims_are_supported()
    {
        var configuration = new PluginConfiguration { GroupClaim = "roles", UserGroup = "jellyfin-users" };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "subject"),
            new Claim("email", "user@example.test"),
            new Claim("email_verified", "true"),
            new Claim("roles", "other-role"),
            new Claim("roles", "jellyfin-users"),
        ]));

        Assert.True(IdentityClaims.TryCreate(principal, configuration, out _));
    }

    [Fact]
    public void Requested_scopes_include_standard_and_configured_scopes()
    {
        var scopes = OidcOptions.RequestedScopes(new PluginConfiguration { AdditionalScopes = "groups openid custom" });

        Assert.Equal(["openid", "email", "profile", "groups", "custom"], scopes);
    }

    [Fact]
    public void Group_membership_is_case_sensitive()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "subject"),
            new Claim("email", "user@example.test"),
            new Claim("email_verified", "true"),
            new Claim("groups", "jellyfin-users"),
        ]));

        Assert.False(IdentityClaims.TryCreate(principal, new PluginConfiguration { UserGroup = "Jellyfin-Users" }, out _));
    }

    [Theory]
    [InlineData("", "user@example.test", "true", "jellyfin-users")]
    [InlineData("subject", "", "true", "jellyfin-users")]
    [InlineData("subject", "user@example.test", "not-a-bool", "jellyfin-users")]
    [InlineData("subject", "user@example.test", "true", "other-group")]
    [InlineData("subject", "user@example.test", "true", "[invalid")]
    public void Missing_or_ineligible_identity_claims_are_denied(string subject, string email, string verified, string groups)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", subject),
            new Claim("email", email),
            new Claim("email_verified", verified),
            new Claim("groups", groups),
        ]));

        Assert.False(IdentityClaims.TryCreate(principal, new PluginConfiguration { UserGroup = "jellyfin-users" }, out _));
    }

    [Theory]
    [InlineData("/items/1", true)]
    [InlineData("https://attacker.example", false)]
    [InlineData("//attacker.example", false)]
    public void Only_local_return_urls_are_accepted(string value, bool expected)
    {
        Assert.Equal(expected, ReturnUrls.Local(value));
    }

    [Fact]
    public void Empty_and_backslash_return_urls_are_rejected()
    {
        Assert.False(ReturnUrls.Local(null));
        Assert.False(ReturnUrls.Local("  "));
        Assert.False(ReturnUrls.Local("/items\\1"));
    }

    [Fact]
    public void Public_url_uses_the_request_origin_without_an_override()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("jellyfin.example.test", 8096);
        context.Request.PathBase = "/jellyfin";

        Assert.Equal("https://jellyfin.example.test:8096/jellyfin", PublicUrls.Get(context.Request, new PluginConfiguration()));
    }

    [Fact]
    public void Public_url_override_has_no_trailing_slash()
    {
        var context = new DefaultHttpContext();

        Assert.Equal("https://jellyfin.example.test", PublicUrls.Get(context.Request, new PluginConfiguration { PublicUrl = "https://jellyfin.example.test/" }));
    }

    [Fact]
    public void Logout_return_url_has_no_fragment()
    {
        var context = new DefaultHttpContext();

        Assert.Equal("https://jellyfin.example.test/web/index.html", PublicUrls.LogoutReturnUrl(context.Request, new PluginConfiguration { PublicUrl = "https://jellyfin.example.test" }));
    }

    [Fact]
    public void Oidc_cookie_path_preserves_the_public_url_path()
    {
        var context = new DefaultHttpContext();

        Assert.Equal("/jellyfin/oidc", PublicUrls.OidcPath(context.Request, new PluginConfiguration { PublicUrl = "https://jellyfin.example.test/jellyfin" }));
    }

    [Fact]
    public void Login_tickets_are_single_use()
    {
        var store = new OidcLoginStore();
        var userId = Guid.NewGuid();
        var ticket = store.Create(userId, "/items/1");

        Assert.NotNull(ticket);
        Assert.True(store.TryTake(ticket, out var returnedUserId, out var returnUrl));
        Assert.Equal(userId, returnedUserId);
        Assert.Equal("/items/1", returnUrl);
        Assert.False(store.TryTake(ticket, out _, out _));
    }

    [Fact]
    public void Unknown_login_ticket_returns_safe_defaults()
    {
        Assert.False(new OidcLoginStore().TryTake("missing", out var userId, out var returnUrl));
        Assert.Equal(Guid.Empty, userId);
        Assert.Equal("/", returnUrl);
    }

    [Fact]
    public void Login_tickets_have_a_bounded_capacity()
    {
        var store = new OidcLoginStore();

        for (var index = 0; index < 1024; index++)
        {
            Assert.NotNull(store.Create(Guid.NewGuid(), "/"));
        }

        Assert.Null(store.Create(Guid.NewGuid(), "/"));
    }
}
