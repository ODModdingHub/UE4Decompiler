using UE4Decompiler.Utils;
using Xunit;

namespace UE4Decompiler.Tests;

public class AesKeyResolverTests
{
    [Fact]
    public void FromHex_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(AesKeyResolver.FromHex(null));
        Assert.Null(AesKeyResolver.FromHex(""));
        Assert.Null(AesKeyResolver.FromHex("   "));
    }

    [Fact]
    public void FromHex_Valid64HexWith0x_ReturnsValidKey()
    {
        var hex = "0x" + new string('A', 64);
        var key = AesKeyResolver.FromHex(hex);

        Assert.NotNull(key);
        Assert.Contains("AA", key.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromHex_Valid64HexWithoutPrefix_ReturnsValidKey()
    {
        var hex = new string('F', 64);
        var key = AesKeyResolver.FromHex(hex);

        Assert.NotNull(key);
        Assert.Contains("FF", key.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0x1234")] // Too short
    [InlineData("0xZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")] // Non-hex
    public void FromHex_InvalidFormat_ThrowsArgumentException(string badHex)
    {
        Assert.Throws<ArgumentException>(() => AesKeyResolver.FromHex(badHex));
    }
}
