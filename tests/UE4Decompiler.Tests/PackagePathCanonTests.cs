using UE4Decompiler.Output.Writer;
using Xunit;

namespace UE4Decompiler.Tests;

public class PackagePathCanonTests
{
    [Fact]
    public void ToEditorPath_GameMount_ConvertsToGamePath()
    {
        var result = PackagePathCanon.ToEditorPath("MyGame/Content/Maps/Arena.umap");
        Assert.Equal("/Game/Maps/Arena", result);
    }

    [Fact]
    public void ToEditorPath_EngineMount_ConvertsToEnginePath()
    {
        var result = PackagePathCanon.ToEditorPath("Engine/Content/BasicShapes/Cube.uasset");
        Assert.Equal("/Engine/BasicShapes/Cube", result);
    }

    [Fact]
    public void ToEditorPath_PluginMount_ConvertsToPluginRoot()
    {
        var result = PackagePathCanon.ToEditorPath("MyGame/Plugins/CustomTools/Content/Widgets/ToolUI.uasset");
        Assert.Equal("/CustomTools/Widgets/ToolUI", result);
    }

    [Fact]
    public void Normalize_WithBuiltCanonMap_CanonicalizesCase()
    {
        PackagePathCanon.Reset();
        try
        {
            var keys = new[]
            {
                "MyGame/Content/Characters/HeroKnight.uasset",
                "MyGame/Content/Environments/CastleWall.uasset"
            };
            PackagePathCanon.Build(keys);

            // Mismatched case queries should be normalized to the canonical on-disk casing
            var normalized1 = PackagePathCanon.Normalize("/game/characters/heroknight");
            Assert.Equal("/Game/Characters/HeroKnight", normalized1);

            var normalized2 = PackagePathCanon.Normalize("/GAME/ENVIRONMENTS/CASTLEWALL");
            Assert.Equal("/Game/Environments/CastleWall", normalized2);

            // Non-game path returns unchanged
            var enginePath = "/Engine/BasicShapes/Cube";
            Assert.Equal(enginePath, PackagePathCanon.Normalize(enginePath));
        }
        finally
        {
            PackagePathCanon.Reset();
        }
    }
}
