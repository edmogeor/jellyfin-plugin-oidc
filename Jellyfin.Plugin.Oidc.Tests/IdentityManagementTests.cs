using System.Security.Claims;
using System.Xml.Serialization;
using Jellyfin.Plugin.Oidc.Configuration;
using Jellyfin.Plugin.Oidc.Identity;
using Xunit;

namespace Jellyfin.Plugin.Oidc.Tests;

public sealed class IdentityManagementTests
{
    private const string Issuer = "https://identity.example.test";

    [Fact]
    public void Open_enrollment_needs_only_a_subject_and_never_grants_administrator_rights()
    {
        var principal = Principal(new Claim("groups", "admins"));
        Assert.True(IdentityClaims.TryCreate(principal, new PluginConfiguration(), out var identity));
        Assert.False(identity.IsAdministrator);
        Assert.Empty(identity.Email);
        Assert.Empty(identity.PreferredUsername);
    }

    [Theory]
    [InlineData("users", "")]
    [InlineData("", "admins")]
    public void Configured_groups_require_membership_even_without_matching_claims(string users, string administrators)
    {
        Assert.False(IdentityClaims.TryCreate(Principal(), new PluginConfiguration { UserGroup = users, AdministratorGroup = administrators }, out _));
    }

    [Fact]
    public void Preferred_username_does_not_require_verified_email()
    {
        Assert.True(IdentityClaims.TryCreate(Principal(new Claim("preferred_username", "local-user"), new Claim("email", "unverified@example.test")), new PluginConfiguration(), out var identity));
        Assert.Equal("local-user", identity.MatchingKey(FirstSignInMatching.PreferredUsername));
        Assert.Empty(identity.MatchingKey(FirstSignInMatching.VerifiedEmail));
    }

    [Fact]
    public void Missing_or_duplicate_subjects_are_denied()
    {
        Assert.False(IdentityClaims.TryCreate(new ClaimsPrincipal(new ClaimsIdentity()), new PluginConfiguration(), out _));
        Assert.False(IdentityClaims.TryCreate(Principal(new Claim("sub", "other")), new PluginConfiguration(), out _));
    }

    [Fact]
    public void Every_brand_has_a_generated_label_and_custom_text_wins()
    {
        foreach (var (brand, name) in ProviderBrands.Names)
        {
            var configuration = new PluginConfiguration { ProviderBrand = brand };
            Assert.Equal("Sign in with " + name, ProviderBrands.LoginLabel(configuration));
            configuration.LoginButtonText = "Custom label";
            Assert.Equal("Custom label", ProviderBrands.LoginLabel(configuration));
        }
        Assert.Equal("Other", new PluginConfiguration().ProviderBrand);
        Assert.True(new PluginConfiguration().AllowSelfServiceIdentityLinks);
    }

    [Fact]
    public void A_linked_subject_or_target_cannot_be_reassigned()
    {
        var userId = Guid.NewGuid();
        var configuration = Config();
        configuration.IdentityLinks.Add(new IdentityLink { Issuer = Issuer, Subject = "subject", UserId = userId });
        IdentityRules.EnsureAvailable(configuration, "subject", userId, true);
        Assert.Throws<InvalidOperationException>(() => IdentityRules.EnsureAvailable(configuration, "other", userId, true));
        Assert.Throws<InvalidOperationException>(() => IdentityRules.EnsureAvailable(configuration, "subject", Guid.NewGuid(), true));
    }

    [Fact]
    public void An_opt_out_reserves_both_subject_and_former_user_until_explicit_relink()
    {
        var userId = Guid.NewGuid();
        var configuration = Config();
        configuration.UnlinkOptOuts.Add(new UnlinkOptOut(Issuer, "subject", userId));
        Assert.Throws<InvalidOperationException>(() => IdentityRules.EnsureAvailable(configuration, "subject", userId, false));
        IdentityRules.EnsureAvailable(configuration, "subject", userId, true);
        Assert.Throws<InvalidOperationException>(() => IdentityRules.EnsureAvailable(configuration, "subject", Guid.NewGuid(), true));
        Assert.Throws<InvalidOperationException>(() => IdentityRules.EnsureAvailable(configuration, "other", userId, true));
    }

    [Fact]
    public void A_pending_match_uses_its_selected_verified_claim_and_reserves_its_target()
    {
        var userId = Guid.NewGuid();
        var configuration = Config();
        var pending = new PendingAdminMatch(Issuer, userId, FirstSignInMatching.VerifiedEmail, "EXPECTED@example.test");
        configuration.PendingAdminMatches.Add(pending);
        Assert.Null(IdentityRules.PendingMatch(configuration, new OidcIdentity("subject", "", false, "EXPECTED@example.test")));
        Assert.Equal(pending, IdentityRules.PendingMatch(configuration, new OidcIdentity("subject", "expected@example.test", false)));
        Assert.Throws<InvalidOperationException>(() => IdentityRules.EnsureAvailable(configuration, "subject", userId, false));
        IdentityRules.EnsureAvailable(configuration, "subject", userId, true);
    }

