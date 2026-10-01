using UE4Decompiler.Core.Utils;
using Xunit;

namespace UE4Decompiler.Tests;

public class PathUtilsTests
{
    [Theory]
    [InlineData("Characters/Hero", "Characters/Hero")]
    [InlineData(@"Characters\Hero\Mesh", "Characters/Hero/Mesh")]
    [InlineData("Characters///Hero//Mesh", "Characters/Hero/Mesh")]
    public void Normalize_CleansSlashes(string input, string expected)
    {
        var result = PathUtils.Normalize(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SafeCombine_ValidRelativePath_ReturnsInsideRoot()
    {
        var tempDir = Path.GetTempPath();
        var combined = PathUtils.SafeCombine(tempDir, "SubDir/Asset.uasset");

        Assert.StartsWith(Path.GetFullPath(tempDir), combined, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Asset.uasset", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData(@"..\windows\system32")]
    [InlineData("foo/../../bar")]
    public void SafeCombine_DirectoryTraversalAttempt_ThrowsInvalidOperationException(string maliciousPath)
    {
        var root = Path.GetTempPath();
        Assert.Throws<InvalidOperationException>(() => PathUtils.SafeCombine(root, maliciousPath));
    }

    [Theory]
    [InlineData("SafeName", "SafeName")]
    [InlineData("Name:With?Bad*Chars", "Name_With_Bad_Chars")]
    [InlineData("", "unnamed")]
    public void SanitizeFileName_ReplacesInvalidCharacters(string input, string expected)
    {
        var result = PathUtils.SanitizeFileName(input);
        Assert.Equal(expected, result);
    }
}
