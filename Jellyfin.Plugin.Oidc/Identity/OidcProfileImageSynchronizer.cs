using System.Net;
using System.Net.Sockets;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using Jellyfin.Plugin.Oidc.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Downloads and stores a Jellyfin User profile image supplied by an Identity Provider.</summary>
public sealed class OidcProfileImageSynchronizer(
    IServerConfigurationManager serverConfigurationManager,
    IUserManager userManager,
    IProviderManager providerManager,
    ILogger<OidcProfileImageSynchronizer> logger
)
{
    private const long MaximumImageBytes = 5 * 1024 * 1024;

    /// <summary>Synchronizes a standard OIDC picture claim when it is a safe image URL.</summary>
    public async Task SynchronizeAsync(
        User user,
        IdentityLink link,
        string? pictureUrl,
        string issuerUrl
    )
    {
        if (
            !Uri.TryCreate(pictureUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
        )
        {
            return;
        }

        try
        {
            var allowIssuerAddress =
                Uri.TryCreate(issuerUrl, UriKind.Absolute, out var issuer)
                && string.Equals(uri.Host, issuer.Host, StringComparison.OrdinalIgnoreCase);
            using var handler = new SocketsHttpHandler();
            handler.AllowAutoRedirect = false;
            handler.ConnectCallback = (context, cancellationToken) =>
                ConnectAsync(context, allowIssuerAddress, cancellationToken);
            handler.UseProxy = false;
            // A per-request handler enforces the picture-host boundary.
            // ReSharper disable once ShortLivedHttpClient
            using var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(10);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (
                user.ProfileImage is not null
                && string.Equals(link.ProfileImageUrl, pictureUrl, StringComparison.Ordinal)
            )
            {
                if (!string.IsNullOrEmpty(link.ProfileImageETag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", link.ProfileImageETag);
                }

                if (link.ProfileImageLastModified is not null)
                {
                    request.Headers.IfModifiedSince = link.ProfileImageLastModified;
                }
            }

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return;
            }

            if (
                !response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength > MaximumImageBytes
                || !Extension(response.Content.Headers.ContentType?.MediaType, out var extension)
            )
            {
                return;
            }

            await using var image = await ReadImageAsync(response.Content, deadline.Token)
                .ConfigureAwait(false);
            if (image is null)
                return;

            var userConfigurationDirectoryPath = serverConfigurationManager
                .ApplicationPaths
                .UserConfigurationDirectoryPath;
            var directory = Path.Combine(userConfigurationDirectoryPath, user.Username);
            if (!PathHelper.IsContainedIn(userConfigurationDirectoryPath, directory))
            {
                return;
            }

            if (user.ProfileImage is not null)
            {
                await userManager.ClearProfileImageAsync(user).ConfigureAwait(false);
            }

            user.ProfileImage = new ImageInfo(Path.Combine(directory, "profile" + extension));
            image.Position = 0;
            await providerManager
                .SaveImage(
                    image,
                    response.Content.Headers.ContentType!.MediaType,
                    user.ProfileImage.Path
                )
                .ConfigureAwait(false);
            await userManager.UpdateUserAsync(user).ConfigureAwait(false);
            link.ProfileImageUrl = pictureUrl;
            link.ProfileImageETag = response.Headers.ETag?.ToString() ?? string.Empty;
            link.ProfileImageLastModified = response.Content.Headers.LastModified;
        }
        catch (Exception exception)
            when (exception
                    is HttpRequestException
                        or IOException
                        or OperationCanceledException
                        or SocketException
            )
        {
            logger.LogWarning("OIDC profile image synchronization failed.");
        }
    }

    internal static async Task<MemoryStream?> ReadImageAsync(
        HttpContent content,
        CancellationToken cancellationToken
    )
    {
        await using var input = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var image = new MemoryStream();
        try
        {
            var buffer = new byte[81920];
            for (
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                read > 0;
                read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)
            )
            {
                if (image.Length + read > MaximumImageBytes)
                {
                    image.Dispose();
                    return null;
                }
                await image
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        bool allowIssuerAddress,
        CancellationToken cancellationToken
    )
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);
        foreach (var address in addresses.Where(address => allowIssuerAddress || IsPublic(address)))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket
                    .ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, true);
            }
            catch
            {
                socket.Dispose();
            }
        }

        throw new HttpRequestException("Profile image host did not resolve to a public address.");
    }

    private static bool Extension(string? mediaType, out string extension)
    {
        extension = mediaType?.ToLowerInvariant() switch
        {
            "image/gif" => ".gif",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => string.Empty,
        };
        return extension.Length > 0;
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (
            IPAddress.IsLoopback(address)
            || IPAddress.Any.Equals(address)
            || IPAddress.None.Equals(address)
            || IPAddress.IPv6Any.Equals(address)
            || IPAddress.IPv6None.Equals(address)
        )
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] != 0
                && bytes[0] != 10
                && bytes[0] != 127
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && bytes[0] < 224,
            AddressFamily.InterNetworkV6 => address
                is not (
                    { IsIPv6LinkLocal: true }
                    or { IsIPv6SiteLocal: true }
                    or { IsIPv6Multicast: true }
                )
                && (bytes[0] & 0xfe) != 0xfc,
            _ => false,
        };
    }
}
