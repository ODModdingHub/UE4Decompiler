using UE4Decompiler.Core;
using Xunit;

namespace UE4Decompiler.Tests;

public class GlobMatcherTests
{
    [Theory]
    [InlineData("Characters/**", "Characters/Hero/BP_Hero.uasset", true)]
    [InlineData("Characters/**", "Maps/MainLevel.umap", false)]
    [InlineData("*.uasset", "Test.uasset", true)]
    [InlineData("*.uasset", "Test.umap", false)]
    [InlineData("Maps/*", "Maps/Sub/Deep.umap", false)]
    [InlineData("Maps/**", "Maps/Sub/Deep.umap", true)]
    public void GlobToRegex_MatchesExpectedVirtualPaths(string glob, string testPath, bool expectedMatch)
    {
        var regex = PakExtractor.GlobToRegex(glob);
        var match = regex.IsMatch(testPath);
        Assert.Equal(expectedMatch, match);
    }
}
