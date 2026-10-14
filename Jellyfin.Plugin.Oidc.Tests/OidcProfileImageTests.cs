using Jellyfin.Plugin.Oidc.Identity;
using Xunit;

namespace Jellyfin.Plugin.Oidc.Tests;

public sealed class OidcProfileImageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(81921)]
    [InlineData(5 * 1024 * 1024)]
    public async Task Download_preserves_complete_bodies_up_to_the_size_limit(int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)0x5a);
        using var content = new StreamContent(new MemoryStream(bytes));
        await using var image = await OidcProfileImageSynchronizer.ReadImageAsync(
            content,
            CancellationToken.None
        );
        Assert.NotNull(image);
        Assert.Equal(bytes, image.ToArray());
    }

    [Fact]
    public async Task Download_deadline_cancels_a_stalled_body_after_headers()
    {
        using var content = new StreamContent(new StalledStream());
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OidcProfileImageSynchronizer
                .ReadImageAsync(content, deadline.Token)
                .WaitAsync(TimeSpan.FromSeconds(2))
        );
    }

    [Fact]
    public async Task Download_limit_rejects_oversized_streams_without_content_length()
    {
        using var content = new StreamContent(new MemoryStream(new byte[5 * 1024 * 1024 + 1]));
        Assert.Null(
            await OidcProfileImageSynchronizer.ReadImageAsync(content, CancellationToken.None)
        );
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
