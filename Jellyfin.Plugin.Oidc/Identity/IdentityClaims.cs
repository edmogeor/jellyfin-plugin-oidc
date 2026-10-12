using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.Oidc.Configuration;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Validated identity details required for a Jellyfin sign-in.</summary>
public sealed record OidcIdentity(
    string Subject,
    string Email,
    bool IsAdministrator,
    string Username = ""
);

/// <summary>Extracts the deliberately small claim model supported by this plugin.</summary>
public static class IdentityClaims
{
    /// <summary>Validates the identity claims and configured group membership.</summary>
    public static bool TryCreate(
        ClaimsPrincipal principal,
        PluginConfiguration configuration,
        [NotNullWhen(true)] out OidcIdentity? identity
    )
    {
        identity = null;
        var subject = ClaimValue(principal, "sub");
        var email =
            bool.TryParse(ClaimValue(principal, "email_verified"), out var verified) && verified
                ? ClaimValue(principal, "email")
                : null;
        if (
            string.IsNullOrWhiteSpace(subject)
            || (configuration.RequireVerifiedEmail && string.IsNullOrWhiteSpace(email))
        )
        {
            return false;
        }

        var groups = principal
            .FindAll(configuration.GroupClaim.Trim())
            .SelectMany(claim => Groups(claim.Value))
            .ToHashSet(StringComparer.Ordinal);
        var isAdministrator = configuration
            .AdministratorGroup.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            )
            .Any(groups.Contains);
        var isUser = configuration
            .UserGroup.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            )
            .Any(groups.Contains);
        if (!isAdministrator && !isUser)
        {
            return false;
        }

        var usernameClaim = configuration.UsernameClaim.Trim();
        string? username = null;
        if (usernameClaim == "email")
        {
            username = email;
        }
        else if (
            principal.FindFirst(usernameClaim) is
            { ValueType: ClaimValueTypes.String } usernameValue
        )
        {
            username = usernameValue.Value;
        }
        identity = new OidcIdentity(
            subject,
            string.IsNullOrWhiteSpace(email) ? string.Empty : email,
            isAdministrator,
            string.IsNullOrWhiteSpace(username) ? string.Empty : username
        );
        return true;
    }

    private static string? ClaimValue(ClaimsPrincipal principal, string type) =>
        principal.FindFirst(type)?.Value;

    private static IEnumerable<string> Groups(string value)
    {
        if (!value.StartsWith("[", StringComparison.Ordinal))
        {
            return [value];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
