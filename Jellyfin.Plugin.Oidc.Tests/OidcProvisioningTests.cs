using System.Reflection;
using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Oidc.Configuration;
using Jellyfin.Plugin.Oidc.Identity;
using Jellyfin.Plugin.Oidc.Api;
using Jellyfin.Database.Implementations.Entities.Security;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
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

    [Theory]
    [InlineData(ProvisioningUsernameMode.UsernameClaim, "provider-name")]
    [InlineData(ProvisioningUsernameMode.VerifiedEmail, "user@example.test")]
    public async Task Provisioning_name_source_is_independent_of_matching_and_existing_usernames(ProvisioningUsernameMode mode, string expectedName)
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.AutomaticLinkingMode = AutomaticLinkingMode.None;
        fixture.Configuration.ProvisioningUsernameMode = mode;
        var id = await fixture.SignInAsync("subject", username: "provider-name");
        Assert.NotNull(id);
        Assert.Equal(expectedName, Assert.Single(fixture.Users()).Username);
        Assert.Equal("provider-name", Assert.Single(fixture.Configuration.IdentityLinks).Username);
        fixture.Configuration.ProvisioningUsernameMode = mode == ProvisioningUsernameMode.UsernameClaim ? ProvisioningUsernameMode.VerifiedEmail : ProvisioningUsernameMode.UsernameClaim;
        Assert.Equal(id, await fixture.SignInAsync("subject", username: "changed-name", email: "changed@example.test"));
        Assert.Equal(expectedName, Assert.Single(fixture.Users()).Username);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("user@example.test", false)]
    public async Task Email_naming_requires_current_verified_email_without_weakening_existing_user_resolution(string? email, bool verified)
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.RequireVerifiedEmail = false;
        fixture.Configuration.ProvisioningUsernameMode = ProvisioningUsernameMode.VerifiedEmail;
        Assert.Null(await fixture.SignInAsync("subject", email: email, verified: verified, username: "provider-name"));
        Assert.Empty(fixture.Users());
        var user = await fixture.SeedUserAsync("provider-name");
        fixture.Configuration.AutomaticLinkingMode = AutomaticLinkingMode.UsernameToUsername;
        Assert.Equal(user.Id, await fixture.SignInAsync("subject", email: email, verified: verified, username: "provider-name"));
        Assert.Equal("provider-name", Assert.Single(fixture.Users()).Username);
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

    [Theory]
    [InlineData("", "subject")]
    [InlineData("USER@example.test", "")]
    public async Task Administrator_approvals_resolve_existing_user_without_expiry(string email, string subject)
    {
        using var fixture = new ProvisioningFixture();
        var user = await fixture.SeedUserAsync("local-name");
        var state = AdministrationRules.State(fixture.Configuration, user.Id);
        state.AutomaticReattachmentBlocked = true;
        var controller = await fixture.AdminAsync();
        Assert.IsType<OkObjectResult>(await controller.Approve(user.Id, new(Guid.Empty, email, subject)));
        state.Request!.CreatedAt = DateTime.UtcNow.AddYears(-10);
        Assert.Equal(user.Id, await fixture.SignInAsync("subject", administrator: true));
        Assert.Null(state.Request);
        Assert.False(state.AutomaticReattachmentBlocked);
        Assert.Equal("local-name", Assert.Single(fixture.Users(), item => item.Id == user.Id).Username);
        Assert.True(fixture.Users().Single(item => item.Id == user.Id).HasPermission(PermissionKind.IsAdministrator));
    }

    [Theory]
    [InlineData(false, "subject")]
    [InlineData(true, "other-subject")]
    public async Task Pending_approval_cannot_be_bypassed_by_email_match_or_provisioning(bool verified, string subject)
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.RequireVerifiedEmail = false;
        var user = await fixture.SeedUserAsync("USER@example.test");
        var controller = await fixture.AdminAsync();
        Assert.IsType<OkObjectResult>(await controller.Approve(user.Id, verified ? new(Guid.Empty, Subject: "subject") : new(Guid.Empty, Email: "user@example.test")));
        Assert.Null(await fixture.SignInAsync(subject, verified: verified, username: "new-user"));
        Assert.Empty(fixture.Configuration.IdentityLinks);
        Assert.False(await fixture.LinkAsync(user.Id, subject, verified: verified));
    }

    [Fact]
    public async Task Subject_approval_accepts_email_free_identity_but_email_approval_requires_current_verification()
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.RequireVerifiedEmail = false;
        var user = await fixture.SeedUserAsync("local-name");
        var controller = await fixture.AdminAsync();
        Assert.IsType<OkObjectResult>(await controller.Approve(user.Id, new(Guid.Empty, "user@example.test", "")));
        Assert.Null(await fixture.SignInAsync("subject", verified: false, username: "new-name"));
        Assert.IsType<OkObjectResult>(await controller.Approve(user.Id, new(AdministrationRules.Version(fixture.Configuration, user.Id), "", "Subject")));
        Assert.Equal(user.Id, await fixture.SignInAsync("Subject", email: null));
    }

    [Fact]
    public async Task Approval_save_failure_during_sign_in_preserves_approval_block_and_password_policy()
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.PasswordLoginMode = PasswordLoginMode.DisableForLinkedUsersOnly;
        var user = await fixture.SeedUserAsync("local-name");
        var controller = await fixture.AdminAsync();
        Assert.IsType<OkObjectResult>(await controller.Approve(user.Id, new(Guid.Empty, "", "subject")));
        var state = AdministrationRules.State(fixture.Configuration, user.Id);
        state.AutomaticReattachmentBlocked = true;
        fixture.FailConfigurationSave = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SignInAsync("subject"));
        Assert.Empty(fixture.Configuration.IdentityLinks);
        Assert.NotNull(state.Request);
        Assert.True(state.AutomaticReattachmentBlocked);
        Assert.Equal(ProvisioningFixture.DefaultProvider, fixture.Users().Single(item => item.Id == user.Id).AuthenticationProviderId);
    }

    [Fact]
    public async Task Reservations_reject_overlaps_stale_edits_and_conflicting_explicit_links()
    {
        using var fixture = new ProvisioningFixture();
        var first = await fixture.SeedUserAsync("first");
        var second = await fixture.SeedUserAsync("second");
        var controller = await fixture.AdminAsync();
        Assert.IsType<BadRequestResult>(await controller.Approve(first.Id, new(Guid.Empty, "not-an-email", "")));
        Assert.IsType<BadRequestResult>(await controller.Approve(first.Id, new(Guid.Empty, "", "")));
        Assert.IsType<BadRequestResult>(await controller.Approve(first.Id, new(Guid.Empty, "user@example.test", "Subject")));
        Assert.IsType<OkObjectResult>(await controller.Approve(first.Id, new(Guid.Empty, Email: "user@example.test")));
        Assert.IsType<ConflictResult>(await controller.Approve(second.Id, new(Guid.Empty, Email: "USER@example.test")));
        Assert.IsType<OkObjectResult>(await controller.Approve(first.Id, new(AdministrationRules.Version(fixture.Configuration, first.Id), Subject: "Subject")));
        Assert.IsType<ConflictResult>(await controller.Approve(second.Id, new(Guid.Empty, Subject: "Subject")));
        Assert.IsType<ConflictResult>(await controller.Cancel(first.Id, Guid.Empty));
        Assert.False(await fixture.LinkAsync(second.Id, "Subject"));
        Assert.False(await fixture.LinkAsync(first.Id, "subject"));
        Assert.IsType<OkObjectResult>(await controller.Cancel(first.Id, AdministrationRules.Version(fixture.Configuration, first.Id)));
        Assert.True(await fixture.LinkAsync(second.Id, "Subject"));
        Assert.IsType<ConflictResult>(await controller.Approve(first.Id, new(AdministrationRules.Version(fixture.Configuration, first.Id), "", "Subject")));
    }

    [Fact]
    public async Task Durable_links_are_authoritative_over_pending_approvals()
    {
        using var fixture = new ProvisioningFixture();
        var first = await fixture.SeedUserAsync("first");
        var second = await fixture.SeedUserAsync("second");
        Assert.True(await fixture.LinkAsync(first.Id, "subject"));
        var controller = await fixture.AdminAsync();
        Assert.IsType<OkObjectResult>(await controller.Approve(second.Id, new(Guid.Empty, "user@example.test", "")));
        Assert.Equal(first.Id, await fixture.SignInAsync("subject"));
        Assert.NotNull(AdministrationRules.State(fixture.Configuration, second.Id).Request);
    }

    [Theory]
    [InlineData(AutomaticLinkingMode.None)]
    [InlineData(AutomaticLinkingMode.EmailToUsername)]
    [InlineData(AutomaticLinkingMode.UsernameToUsername)]
    public async Task Automatic_matching_uses_only_the_selected_key_and_never_replaces_existing_links(AutomaticLinkingMode mode)
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.AutomaticLinkingMode = mode;
        var emailUser = await fixture.SeedUserAsync("USER@example.test");
        var usernameUser = await fixture.SeedUserAsync("PROVIDER-NAME");
        var result = await fixture.SignInAsync("subject", username: "provider-name");
        Assert.Equal(mode == AutomaticLinkingMode.EmailToUsername ? emailUser.Id : mode == AutomaticLinkingMode.UsernameToUsername ? usernameUser.Id : (Guid?)null, result);
        if (result is not null)
        {
            Assert.Equal(result, await fixture.SignInAsync("subject", email: "changed@example.test", username: "changed-name"));
            Assert.Null(await fixture.SignInAsync("other-subject", username: "provider-name"));
        }
    }

    [Fact]
    public async Task No_automatic_linking_still_allows_provisioning_and_explicit_username_approval()
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.AutomaticLinkingMode = AutomaticLinkingMode.None;
        var emailUser = await fixture.SeedUserAsync("user@example.test");
        var created = await fixture.SignInAsync("new-subject", username: "new-name");
        Assert.NotNull(created);
        Assert.NotEqual(emailUser.Id, created);
        var user = await fixture.SeedUserAsync("local-name");
        var controller = await fixture.AdminAsync();
        Assert.IsType<OkObjectResult>(await controller.Approve(user.Id, new(Guid.Empty, Username: " Provider-Name ")));
        var state = AdministrationRules.State(fixture.Configuration, user.Id);
        Assert.Equal("Provider-Name", state.Request!.Username);
        Assert.Equal("preferred_username", state.Request.UsernameClaim);
        Assert.False(await fixture.LinkAsync(user.Id, "subject", username: "wrong-name"));
        Assert.Equal(user.Id, await fixture.SignInAsync("subject", username: "provider-name"));
        Assert.Null(state.Request);
        Assert.Equal("subject", fixture.Configuration.IdentityLinks.Single(item => item.UserId == user.Id).Subject);
        Assert.Equal("local-name", fixture.Users().Single(item => item.Id == user.Id).Username);
    }

    [Fact]
    public async Task Different_approval_types_matching_the_same_identity_deny_ambiguous_resolution()
    {
        using var fixture = new ProvisioningFixture();
        var first = await fixture.SeedUserAsync("first");
        var second = await fixture.SeedUserAsync("second");
        var controller = await fixture.AdminAsync();
        Assert.IsType<OkObjectResult>(await controller.Approve(first.Id, new(Guid.Empty, Email: "user@example.test")));
        Assert.IsType<OkObjectResult>(await controller.Approve(second.Id, new(Guid.Empty, Username: "provider-name")));
        Assert.Null(await fixture.SignInAsync("subject", username: "provider-name"));
        Assert.Empty(fixture.Configuration.IdentityLinks);
        Assert.False(await fixture.LinkAsync(first.Id, "subject", username: "provider-name"));
    }

    [Fact]
    public async Task Deleted_or_disabled_approval_targets_deny_fallback()
    {
        using var fixture = new ProvisioningFixture();
        var user = await fixture.SeedUserAsync("local-name");
        var controller = await fixture.AdminAsync();
        Assert.IsType<OkObjectResult>(await controller.Approve(user.Id, new(Guid.Empty, "", "subject")));
        await using (var context = fixture.CreateDbContext())
        {
            var target = await context.Users.Include(item => item.Permissions).SingleAsync(item => item.Id == user.Id);
            target.SetPermission(PermissionKind.IsDisabled, true);
            await context.SaveChangesAsync();
        }
        Assert.Null(await fixture.SignInAsync("subject", username: "new-name"));
        await using (var context = fixture.CreateDbContext())
        {
            context.Users.Remove(await context.Users.SingleAsync(item => item.Id == user.Id));
            await context.SaveChangesAsync();
        }
        Assert.Null(await fixture.SignInAsync("subject", username: "new-name"));
    }

    [Theory]
    [InlineData(PasswordLoginMode.AllowForAllUsers)]
    [InlineData(PasswordLoginMode.DisableForLinkedUsersOnly)]
    [InlineData(PasswordLoginMode.DisableForAllUsers)]
    public async Task Unlink_preserves_local_tokens_and_policy_and_blocks_old_callbacks(PasswordLoginMode mode)
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.PasswordLoginMode = mode;
        var user = await fixture.SeedUserAsync("USER@example.test");
        Assert.True(await fixture.LinkAsync(user.Id, "subject"));
        var oidc = await fixture.SeedSessionAsync(user.Id, tracked: true);
        var local = await fixture.SeedSessionAsync(user.Id, tracked: false);
        var controller = await fixture.AdminAsync();
        var revision = fixture.Configuration.IdentityRevision;
        var version = AdministrationRules.Version(fixture.Configuration, user.Id);
        Assert.IsType<OkObjectResult>(await controller.Unlink(user.Id, version));
        Assert.IsType<OkObjectResult>(await controller.Unlink(user.Id, version));
        Assert.False(fixture.TokenExists(oidc.Id));
        Assert.True(fixture.TokenExists(local.Id));
        Assert.Null(await fixture.SignInAsync("subject", revision: revision));
        Assert.False(await fixture.LinkAsync(user.Id, "subject", revision: revision));
        Assert.Null(await fixture.SignInAsync("subject"));
        var state = AdministrationRules.State(fixture.Configuration, user.Id);
        Assert.True(state.AutomaticReattachmentBlocked);
        var savedUser = fixture.Users().Single(item => item.Id == user.Id);
        Assert.Equal("existing-password", savedUser.Password);
        Assert.Equal(mode == PasswordLoginMode.DisableForAllUsers ? typeof(OidcPasswordDisabledProvider).FullName : ProvisioningFixture.DefaultProvider, savedUser.AuthenticationProviderId);
        Assert.True(await fixture.LinkAsync(user.Id, "subject"));
        Assert.False(state.AutomaticReattachmentBlocked);
        Assert.IsType<ConflictResult>(await controller.Unlink(user.Id, version));
        Assert.Single(fixture.Configuration.IdentityLinks);
    }

    [Fact]
    public async Task Failed_unlink_save_rolls_back_and_partial_revocation_retries_only_old_tokens()
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.PasswordLoginMode = PasswordLoginMode.DisableForLinkedUsersOnly;
        var user = await fixture.SeedUserAsync("local-name");
        Assert.True(await fixture.LinkAsync(user.Id, "subject"));
        var old = await fixture.SeedSessionAsync(user.Id, tracked: true);
        var controller = await fixture.AdminAsync();
        var version = AdministrationRules.Version(fixture.Configuration, user.Id);
        fixture.FailConfigurationSave = true;
        Assert.Equal(503, Assert.IsType<StatusCodeResult>(await controller.Unlink(user.Id, version)).StatusCode);
        Assert.Single(fixture.Configuration.IdentityLinks);
        Assert.True(fixture.TokenExists(old.Id));
        Assert.Equal(typeof(OidcPasswordDisabledProvider).FullName, fixture.Users().Single(item => item.Id == user.Id).AuthenticationProviderId);
        fixture.FailConfigurationSave = false;
        fixture.FailRevocation = true;
        Assert.IsType<OkObjectResult>(await controller.Unlink(user.Id, version));
        var state = AdministrationRules.State(fixture.Configuration, user.Id);
        Assert.Single(state.PendingRevocations);
        var operation = state.UnlinkOperation;
        Assert.True(await fixture.LinkAsync(user.Id, "subject"));
        var newer = await fixture.SeedSessionAsync(user.Id, tracked: true);
        fixture.FailRevocation = false;
        Assert.IsType<OkObjectResult>(await controller.Retry(user.Id, new(operation)));
        Assert.False(fixture.TokenExists(old.Id));
        Assert.True(fixture.TokenExists(newer.Id));
        Assert.Empty(state.PendingRevocations);
        Assert.IsType<OkObjectResult>(await controller.Retry(user.Id, new(operation)));
    }

    [Fact]
    public async Task Cancel_replace_and_failed_approval_persistence_invalidate_only_successful_mutations()
    {
        using var fixture = new ProvisioningFixture();
        var user = await fixture.SeedUserAsync("local-name");
        var controller = await fixture.AdminAsync();
        Assert.IsType<OkObjectResult>(await controller.Approve(user.Id, new(Guid.Empty, "", "subject")));
        var state = AdministrationRules.State(fixture.Configuration, user.Id);
        var revision = fixture.Configuration.IdentityRevision;
        fixture.FailConfigurationSave = true;
        Assert.Equal(503, Assert.IsType<StatusCodeResult>(await controller.Approve(user.Id, new(state.Version, "", "replacement"))).StatusCode);
        Assert.Equal("subject", state.Request!.Subject);
        Assert.Equal(revision, fixture.Configuration.IdentityRevision);
        fixture.FailConfigurationSave = false;
        Assert.IsType<OkObjectResult>(await controller.Approve(user.Id, new(state.Version, "", "replacement")));
        Assert.Null(await fixture.SignInAsync("subject", revision: revision));
        revision = fixture.Configuration.IdentityRevision;
        Assert.IsType<OkObjectResult>(await controller.Cancel(user.Id, state.Version));
        Assert.Null(await fixture.SignInAsync("replacement", revision: revision));
        Assert.Null(state.Request);
    }

    [Fact]
    public async Task Competing_administrator_approvals_are_serialized_and_recovery_works_when_disabled()
    {
        using var fixture = new ProvisioningFixture();
        var user = await fixture.SeedUserAsync("local-name");
        var controller = await fixture.AdminAsync();
        var results = await Task.WhenAll(controller.Approve(user.Id, new(Guid.Empty, "", "first")), controller.Approve(user.Id, new(Guid.Empty, "", "second")));
        Assert.Single(results.OfType<OkObjectResult>());
        Assert.Single(results.OfType<ConflictResult>());
        var state = AdministrationRules.State(fixture.Configuration, user.Id);
        Assert.True(await fixture.LinkAsync(user.Id, state.Request!.Subject));
        fixture.Configuration.Enabled = false;
        fixture.Configuration.IssuerUrl = "invalid";
        Assert.IsType<OkObjectResult>(await controller.Status(user.Id));
        Assert.IsType<OkObjectResult>(await controller.Unlink(user.Id, state.Version));
        Assert.Empty(fixture.Configuration.IdentityLinks);
        Assert.True(state.AutomaticReattachmentBlocked);
        Assert.IsType<ConflictResult>(await controller.Approve(user.Id, new(state.Version, "", "subject")));
    }

    [Fact]
    public async Task Disabled_invalid_configuration_keeps_administration_injection_local_to_the_path_base()
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.Enabled = false;
        fixture.Configuration.PublicUrl = "invalid-public-url";
        using var services = new ServiceCollection().BuildServiceProvider();
        var builder = new ApplicationBuilder(services);
        new WebInjectionStartupFilter().Configure(app => app.Run(async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><head></head><body>Jellyfin</body></html>");
        }))(builder);
        await using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.PathBase = "/jellyfin";
        context.Request.Path = "/web/index.html";
        context.Response.Body = body;
        await builder.Build()(context);
        var html = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("src=\"/jellyfin/oidc/web.js?v=", html);
        Assert.Contains("window.oidcEnabled=false", html);
        Assert.Contains("window.oidcPasswordLoginMode='AllowForAllUsers'", html);
        Assert.DoesNotContain("invalid-public-url", html);
        Assert.DoesNotContain("oidc-login-redirect-style", html);
        Assert.DoesNotContain(fixture.Configuration.ClientSecret, html);
    }

    [Fact]
    public async Task Ordinary_users_unlink_only_their_own_profile_and_preserve_unrelated_tokens()
    {
        using var fixture = new ProvisioningFixture();
        fixture.Configuration.PasswordLoginMode = PasswordLoginMode.DisableForLinkedUsersOnly;
        var owner = await fixture.SeedUserAsync("owner");
        var other = await fixture.SeedUserAsync("other");
        Assert.True(await fixture.LinkAsync(owner.Id, "owner-subject"));
        Assert.True(await fixture.LinkAsync(other.Id, "other-subject", email: "other@example.test"));
        var oidc = await fixture.SeedSessionAsync(owner.Id, true);
        var local = await fixture.SeedSessionAsync(owner.Id, false);
        var unrelated = await fixture.SeedSessionAsync(other.Id, true);
        var controller = fixture.Profile(owner.Id);
        var response = Assert.IsType<OkObjectResult>(await controller.Status());
        var json = JsonSerializer.Serialize(response.Value);
        Assert.DoesNotContain("owner-subject", json);
        Assert.DoesNotContain("other@example.test", json);
        Assert.DoesNotContain("Request", json);
        controller.Request.QueryString = new QueryString("?userId=" + other.Id);
        var version = AdministrationRules.Version(fixture.Configuration, owner.Id);
        Assert.IsType<OkObjectResult>(await controller.Unlink(version));
        Assert.IsType<OkObjectResult>(await controller.Unlink(version));
        Assert.False(fixture.TokenExists(oidc.Id));
        Assert.True(fixture.TokenExists(local.Id));
        Assert.True(fixture.TokenExists(unrelated.Id));
        Assert.Equal(other.Id, Assert.Single(fixture.Configuration.IdentityLinks).UserId);
        Assert.Equal(ProvisioningFixture.DefaultProvider, fixture.Users().Single(item => item.Id == owner.Id).AuthenticationProviderId);
        Assert.True(await fixture.LinkAsync(owner.Id, "owner-subject"));
        Assert.IsType<ConflictResult>(await controller.Unlink(version));
        controller.Request.Headers.Authorization = string.Empty;
        Assert.IsType<UnauthorizedResult>(await controller.Unlink(AdministrationRules.Version(fixture.Configuration, owner.Id)));
    }

    [Fact]
    public async Task Profile_unlink_rolls_back_failed_saves_and_supports_disabled_plugin_recovery()
    {
        using var fixture = new ProvisioningFixture();
        var owner = await fixture.SeedUserAsync("owner");
        Assert.True(await fixture.LinkAsync(owner.Id, "subject"));
        var oidc = await fixture.SeedSessionAsync(owner.Id, true);
        var controller = fixture.Profile(owner.Id);
        var version = AdministrationRules.Version(fixture.Configuration, owner.Id);
        fixture.FailConfigurationSave = true;
        Assert.Equal(503, Assert.IsType<StatusCodeResult>(await controller.Unlink(version)).StatusCode);
        Assert.Single(fixture.Configuration.IdentityLinks);
        Assert.True(fixture.TokenExists(oidc.Id));
        fixture.FailConfigurationSave = false;
        fixture.Configuration.Enabled = false;
        fixture.Configuration.IssuerUrl = "invalid";
        Assert.IsType<OkObjectResult>(await controller.Status());
        fixture.FailRevocation = true;
        Assert.IsType<OkObjectResult>(await controller.Unlink(version));
        var state = AdministrationRules.State(fixture.Configuration, owner.Id);
        Assert.Single(state.PendingRevocations);
        Assert.IsType<OkObjectResult>(await controller.Status());
        fixture.FailRevocation = false;
        Assert.IsType<OkObjectResult>(await controller.Retry(new(state.UnlinkOperation)));
        Assert.Empty(state.PendingRevocations);
        Assert.False(fixture.TokenExists(oidc.Id));
    }

    private sealed class ProvisioningFixture : IDisposable, IDbContextFactory<JellyfinDbContext>
    {
        public const string DefaultProvider = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "oidc-provisioning-tests-" + Guid.NewGuid().ToString("N"));
        private readonly OidcUserProvisioner _provisioner;
        private readonly IUserManager _manager;

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
                "GetUserDto" => new UserDto { Policy = new UserPolicy { IsAdministrator = ((User)args![0]!).HasPermission(PermissionKind.IsAdministrator), IsDisabled = ((User)args[0]!).HasPermission(PermissionKind.IsDisabled) } },
                "UpdatePolicyAsync" => UpdatePolicyAsync((Guid)args![0]!, (UserPolicy)args[1]!),
                _ => throw new InvalidOperationException("Unexpected user-manager call: " + method.Name),
            });
            _manager = manager;
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
        public bool FailRevocation { get; set; }

        public async Task<OidcAdminController> AdminAsync()
        {
            var actor = await SeedUserAsync("administrator-" + Guid.NewGuid().ToString("N"));
            await using (var context = CreateDbContext())
            {
                var user = await context.Users.Include(item => item.Permissions).SingleAsync(item => item.Id == actor.Id);
                user.SetPermission(PermissionKind.IsAdministrator, true);
                await context.SaveChangesAsync();
            }
            var auth = Stub<IAuthorizationContext>((_, _) => Task.FromResult(new AuthorizationInfo { IsAuthenticated = true, User = actor, Token = "local-admin-token" }));
            var controller = new OidcAdminController(_manager, auth, LinkManager(), NullLogger<OidcAdminController>.Instance) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            controller.Request.Headers.Authorization = "MediaBrowser Token=\"local-admin-token\"";
            return controller;
        }

        public OidcLinkController Profile(Guid userId)
        {
            var auth = Stub<IAuthorizationContext>((_, _) => Task.FromResult(new AuthorizationInfo { IsAuthenticated = true, User = _manager.GetUserById(userId), Token = "profile-token" }));
            var controller = new OidcLinkController(auth, new OidcLinkStore(TimeProvider.System), _provisioner, _manager, LinkManager(), NullLogger<OidcLinkController>.Instance)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            controller.Request.Headers.Authorization = "MediaBrowser Token=\"profile-token\"";
            return controller;
        }

        private OidcLinkManager LinkManager()
        {
            var devices = Stub<IDeviceManager>((method, args) => method.Name == "DeleteDevice" ? DeleteDeviceAsync((Device)args![0]!) : throw new InvalidOperationException(method.Name));
            var sessions = Stub<ISessionManager>((method, _) => method.Name == "get_Sessions" ? Array.Empty<SessionInfo>() : throw new InvalidOperationException(method.Name));
            var tracker = new OidcSessionTracker(this, devices, sessions, NullLogger<OidcSessionTracker>.Instance);
            return new OidcLinkManager(_manager, tracker, NullLogger<OidcLinkManager>.Instance);
        }

        public async Task<Device> SeedSessionAsync(Guid userId, bool tracked)
        {
            await using var context = CreateDbContext();
            var device = new Device(userId, "test", "1", "test", Guid.NewGuid().ToString("N"));
            context.Devices.Add(device);
            await context.SaveChangesAsync();
            if (tracked) Configuration.OidcSessions.Add(new OidcSessionProvenance { AuthenticationId = device.Id, UserId = userId, TokenFingerprint = OidcSessionTracker.Fingerprint(device.AccessToken) });
            return device;
        }

        public bool TokenExists(int id) { using var context = CreateDbContext(); return context.Devices.Any(item => item.Id == id); }

        private async Task DeleteDeviceAsync(Device device)
        {
            if (FailRevocation) throw new InvalidOperationException("Injected revocation failure.");
            await using var context = CreateDbContext();
            context.Devices.Remove(device);
            await context.SaveChangesAsync();
        }

        public Task<Guid?> SignInAsync(string subject, bool administrator = false, string? email = "user@example.test", bool verified = true, string? username = null, long? revision = null)
            => _provisioner.ProvisionAsync(Principal(subject, administrator, email, verified, null, username), Configuration, revision);

        public Task<bool> LinkAsync(Guid userId, string subject, bool administrator = false, string? email = "user@example.test", string? group = null, bool verified = true, string? username = null, long? revision = null)
            => _provisioner.LinkAsync(userId, Principal(subject, administrator, email, verified, group, username), Configuration, revision);

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
            foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(entity => entity.ClrType != typeof(User) && entity.ClrType != typeof(Permission) && entity.ClrType != typeof(Preference) && entity.ClrType != typeof(Device)).ToArray())
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
