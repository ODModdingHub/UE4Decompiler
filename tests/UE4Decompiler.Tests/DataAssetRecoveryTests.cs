using System.Globalization;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using UE4Decompiler.Core;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Output;
using UE4Decompiler.Output.Stubs;
using UE4Decompiler.Reconstructors;
using Xunit;

namespace UE4Decompiler.Tests;

public class DataAssetRecoveryTests : IDisposable
{
    private readonly string _tempDir;

    public DataAssetRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ue4d_test_" + Guid.NewGuid().ToString("N"));
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
    public void DataTableReconstructor_EscapeCsv_HandlesSpecialCharacters()
    {
        Assert.Equal("SimpleText", DataTableReconstructor.EscapeCsv("SimpleText"));
        Assert.Equal("\"Text, With, Commas\"", DataTableReconstructor.EscapeCsv("Text, With, Commas"));
        Assert.Equal("\"Text With \"\"Quotes\"\"\"", DataTableReconstructor.EscapeCsv("Text With \"Quotes\""));
        Assert.Equal("\" LineWithLeadingSpace\"", DataTableReconstructor.EscapeCsv(" LineWithLeadingSpace"));
        Assert.Equal("\"LineWithNewline\n\"", DataTableReconstructor.EscapeCsv("LineWithNewline\n"));
    }

    [Fact]
    public void StubModuleGenerator_EmitsFTableRowBaseForDataTableStructs()
    {
        var gen = new StubModuleGenerator(null);
        var stubs = new List<GameStub>
        {
            new("MyGame", "ItemDefinitionRow", "ScriptStruct"),
            new("MyGame", "CharacterClass", "Class")
        };
        var baseHints = new Dictionary<string, string>
        {
            ["MyGame.ItemDefinitionRow"] = "FTableRowBase"
        };

        gen.Generate(_tempDir, stubs, baseHints);

        var headerPath = Path.Combine(_tempDir, "Source", "MyGame", "Public", "MyGame.h");
        Assert.True(File.Exists(headerPath));

        var headerContent = File.ReadAllText(headerPath);
        Assert.Contains("#include \"Engine/DataTable.h\"", headerContent);
        Assert.Contains("struct FItemDefinitionRow : public FTableRowBase", headerContent);
    }

    [Fact]
    public void ProjectScaffold_GeneratesDefaultInputAndUe5RendererSettings()
    {
        var opts = new DecompileOptions
        {
            InputPath = _tempDir,
            OutputRoot = _tempDir,
            ProjectName = "TestGame",
            Game = EGame.GAME_UE5_4,
            EngineAssociation = "5.4"
        };

        // Run scaffold with empty provider (no pak files -> triggers stand-in config generation)
        var provider = new DefaultFileProvider(new DirectoryInfo(_tempDir), SearchOption.TopDirectoryOnly, false, new VersionContainer(opts.Game));
        var scaffold = new ProjectScaffold(opts, provider);
        scaffold.Generate();

        var configDir = Path.Combine(_tempDir, "Config");
        Assert.True(Directory.Exists(configDir));

        var inputIni = Path.Combine(configDir, "DefaultInput.ini");
        Assert.True(File.Exists(inputIni));
        var inputContent = File.ReadAllText(inputIni);
        Assert.Contains("[/Script/Engine.InputSettings]", inputContent);
        Assert.Contains("ActionName=\"Jump\"", inputContent);
        Assert.Contains("AxisName=\"MoveForward\"", inputContent);

        var engineIni = Path.Combine(configDir, "DefaultEngine.ini");
        Assert.True(File.Exists(engineIni));
        var engineContent = File.ReadAllText(engineIni);
        Assert.Contains("[/Script/Engine.RendererSettings]", engineContent);
        Assert.Contains("r.Nanite=1", engineContent);
        Assert.Contains("r.Lumen.DiffuseIndirect.Allow=1", engineContent);
    }

    [Fact]
    public void AudioReconstructor_ReconstructsEmptyFallbackGracefully()
    {
        var recon = new AudioReconstructor();
        var testFilePath = Path.Combine(_tempDir, "Cue_Test.uasset");
        File.WriteAllBytes(testFilePath, Array.Empty<byte>());
        var osFile = new CUE4Parse.FileProvider.Objects.OsGameFile(new DirectoryInfo(_tempDir), new FileInfo(testFilePath), "/Game", new VersionContainer(EGame.GAME_UE4_21));
        var asset = new ParsedAsset
        {
            File = osFile,
            Package = null!,
            Exports = new List<CUE4Parse.UE4.Assets.Exports.UObject>(),
            PrimaryType = "SoundCue",
            Imports = new List<string>()
        };

        var result = recon.Reconstruct(asset, Path.Combine(_tempDir, "Cue_Test"));

        Assert.NotNull(result);
        Assert.Equal(Fidelity.Partial, result.Fidelity);
        Assert.Contains("SoundCue model preserved", result.Note);
    }

    [Fact]
    public void StringTableReconstructor_ReconstructsEmptyFallbackGracefully()
    {
        var recon = new StringTableReconstructor();
        var testFilePath = Path.Combine(_tempDir, "ST_Missing.uasset");
        File.WriteAllBytes(testFilePath, Array.Empty<byte>());
        var osFile = new CUE4Parse.FileProvider.Objects.OsGameFile(new DirectoryInfo(_tempDir), new FileInfo(testFilePath), "/Game", new VersionContainer(EGame.GAME_UE4_21));
        var asset = new ParsedAsset
        {
            File = osFile,
            Package = null!,
            Exports = new List<CUE4Parse.UE4.Assets.Exports.UObject>(),
            PrimaryType = "StringTable",
            Imports = new List<string>()
        };

        var result = recon.Reconstruct(asset, Path.Combine(_tempDir, "ST_Missing"));

        Assert.NotNull(result);
        Assert.Equal(Fidelity.Failed, result.Fidelity);
        Assert.Contains("No UStringTable export found", result.Note);
    }

    [Fact]
    public void CurveReconstructor_ReconstructsEmptyFallbackGracefully()
    {
        var recon = new CurveReconstructor();
        var testFilePath = Path.Combine(_tempDir, "Curve_Missing.uasset");
        File.WriteAllBytes(testFilePath, Array.Empty<byte>());
        var osFile = new CUE4Parse.FileProvider.Objects.OsGameFile(new DirectoryInfo(_tempDir), new FileInfo(testFilePath), "/Game", new VersionContainer(EGame.GAME_UE4_21));
        var asset = new ParsedAsset
        {
            File = osFile,
            Package = null!,
            Exports = new List<CUE4Parse.UE4.Assets.Exports.UObject>(),
            PrimaryType = "CurveFloat",
            Imports = new List<string>()
        };

        var result = recon.Reconstruct(asset, Path.Combine(_tempDir, "Curve_Missing"));

        Assert.NotNull(result);
        Assert.Equal(Fidelity.Failed, result.Fidelity);
        Assert.Contains("No curve export found", result.Note);
    }
}
