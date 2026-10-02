using CUE4Parse.UE4.Versions;
using UE4Decompiler.Utils;
using Xunit;

namespace UE4Decompiler.Tests;

public class VersionDetectorTests
{
    [Theory]
    [InlineData("4.21", EGame.GAME_UE4_21)]
    [InlineData("4.27", EGame.GAME_UE4_27)]
    [InlineData("5.0", EGame.GAME_UE5_0)]
    [InlineData("5.1", EGame.GAME_UE5_1)]
    [InlineData("5.2", EGame.GAME_UE5_2)]
    [InlineData("5.3", EGame.GAME_UE5_3)]
    [InlineData("5.4", EGame.GAME_UE5_4)]
    public void FromHint_SupportedVersions_ResolvesExpectedEGame(string hint, EGame expected)
    {
        var result = VersionDetector.FromHint(hint);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid-version")]
    public void FromHint_InvalidOrEmpty_ReturnsNull(string? hint)
    {
        var result = VersionDetector.FromHint(hint);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(EGame.GAME_UE4_21, "4.21")]
    [InlineData(EGame.GAME_UE4_27, "4.27")]
    [InlineData(EGame.GAME_UE5_1, "5.1")]
    [InlineData(EGame.GAME_UE5_3, "5.3")]
    public void ToEngineAssociation_StandardGames_ProducesEngineString(EGame game, string expected)
    {
        var assoc = VersionDetector.ToEngineAssociation(game);
        Assert.Equal(expected, assoc);
    }
}
