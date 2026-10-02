using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using UE4Decompiler.Core;
using UE4Decompiler.Core.Mcp;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Output;
using UE4Decompiler.Reconstructors;
using Xunit;

namespace UE4Decompiler.Tests;

public class AdvancedRecoveryTests : IDisposable
{
    private readonly string _tempDir;

    public AdvancedRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ue4d_adv_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ProjectScaffold_GeneratesDefaultGameplayTags()
    {
        var configDir = Path.Combine(_tempDir, "Config");
        Directory.CreateDirectory(configDir);

        var opts = new DecompileOptions
        {
            InputPath = _tempDir,
            OutputRoot = _tempDir,
            ProjectName = "TestGame",
            Game = EGame.GAME_UE5_3,
            EngineAssociation = "5.3"
        };

        var provider = new DefaultFileProvider(new DirectoryInfo(_tempDir), SearchOption.TopDirectoryOnly, false, new VersionContainer(EGame.GAME_UE5_3));
        var scaffold = new ProjectScaffold(opts, provider);

        var tags = new[] { "Ability.Skill.Fireball", "Weapon.Rifle.Reload", "State.Debuff.Stun", "Ability.Skill.Fireball" };
        scaffold.WriteDefaultGameplayTags(tags);

        var tagFile = Path.Combine(configDir, "DefaultGameplayTags.ini");
        Assert.True(File.Exists(tagFile));

        var content = File.ReadAllText(tagFile);
        Assert.Contains("[/Script/GameplayTags.GameplayTagsSettings]", content);
        Assert.Contains("+GameplayTagList=(Tag=\"Ability.Skill.Fireball\",DevComment=\"\")", content);
        Assert.Contains("+GameplayTagList=(Tag=\"Weapon.Rifle.Reload\",DevComment=\"\")", content);
        Assert.Contains("+GameplayTagList=(Tag=\"State.Debuff.Stun\",DevComment=\"\")", content);
    }

    [Fact]
    public void ProjectScaffold_GeneratesMasterReconstructionScript()
    {
        var opts = new DecompileOptions
        {
            InputPath = _tempDir,
            OutputRoot = _tempDir,
            ProjectName = "TestGame",
            Game = EGame.GAME_UE5_3,
            EngineAssociation = "5.3"
        };

        var provider = new DefaultFileProvider(new DirectoryInfo(_tempDir), SearchOption.TopDirectoryOnly, false, new VersionContainer(EGame.GAME_UE5_3));
        var scaffold = new ProjectScaffold(opts, provider);

        scaffold.Generate();

        var masterScript = Path.Combine(_tempDir, "Scripts", "ReconstructAllLevels.py");
        Assert.True(File.Exists(masterScript));

        var scriptText = File.ReadAllText(masterScript);
        Assert.Contains("import unreal", scriptText);
        Assert.Contains("ReconstructAllLevels", scriptText);
        Assert.Contains("_reconstruct.py", scriptText);
        Assert.Contains("_sockets.py", scriptText);
    }

    [Fact]
    public void ProjectScaffold_EnablesPythonAndEnhancedInputPlugins()
    {
        var opts = new DecompileOptions
        {
            InputPath = _tempDir,
            OutputRoot = _tempDir,
            ProjectName = "TestGame",
            Game = EGame.GAME_UE5_3,
            EngineAssociation = "5.3"
        };

        var provider = new DefaultFileProvider(new DirectoryInfo(_tempDir), SearchOption.TopDirectoryOnly, false, new VersionContainer(EGame.GAME_UE5_3));
        var scaffold = new ProjectScaffold(opts, provider);

        scaffold.Generate();

        var engineIni = Path.Combine(_tempDir, "Config", "DefaultEngine.ini");
        Assert.True(File.Exists(engineIni));

        var content = File.ReadAllText(engineIni);
        Assert.Contains("[Plugins]", content);
        Assert.Contains("+EnabledPlugins=\"EnhancedInput\"", content);
        Assert.Contains("+EnabledPlugins=\"PythonScriptPlugin\"", content);
        Assert.Contains("+EnabledPlugins=\"EditorScriptingUtilities\"", content);
    }

    [Fact]
    public void LevelLightingEnvironment_DataStructures_SerializeCorrectly()
    {
        var env = new LevelLightingEnvironment();
        env.DirectionalLights.Add(new DirectionalLightData
        {
            ActorName = "SunLight_01",
            Intensity = 120000f,
            LightColor = new byte[] { 255, 240, 220, 255 },
            LightSourceAngle = 0.5357f,
            AtmosphereSunLightIndex = 0,
            UsedAsAtmosphereSunLight = true
        });

        env.SkyLights.Add(new SkyLightData
        {
            ActorName = "SkyLight_01",
            Intensity = 1.5f,
            LowerHemisphereIsBlack = true
        });

        env.HeightFogs.Add(new HeightFogData
        {
            ActorName = "ExponentialHeightFog_01",
            FogDensity = 0.05f,
            EnableVolumetricFog = true
        });

        Assert.Equal(3, env.TotalLightingCount);

        var json = JsonSerializer.Serialize(env, new JsonSerializerOptions { WriteIndented = true });
        Assert.Contains("SunLight_01", json);
        Assert.Contains("SkyLight_01", json);
        Assert.Contains("ExponentialHeightFog_01", json);
        Assert.Contains("120000", json);
    }

    [Fact]
    public void SkeletalSocketData_And_BoneInfo_SerializeCorrectly()
    {
        var bones = new List<BoneInfoData>
        {
            new() { Index = 0, Name = "root", ParentIndex = -1 },
            new() { Index = 1, Name = "pelvis", ParentIndex = 0 },
            new() { Index = 2, Name = "spine_01", ParentIndex = 1 },
            new() { Index = 3, Name = "hand_r", ParentIndex = 2 }
        };

        var sockets = new List<SkeletalSocketData>
        {
            new()
            {
                SocketName = "Weapon_Socket",
                BoneName = "hand_r",
                Location = new[] { 10f, 0f, 5f },
                Rotation = new[] { 0f, 90f, 0f }
            }
        };

        var model = new
        {
            AssetType = "Skeleton",
            BoneCount = bones.Count,
            Bones = bones,
            SocketCount = sockets.Count,
            Sockets = sockets
        };

        var json = JsonSerializer.Serialize(model);
        Assert.Contains("Weapon_Socket", json);
        Assert.Contains("hand_r", json);
        Assert.Contains("pelvis", json);
    }

    [Fact]
    public void ActionKeyMappingData_SerializesEnhancedInputMappings()
    {
        var mapping = new ActionKeyMappingData
        {
            Action = "/Game/Input/Actions/IA_Jump",
            Key = "SpaceBar",
            IsPlayerMappable = true,
            Modifiers = new List<string> { "InputModifierDeadZone" },
            Triggers = new List<string> { "InputTriggerPressed" }
        };

        var json = JsonSerializer.Serialize(mapping);
        Assert.Contains("IA_Jump", json);
        Assert.Contains("SpaceBar", json);
        Assert.Contains("InputModifierDeadZone", json);
        Assert.Contains("InputTriggerPressed", json);
    }
}
