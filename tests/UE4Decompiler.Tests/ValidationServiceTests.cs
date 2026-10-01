using CUE4Parse.UE4.Versions;
using UE4Decompiler.Core.Services;
using Xunit;

namespace UE4Decompiler.Tests;

public class ValidationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ValidationService _service;

    public ValidationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ue4decompiler_val_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _service = new ValidationService();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ValidateDirectoryAsync_EmptyDirectory_ReturnsAllValid()
    {
        var report = await _service.ValidateDirectoryAsync(_tempDir, EGame.GAME_UE4_21);

        Assert.NotNull(report);
        Assert.Equal(0, report.TotalChecked);
        Assert.True(report.IsAllValid);
    }

    [Fact]
    public async Task ValidateDirectoryAsync_InvalidPackageTag_FlagsInvalid()
    {
        var badFile = Path.Combine(_tempDir, "BadPackage.uasset");
        File.WriteAllBytes(badFile, new byte[64]); // zeros, not 0x9E2A83C1

        var report = await _service.ValidateDirectoryAsync(_tempDir, EGame.GAME_UE4_21);

        Assert.Equal(1, report.TotalChecked);
        Assert.Equal(1, report.InvalidCount);
        Assert.False(report.IsAllValid);
        Assert.Contains("Invalid package tag", report.Items[0].ErrorMessage);
    }

    [Fact]
    public async Task ValidateDirectoryAsync_ValidJsonFile_ValidatesSuccessfully()
    {
        var jsonFile = Path.Combine(_tempDir, "Model.json");
        File.WriteAllText(jsonFile, "{\n  \"AssetType\": \"StaticMesh\",\n  \"LODs\": 1\n}");

        var report = await _service.ValidateDirectoryAsync(_tempDir, EGame.GAME_UE4_21);

        Assert.Equal(1, report.TotalChecked);
        Assert.Equal(1, report.ValidCount);
        Assert.True(report.IsAllValid);
    }
}
