using iBackup.Client.Core.Util;
using Xunit;

namespace iBackup.Client.UnitTests;

public class SubStreamTests
{
    private static MemoryStream Source() => new([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);

    [Fact]
    public async Task Reads_only_its_window()
    {
        await using var window = new SubStream(Source(), start: 3, length: 4);
        var buffer = new byte[10];
        var read = await window.ReadAsync(buffer);
        Assert.Equal(4, read);
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, buffer[..4]);
        Assert.Equal(0, await window.ReadAsync(buffer));
    }

    [Fact]
    public async Task Window_clamps_to_stream_end()
    {
        await using var window = new SubStream(Source(), start: 8, length: 100);
        Assert.Equal(2, window.Length);
        var buffer = new byte[10];
        Assert.Equal(2, await window.ReadAsync(buffer));
        Assert.Equal(new byte[] { 8, 9 }, buffer[..2]);
    }

    [Fact]
    public async Task Seek_to_begin_allows_rereading()
    {
        await using var window = new SubStream(Source(), start: 2, length: 3);
        var buffer = new byte[3];
        await window.ReadAsync(buffer);
        window.Position = 0;
        var buffer2 = new byte[3];
        await window.ReadAsync(buffer2);
        Assert.Equal(buffer, buffer2);
        Assert.Equal(new byte[] { 2, 3, 4 }, buffer2);
    }

    [Fact]
    public void Write_is_not_supported()
    {
        using var window = new SubStream(Source(), 0, 5);
        Assert.Throws<NotSupportedException>(() => window.Write([1], 0, 1));
    }
}
