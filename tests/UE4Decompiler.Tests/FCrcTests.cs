using UE4Decompiler.Output.Writer;
using Xunit;

namespace UE4Decompiler.Tests;

public class FCrcTests
{
    [Fact]
    public void StrCrc32_KnownString_ReturnsConsistentHash()
    {
        var crc1 = FCrc.StrCrc32("UnrealEngine");
        var crc2 = FCrc.StrCrc32("UnrealEngine");

        Assert.NotEqual(0u, crc1);
        Assert.Equal(crc1, crc2);
    }

    [Fact]
    public void Strihash_DEPRECATED_ProducesValidHash()
    {
        var hashAnsi = FCrc.Strihash_DEPRECATED("PackageName", wide: false);
        var hashWide = FCrc.Strihash_DEPRECATED("PackageName", wide: true);

        Assert.NotEqual(0u, hashAnsi);
        Assert.NotEqual(0u, hashWide);
    }

    [Fact]
    public void NonCasePreservingHash_CaseInsensitive_Matches()
    {
        var hashUpper = FCrc.NonCasePreservingHash("MYASSET");
        var hashLower = FCrc.NonCasePreservingHash("myasset");

        Assert.Equal(hashUpper, hashLower);
    }

    [Fact]
    public void CasePreservingHash_CaseSensitive_Differs()
    {
        var hashUpper = FCrc.CasePreservingHash("MyAsset");
        var hashLower = FCrc.CasePreservingHash("myasset");

        Assert.NotEqual(hashUpper, hashLower);
    }
}
