using System.Net;
using System.Net.Sockets;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Oidc.Identity;

/// <summary>Downloads and stores a Jellyfin User profile image supplied by an Identity Provider.</summary>
public sealed class OidcProfileImageSynchronizer
{
    private const long MaximumImageBytes = 5 * 1024 * 1024;
    private readonly ILogger<OidcProfileImageSynchronizer> _logger;
    private readonly IProviderManager _providerManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IUserManager _userManager;

    /// <summary>Initializes a new instance of the <see cref="OidcProfileImageSynchronizer"/> class.</summary>
    public OidcProfileImageSynchronizer(
        IServerConfigurationManager serverConfigurationManager,
        IUserManager userManager,
        IProviderManager providerManager,
        ILogger<OidcProfileImageSynchronizer> logger)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _userManager = userManager;
        _providerManager = providerManager;
        _logger = logger;
    }

    /// <summary>Synchronizes a standard OIDC picture claim when it is a safe image URL.</summary>
    public async Task SynchronizeAsync(User user, string? pictureUrl, string issuerUrl)
    {
        if (!Uri.TryCreate(pictureUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            var allowIssuerAddress = Uri.TryCreate(issuerUrl, UriKind.Absolute, out var issuer)
                && string.Equals(uri.Host, issuer.Host, StringComparison.OrdinalIgnoreCase);
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = (context, cancellationToken) => ConnectAsync(context, allowIssuerAddress, cancellationToken),
                UseProxy = false,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength > MaximumImageBytes
                || !Extension(response.Content.Headers.ContentType?.MediaType, out var extension))
            {
                return;
            }

            await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var image = new MemoryStream();
            var buffer = new byte[81920];
            for (var read = await input.ReadAsync(buffer).ConfigureAwait(false); read > 0; read = await input.ReadAsync(buffer).ConfigureAwait(false))
            {
                if (image.Length + read > MaximumImageBytes)
                {
                    return;
                }

                await image.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }

            var userConfigurationDirectoryPath = _serverConfigurationManager.ApplicationPaths.UserConfigurationDirectoryPath;
            var directory = Path.Combine(userConfigurationDirectoryPath, user.Username);
            if (!PathHelper.IsContainedIn(userConfigurationDirectoryPath, directory))
            {
                return;
            }

            if (user.ProfileImage is not null)
            {
                await _userManager.ClearProfileImageAsync(user).ConfigureAwait(false);
            }

            user.ProfileImage = new ImageInfo(Path.Combine(directory, "profile" + extension));
            image.Position = 0;
            await _providerManager.SaveImage(image, response.Content.Headers.ContentType!.MediaType, user.ProfileImage.Path).ConfigureAwait(false);
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or SocketException)
        {
            _logger.LogWarning("OIDC profile image synchronization failed.");
        }
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, bool allowIssuerAddress, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        foreach (var address in addresses.Where(address => allowIssuerAddress || IsPublic(address)))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
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

        if (IPAddress.IsLoopback(address)
            || IPAddress.Any.Equals(address)
            || IPAddress.None.Equals(address)
            || IPAddress.IPv6Any.Equals(address)
            || IPAddress.IPv6None.Equals(address))
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
            AddressFamily.InterNetworkV6 => !address.IsIPv6LinkLocal
                && !address.IsIPv6SiteLocal
                && !address.IsIPv6Multicast
                && (bytes[0] & 0xfe) != 0xfc,
            _ => false,
        };
    }
}
