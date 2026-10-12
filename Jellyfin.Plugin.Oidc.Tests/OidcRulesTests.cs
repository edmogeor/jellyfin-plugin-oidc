using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Oidc.Configuration;
using Jellyfin.Plugin.Oidc.Identity;
using Jellyfin.Plugin.Oidc.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Oidc.Tests;

public sealed class OidcRulesTests
{
    [Fact]
    public void Configuration_strings_have_matching_keys_and_preserve_translated_values_and_placeholders()
    {
        var assembly = typeof(PluginConfiguration).Assembly;
        using var englishStream = assembly
            .GetManifestResourceStream("Jellyfin.Plugin.Oidc.Configuration.Strings.en-us.json");

        Assert.NotNull(englishStream);
        var expectedKeys = GetJsonKeys(englishStream);
        englishStream.Position = 0;
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(englishStream)!;
        foreach (var resourceName in assembly.GetManifestResourceNames().Where(name => name.StartsWith("Jellyfin.Plugin.Oidc.Configuration.Strings.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            var keys = GetJsonKeys(stream);
            Assert.Equal(expectedKeys, keys);
            stream.Position = 0;
            var translated = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            var locale = resourceName["Jellyfin.Plugin.Oidc.Configuration.Strings.".Length..^5];
            var controller = new OidcController(null!, new EphemeralDataProtectionProvider(), NullLogger<OidcController>.Instance);
            var response = Assert.IsType<JsonResult>(controller.Strings(locale));
            var dictionary = Assert.IsType<Dictionary<string, string>>(response.Value);
            Assert.Equal(expectedKeys, dictionary.Keys.Order().ToArray());
            foreach (var entry in translated)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Value));
                Assert.Equal(entry.Value, dictionary[entry.Key]);
                Assert.Equal(Regex.Matches(english[entry.Key], @"\{\d+\}").Select(match => match.Value).Order(),
                    Regex.Matches(entry.Value, @"\{\d+\}").Select(match => match.Value).Order());
            }
        }
    }

    [Fact]
    public void Configuration_page_embeds_english_strings_before_translations_load()
    {
        var assembly = typeof(PluginConfiguration).Assembly;
        using var stream = assembly.GetManifestResourceStream("Jellyfin.Plugin.Oidc.Configuration.configPage.html");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();

        Assert.Contains("<title data-i18n=\"pageTitle\">OIDC Authentication</title>", html);
        Assert.Contains("Add &lt;Public Jellyfin URL&gt;/web/index.html", html);
        Assert.DoesNotContain("{{", html);
    }

    [Fact]
    public void Browser_script_embeds_the_english_dictionary_at_build_time()
    {
        var assembly = typeof(PluginConfiguration).Assembly;
        using var stream = assembly.GetManifestResourceStream("Jellyfin.Plugin.Oidc.Web.oidc.js");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var script = reader.ReadToEnd();
        Assert.DoesNotContain("@english-strings", script);
        var match = Regex.Match(script, @"const profileStrings = (\{[^\r\n]+\});");
        Assert.True(match.Success);
        using var dictionary = JsonDocument.Parse(match.Groups[1].Value);
        using var englishStream = assembly.GetManifestResourceStream("Jellyfin.Plugin.Oidc.Configuration.Strings.en-us.json")!;
        using var english = JsonDocument.Parse(englishStream);
        foreach (var entry in english.RootElement.EnumerateObject())
        {
            Assert.Equal(entry.Value.GetString(), dictionary.RootElement.GetProperty(entry.Name).GetString());
        }
    }

    private static string[] GetJsonKeys(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();
    }

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
    public void Unknown_automatic_linking_mode_is_rejected()
    {
        Assert.NotNull(OidcConfigurationValidator.Validate(new PluginConfiguration { AutomaticLinkingMode = (AutomaticLinkingMode)99 }));
        Assert.NotNull(OidcConfigurationValidator.Validate(new PluginConfiguration { ProvisioningUsernameMode = (ProvisioningUsernameMode)99 }));
    }

    [Fact]
    public void Approvals_suppression_and_outstanding_revocation_round_trip_through_xml()
    {
        var state = new OidcUserAdministration
        {
            UserId = Guid.NewGuid(), Version = Guid.NewGuid(), AutomaticReattachmentBlocked = true,
            UnlinkOperation = Guid.NewGuid(), Request = new PendingLinkRequest { Issuer = "https://identity.example.test", Username = "Provider-Name", UsernameClaim = "preferred_username", CreatedAt = DateTime.UtcNow.AddYears(-10) },
            PendingRevocations = [new OidcSessionProvenance { AuthenticationId = 42, UserId = Guid.NewGuid(), TokenFingerprint = "fingerprint" }],
        };
        var original = new PluginConfiguration { IdentityRevision = 12, UserAdministration = [state], OidcSessions = state.PendingRevocations.ToList() };
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, original);
        using var reader = new StringReader(writer.ToString());
        var restored = (PluginConfiguration)serializer.Deserialize(reader)!;
        var restoredState = Assert.Single(restored.UserAdministration);
        Assert.Equal(state.Version, restoredState.Version);
        Assert.Equal(state.Request.CreatedAt, restoredState.Request!.CreatedAt);
        Assert.Equal("Provider-Name", restoredState.Request.Username);
        Assert.Equal("preferred_username", restoredState.Request.UsernameClaim);
        Assert.True(restoredState.AutomaticReattachmentBlocked);
        Assert.Equal(state.UnlinkOperation, restoredState.UnlinkOperation);
        Assert.Equal("fingerprint", Assert.Single(restoredState.PendingRevocations).TokenFingerprint);
        Assert.Equal(42, Assert.Single(restored.OidcSessions).AuthenticationId);
        Assert.Equal(12, restored.IdentityRevision);
    }

    [Fact]
    public void Login_button_text_is_empty_by_default()
    {
        Assert.Empty(new PluginConfiguration().LoginButtonText);
    }

    [Fact]
    public void Verified_email_is_required_by_default_and_username_claim_defaults_to_preferred_username()
    {
        var configuration = new PluginConfiguration();
        Assert.True(configuration.RequireVerifiedEmail);
        Assert.Equal("preferred_username", configuration.UsernameClaim);
        Assert.Equal(AutomaticLinkingMode.EmailToUsername, configuration.AutomaticLinkingMode);
        Assert.Equal(ProvisioningUsernameMode.UsernameClaim, configuration.ProvisioningUsernameMode);
    }

    [Fact]
    public void Configured_username_claim_reads_only_a_top_level_string_and_does_not_change_the_subject()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "subject"), new Claim("email", "user@example.test"), new Claim("email_verified", "true"),
            new Claim("groups", "users"), new Claim("preferred_username", "default-name"), new Claim("login_name", "mapped-name"),
            new Claim("profile", "{\"name\":\"nested-name\"}", "JSON"),
        ]));
        var configuration = new PluginConfiguration { UserGroup = "users", UsernameClaim = " login_name " };
        Assert.True(IdentityClaims.TryCreate(principal, configuration, out var identity));
        Assert.Equal("mapped-name", identity.Username);
        Assert.Equal("subject", identity.Subject);
        configuration.UsernameClaim = "profile.name";
        Assert.True(IdentityClaims.TryCreate(principal, configuration, out identity));
        Assert.Empty(identity.Username);
        configuration.UsernameClaim = "profile";
        Assert.True(IdentityClaims.TryCreate(principal, configuration, out identity));
        Assert.Empty(identity.Username);
    }

    [Fact]
    public void Email_username_claim_never_uses_unverified_email()
    {
        var configuration = new PluginConfiguration { UserGroup = "users", UsernameClaim = "email", RequireVerifiedEmail = false };
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "subject"), new Claim("groups", "users"),
            new Claim("email", "unverified@example.test"), new Claim("email_verified", "false"),
        ]));
        Assert.True(IdentityClaims.TryCreate(principal, configuration, out var identity));
        Assert.Empty(identity.Email);
        Assert.Empty(identity.Username);
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
        Assert.True(identity.IsAdministrator);
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
        var context = new DefaultHttpContext
        {
            Request =
            {
                Scheme = "https",
                Host = new HostString("jellyfin.example.test", 8096),
                PathBase = "/jellyfin",
            },
        };

        Assert.Equal("https://jellyfin.example.test:8096/jellyfin", PublicUrls.Get(context.Request, new PluginConfiguration()));
    }

    [Theory]
    [InlineData("https", "", true)]
    [InlineData("http", "", false)]
    [InlineData("http", "https://jellyfin.example.test", true)]
    public void Public_url_must_effectively_use_https(string scheme, string publicUrl, bool expected)
    {
        var context = new DefaultHttpContext { Request = { Scheme = scheme } };

        Assert.Equal(expected, PublicUrls.UsesHttps(context.Request, new PluginConfiguration { PublicUrl = publicUrl }));
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
