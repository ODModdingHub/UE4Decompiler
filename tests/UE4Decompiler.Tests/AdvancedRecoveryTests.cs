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

    [Fact]
    public void ProjectScaffold_GeneratesDefaultCollision()
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

        var profiles = new[] { "CustomProjectile", "WeaponTrace" };
        var channels = new[] { "ECC_GameTraceChannel1", "ECC_GameTraceChannel2" };
        scaffold.WriteDefaultCollision(profiles, channels);

        var defaultEngine = Path.Combine(configDir, "DefaultEngine.ini");
        Assert.True(File.Exists(defaultEngine));

        var content = File.ReadAllText(defaultEngine);
        Assert.Contains("[/Script/Engine.CollisionProfile]", content);
        Assert.Contains("+DefaultChannelResponses=(Channel=ECC_GameTraceChannel1,DefaultResponse=ECR_Block", content);
        Assert.Contains("+Profiles=(Name=\"CustomProjectile\",CollisionEnabled=ECollisionEnabled::QueryAndPhysics", content);
        Assert.Contains("+Profiles=(Name=\"WeaponTrace\",CollisionEnabled=ECollisionEnabled::QueryAndPhysics", content);
    }

    [Fact]
    public async Task McpToolRegistry_ExportReconstructionScripts_InventoriesProject()
    {
        var scriptsDir = Path.Combine(_tempDir, "Scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(Path.Combine(scriptsDir, "MainMap_reconstruct.py"), "# py");
        File.WriteAllText(Path.Combine(scriptsDir, "SK_Hero_sockets.py"), "# py");
        File.WriteAllText(Path.Combine(scriptsDir, "M_Metal_mic_setup.py"), "# py");
        File.WriteAllText(Path.Combine(scriptsDir, "Cue_Explosion_soundcue.py"), "# py");
        File.WriteAllText(Path.Combine(scriptsDir, "Curve_Recoil_setup.py"), "# py");

        var registry = new McpToolRegistry();
        var args = JsonDocument.Parse($"{{\"outputRoot\": \"{_tempDir.Replace("\\", "\\\\")}\"}}").RootElement;
        var result = await registry.CallToolAsync("ue_export_reconstruction_scripts", args);

        Assert.False(result.IsError);
        var json = result.Content.FirstOrDefault()?.Text;
        Assert.NotNull(json);
        Assert.Contains("TotalScripts\": 5", json);
        Assert.Contains("MainMap_reconstruct.py", json);
        Assert.Contains("SK_Hero_sockets.py", json);
        Assert.Contains("M_Metal_mic_setup.py", json);
    }

    [Fact]
    public void ProjectScaffold_GeneratesDefaultPhysics_PhysicalSurfaces()
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

        var surfaces = new Dictionary<string, string>
        {
            ["SurfaceType1"] = "PM_Flesh",
            ["SurfaceType2"] = "PM_Concrete",
            ["SurfaceType3"] = "PM_Wood"
        };
        scaffold.WriteDefaultPhysics(surfaces);

        var defaultEngine = Path.Combine(configDir, "DefaultEngine.ini");
        Assert.True(File.Exists(defaultEngine));

        var content = File.ReadAllText(defaultEngine);
        Assert.Contains("[/Script/Engine.PhysicsSettings]", content);
        Assert.Contains("+PhysicalSurfaces=(Type=SurfaceType1,Name=\"PM_Flesh\")", content);
        Assert.Contains("+PhysicalSurfaces=(Type=SurfaceType2,Name=\"PM_Concrete\")", content);
        Assert.Contains("+PhysicalSurfaces=(Type=SurfaceType3,Name=\"PM_Wood\")", content);
    }

    [Fact]
    public void WidgetNodeData_ExtractsAndModelsCorrectly()
    {
        var node = new WidgetNodeData
        {
            Name = "StartGameBtn",
            WidgetClass = "Button",
            IsRoot = false,
            IsVariable = true,
            Visibility = "Visible",
            Text = "Play Game",
            SlotType = "CanvasPanelSlot",
            Anchors = new[] { 0.5f, 0.5f, 0.5f, 0.5f },
            Offsets = new[] { -100f, -25f, 200f, 50f },
            ZOrder = 10,
            AutoSize = false
        };

        var json = JsonSerializer.Serialize(node);
        Assert.Contains("StartGameBtn", json);
        Assert.Contains("Button", json);
        Assert.Contains("Play Game", json);
        Assert.Contains("CanvasPanelSlot", json);
        Assert.Contains("0.5", json);
        Assert.Contains("10", json);
    }

    [Fact]
    public void DetailedPhysicsBodyData_ModelsCollisionPrimitives()
    {
        var body = new DetailedPhysicsBodyData
        {
            BodyName = "pelvis_Body",
            BoneName = "pelvis",
            PhysicsType = "Simulated"
        };
        body.Spheres.Add(new SphereElemData { Center = new[] { 0f, 0f, 0f }, Radius = 15f });
        body.Boxes.Add(new BoxElemData { Center = new[] { 0f, 5f, 0f }, X = 20f, Y = 30f, Z = 20f });
        body.Capsules.Add(new CapsuleElemData { Center = new[] { 0f, 0f, 10f }, Radius = 12f, Length = 25f });

        var json = JsonSerializer.Serialize(body);
        Assert.Contains("pelvis_Body", json);
        Assert.Contains("pelvis", json);
        Assert.Contains("Simulated", json);
        Assert.Contains("15", json);
        Assert.Contains("30", json);
        Assert.Contains("25", json);
    }

    [Fact]
    public void NiagaraAndCascade_ModelsSerializeParameters()
    {
        var niagara = new
        {
            AssetType = "NiagaraSystem",
            Name = "NS_FireExplosion",
            WarmupTime = 0.5f,
            UserParameters = new[]
            {
                new NiagaraParamData { Name = "User.SpawnRate", TypeName = "Float" },
                new NiagaraParamData { Name = "User.PrimaryColor", TypeName = "LinearColor" }
            },
            Emitters = new[]
            {
                new NiagaraEmitterData { Name = "Flames", SimTarget = "GPUSim", IsEnabled = true }
            }
        };

        var json = JsonSerializer.Serialize(niagara);
        Assert.Contains("NS_FireExplosion", json);
        Assert.Contains("User.SpawnRate", json);
        Assert.Contains("GPUSim", json);
    }

    [Fact]
    public void FoliageType_ModelSerializesDensityAndCollision()
    {
        var foliage = new
        {
            AssetType = "FoliageType",
            Name = "FT_OakTree",
            Density = 150.0f,
            Radius = 200.0f,
            CastShadow = true,
            CollisionProfile = "BlockAll"
        };

        var json = JsonSerializer.Serialize(foliage);
        Assert.Contains("FT_OakTree", json);
        Assert.Contains("150", json);
        Assert.Contains("200", json);
        Assert.Contains("BlockAll", json);
    }

    [Fact]
    public void WidgetReconstructor_EmptyFallback_HandlesGracefully()
    {
        var recon = new WidgetReconstructor();
        var testFilePath = Path.Combine(_tempDir, "WBP_Test.uasset");
        File.WriteAllBytes(testFilePath, Array.Empty<byte>());
        var osFile = new CUE4Parse.FileProvider.Objects.OsGameFile(new DirectoryInfo(_tempDir), new FileInfo(testFilePath), "/Game", new VersionContainer(EGame.GAME_UE4_21));
        var asset = new ParsedAsset
        {
            File = osFile,
            Package = null!,
            Exports = new List<CUE4Parse.UE4.Assets.Exports.UObject>(),
            PrimaryType = "WidgetBlueprint",
            Imports = new List<string>()
        };

        var result = recon.Reconstruct(asset, Path.Combine(_tempDir, "WBP_Test"));
        Assert.NotNull(result);
        Assert.Equal(Fidelity.Full, result.Fidelity);
        Assert.True(File.Exists(Path.Combine(_tempDir, "WBP_Test_widget_tree.json")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "WBP_Test_widget_setup.py")));
    }

    [Fact]
    public void PhysicsReconstructor_EmptyFallback_HandlesGracefully()
    {
        var recon = new PhysicsReconstructor();
        var testFilePath = Path.Combine(_tempDir, "PM_Empty.uasset");
        File.WriteAllBytes(testFilePath, Array.Empty<byte>());
        var osFile = new CUE4Parse.FileProvider.Objects.OsGameFile(new DirectoryInfo(_tempDir), new FileInfo(testFilePath), "/Game", new VersionContainer(EGame.GAME_UE4_21));
        var asset = new ParsedAsset
        {
            File = osFile,
            Package = null!,
            Exports = new List<CUE4Parse.UE4.Assets.Exports.UObject>(),
            PrimaryType = "PhysicalMaterial",
            Imports = new List<string>()
        };

        var result = recon.Reconstruct(asset, Path.Combine(_tempDir, "PM_Empty"));
        Assert.NotNull(result);
        Assert.Equal(Fidelity.Failed, result.Fidelity);
    }

    [Fact]
    public void ParticleReconstructor_EmptyFallback_HandlesGracefully()
    {
        var recon = new ParticleReconstructor();
        var testFilePath = Path.Combine(_tempDir, "NS_Empty.uasset");
        File.WriteAllBytes(testFilePath, Array.Empty<byte>());
        var osFile = new CUE4Parse.FileProvider.Objects.OsGameFile(new DirectoryInfo(_tempDir), new FileInfo(testFilePath), "/Game", new VersionContainer(EGame.GAME_UE4_21));
        var asset = new ParsedAsset
        {
            File = osFile,
            Package = null!,
            Exports = new List<CUE4Parse.UE4.Assets.Exports.UObject>(),
            PrimaryType = "NiagaraSystem",
            Imports = new List<string>()
        };

        var result = recon.Reconstruct(asset, Path.Combine(_tempDir, "NS_Empty"));
        Assert.NotNull(result);
        Assert.Equal(Fidelity.Failed, result.Fidelity);
    }

    [Fact]
    public void GameplayAbilityReconstructor_EmptyFallback_HandlesGracefully()
    {
        var recon = new GameplayAbilityReconstructor();
        var testFilePath = Path.Combine(_tempDir, "GE_Empty.uasset");
        File.WriteAllBytes(testFilePath, Array.Empty<byte>());
        var osFile = new CUE4Parse.FileProvider.Objects.OsGameFile(new DirectoryInfo(_tempDir), new FileInfo(testFilePath), "/Game", new VersionContainer(EGame.GAME_UE4_21));
        var asset = new ParsedAsset
        {
            File = osFile,
            Package = null!,
            Exports = new List<CUE4Parse.UE4.Assets.Exports.UObject>(),
            PrimaryType = "GameplayEffect",
            Imports = new List<string>()
        };

        var result = recon.Reconstruct(asset, Path.Combine(_tempDir, "GE_Empty"));
        Assert.NotNull(result);
        Assert.Equal(Fidelity.Failed, result.Fidelity);
    }

    [Fact]
    public void FoliageReconstructor_EmptyFallback_HandlesGracefully()
    {
        var recon = new FoliageReconstructor();
        var testFilePath = Path.Combine(_tempDir, "FT_Empty.uasset");
        File.WriteAllBytes(testFilePath, Array.Empty<byte>());
        var osFile = new CUE4Parse.FileProvider.Objects.OsGameFile(new DirectoryInfo(_tempDir), new FileInfo(testFilePath), "/Game", new VersionContainer(EGame.GAME_UE4_21));
        var asset = new ParsedAsset
        {
            File = osFile,
            Package = null!,
            Exports = new List<CUE4Parse.UE4.Assets.Exports.UObject>(),
            PrimaryType = "FoliageType",
            Imports = new List<string>()
        };

        var result = recon.Reconstruct(asset, Path.Combine(_tempDir, "FT_Empty"));
        Assert.NotNull(result);
        Assert.Equal(Fidelity.Failed, result.Fidelity);
    }

    [Fact]
    public void ProjectScaffold_GeneratesScalabilityAndDeviceProfiles()
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

        var scalability = Path.Combine(_tempDir, "Config", "DefaultScalability.ini");
        Assert.True(File.Exists(scalability));
        var scalText = File.ReadAllText(scalability);
        Assert.Contains("[ViewDistanceQuality@3]", scalText);
        Assert.Contains("[ShadowQuality@3]", scalText);
        Assert.Contains("[FoliageQuality@3]", scalText);

        var deviceProfiles = Path.Combine(_tempDir, "Config", "DefaultDeviceProfiles.ini");
        Assert.True(File.Exists(deviceProfiles));
        var devText = File.ReadAllText(deviceProfiles);
        Assert.Contains("[Windows DeviceProfile]", devText);
    }

    [Fact]
    public void LandscapeReconstructor_EmptyFallback_HandlesGracefully()
    {
        var recon = new LandscapeReconstructor();
        var testFilePath = Path.Combine(_tempDir, "Landscape_Empty.uasset");
        File.WriteAllBytes(testFilePath, Array.Empty<byte>());
        var osFile = new CUE4Parse.FileProvider.Objects.OsGameFile(new DirectoryInfo(_tempDir), new FileInfo(testFilePath), "/Game", new VersionContainer(EGame.GAME_UE4_21));
        var asset = new ParsedAsset
        {
            File = osFile,
            Package = null!,
            Exports = new List<CUE4Parse.UE4.Assets.Exports.UObject>(),
            PrimaryType = "Landscape",
            Imports = new List<string>()
        };

        var result = recon.Reconstruct(asset, Path.Combine(_tempDir, "Landscape_Empty"));
        Assert.NotNull(result);
        Assert.Equal(Fidelity.Failed, result.Fidelity);
    }

    [Fact]
    public void SubsurfaceReconstructor_EmptyFallback_HandlesGracefully()
    {
        var recon = new SubsurfaceReconstructor();
        var testFilePath = Path.Combine(_tempDir, "SSP_Empty.uasset");
        File.WriteAllBytes(testFilePath, Array.Empty<byte>());
        var osFile = new CUE4Parse.FileProvider.Objects.OsGameFile(new DirectoryInfo(_tempDir), new FileInfo(testFilePath), "/Game", new VersionContainer(EGame.GAME_UE4_21));
        var asset = new ParsedAsset
        {
            File = osFile,
            Package = null!,
            Exports = new List<CUE4Parse.UE4.Assets.Exports.UObject>(),
            PrimaryType = "SubsurfaceProfile",
            Imports = new List<string>()
        };

        var result = recon.Reconstruct(asset, Path.Combine(_tempDir, "SSP_Empty"));
        Assert.NotNull(result);
        Assert.Equal(Fidelity.Failed, result.Fidelity);
    }

    [Fact]
    public void MediaReconstructor_EmptyFallback_HandlesGracefully()
    {
        var recon = new MediaReconstructor();
        var testFilePath = Path.Combine(_tempDir, "Media_Empty.uasset");
        File.WriteAllBytes(testFilePath, Array.Empty<byte>());
        var osFile = new CUE4Parse.FileProvider.Objects.OsGameFile(new DirectoryInfo(_tempDir), new FileInfo(testFilePath), "/Game", new VersionContainer(EGame.GAME_UE4_21));
        var asset = new ParsedAsset
        {
            File = osFile,
            Package = null!,
            Exports = new List<CUE4Parse.UE4.Assets.Exports.UObject>(),
            PrimaryType = "MediaPlayer",
            Imports = new List<string>()
        };

        var result = recon.Reconstruct(asset, Path.Combine(_tempDir, "Media_Empty"));
        Assert.NotNull(result);
        Assert.Equal(Fidelity.Failed, result.Fidelity);
    }

    [Fact]
    public void IKRigReconstructor_EmptyFallback_HandlesGracefully()
    {
        var recon = new IKRigReconstructor();
        var testFilePath = Path.Combine(_tempDir, "IKR_Empty.uasset");
        File.WriteAllBytes(testFilePath, Array.Empty<byte>());
        var osFile = new CUE4Parse.FileProvider.Objects.OsGameFile(new DirectoryInfo(_tempDir), new FileInfo(testFilePath), "/Game", new VersionContainer(EGame.GAME_UE5_1));
        var asset = new ParsedAsset
        {
            File = osFile,
            Package = null!,
            Exports = new List<CUE4Parse.UE4.Assets.Exports.UObject>(),
            PrimaryType = "IKRigDefinition",
            Imports = new List<string>()
        };

        var result = recon.Reconstruct(asset, Path.Combine(_tempDir, "IKR_Empty"));
        Assert.NotNull(result);
        Assert.Equal(Fidelity.Failed, result.Fidelity);
    }
}