    [Fact]
    public void An_identity_matching_two_pending_directives_is_denied()
    {
        var configuration = Config();
        configuration.PendingAdminMatches.Add(new PendingAdminMatch(Issuer, Guid.NewGuid(), FirstSignInMatching.VerifiedEmail, "user@example.test"));
        configuration.PendingAdminMatches.Add(new PendingAdminMatch(Issuer, Guid.NewGuid(), FirstSignInMatching.PreferredUsername, "user"));
        Assert.Throws<InvalidOperationException>(() => IdentityRules.PendingMatch(configuration, new OidcIdentity("subject", "user@example.test", false, "user")));
    }

    [Fact]
    public void Duplicate_links_and_conflicting_opt_outs_are_denied()
    {
        var configuration = Config();
        var userId = Guid.NewGuid();
        configuration.IdentityLinks.Add(new IdentityLink { Issuer = Issuer, Subject = "subject", UserId = userId });
        configuration.IdentityLinks.Add(new IdentityLink { Issuer = Issuer, Subject = "subject", UserId = Guid.NewGuid() });
        Assert.Throws<InvalidOperationException>(() => IdentityRules.Validate(configuration));
        configuration.IdentityLinks[1].Subject = "other";
        configuration.IdentityLinks[1].UserId = userId;
        Assert.Throws<InvalidOperationException>(() => IdentityRules.Validate(configuration));
        configuration.IdentityLinks.RemoveAt(1);
        configuration.UnlinkOptOuts.Add(new UnlinkOptOut(Issuer, "subject", userId));
        Assert.Throws<InvalidOperationException>(() => IdentityRules.Validate(configuration));
    }

    [Fact]
    public void Changing_issuer_does_not_reinterpret_existing_identity_state()
    {
        var configuration = Config();
        var userId = Guid.NewGuid();
        configuration.UnlinkOptOuts.Add(new UnlinkOptOut("https://old.example.test", "subject", userId));
        configuration.IdentityLinks.Add(new IdentityLink { Issuer = "https://old.example.test", Subject = "subject", UserId = Guid.NewGuid() });
        IdentityRules.EnsureAvailable(configuration, "subject", userId, false);
    }

    [Fact]
    public void Identity_records_round_trip_through_configuration_xml_and_legacy_links_default_to_email_origin()
    {
        var configuration = Config();
        var userId = Guid.NewGuid();
        configuration.IdentityLinks.Add(new IdentityLink { Issuer = Issuer, Subject = "linked", UserId = userId, PreferredUsername = "last-name" });
        configuration.UnlinkOptOuts.Add(new UnlinkOptOut(Issuer, "unlinked", Guid.NewGuid()));
        configuration.PendingAdminMatches.Add(new PendingAdminMatch(Issuer, Guid.NewGuid(), FirstSignInMatching.PreferredUsername, "expected-name"));
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var stream = new MemoryStream();
        serializer.Serialize(stream, configuration);
        stream.Position = 0;
        var restored = Assert.IsType<PluginConfiguration>(serializer.Deserialize(stream));
        Assert.Equal(configuration.UnlinkOptOuts, restored.UnlinkOptOuts);
        Assert.Equal(configuration.PendingAdminMatches, restored.PendingAdminMatches);
        Assert.Equal("last-name", restored.IdentityLinks[0].PreferredUsername);
        Assert.Equal(IdentityLinkOrigin.EmailMatch, restored.IdentityLinks[0].Origin);
        Assert.Equal(FirstSignInMatching.VerifiedEmail, restored.FirstSignInMatching);
    }

    [Fact]
    public void Link_tickets_keep_original_session_and_expiry_across_callback_and_are_single_use()
    {
        var store = new OidcLinkStore();
        var intent = new OidcLinkStore.LinkIntent(Guid.NewGuid(), Guid.NewGuid(), "session-hash", true, Issuer, "/userprofile", 7, DateTimeOffset.UtcNow.AddMinutes(5));
        var start = store.Create(intent)!;
        Assert.True(store.TryGet(start, true, out var consumed));
        Assert.False(store.TryGet(start, true, out _));
        var completion = store.Create(consumed with { Principal = Principal() })!;
        Assert.True(store.TryGet(completion, false, out var completed));
        Assert.Equal(intent.Session, completed.Session);
        Assert.Equal(intent.ExpiresAt, completed.ExpiresAt);
        Assert.True(store.TryGet(completion, true, out _));
        Assert.False(store.TryGet(completion, true, out _));
        var expired = store.Create(intent with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) })!;
        Assert.False(store.TryGet(expired, false, out _));
    }

    private static PluginConfiguration Config() => new() { IssuerUrl = Issuer };
    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(new[] { new Claim("sub", "subject") }.Concat(claims)));
}
