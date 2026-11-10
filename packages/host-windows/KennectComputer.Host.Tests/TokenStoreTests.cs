using KennectComputer.Host.Auth;

namespace KennectComputer.Host.Tests;

public sealed class TokenStoreTests
{
    [Fact]
    public void RotatesA32ByteTokenAndRejectsTheOldOne()
    {
        var tempDir = NewTempDir();
        var store = TokenStore.CreateForTests(tempDir);
        var first = store.Rotate();
        var second = store.Rotate();
        Assert.Equal(32, Base64Url.Decode(first).Length);
        Assert.False(store.Validate(first));
        Assert.True(store.Validate(second));
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kennect-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
