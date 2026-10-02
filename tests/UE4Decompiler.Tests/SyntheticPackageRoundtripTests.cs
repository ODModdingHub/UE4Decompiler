using CUE4Parse.UE4.Versions;
using UE4Decompiler.Core;
using Xunit;

namespace UE4Decompiler.Tests;

public class SyntheticPackageRoundtripTests : IDisposable
{
    private readonly string _tempFile;

    public SyntheticPackageRoundtripTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), "test_package_" + Guid.NewGuid().ToString("N") + ".uasset");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            try { File.Delete(_tempFile); } catch { }
        }
    }

    [Fact]
    public void WriteUAssetHeader_EmitsValidSummaryHeader()
    {
        var writer = new AssetWriter();
        writer.WriteUAssetHeader(_tempFile, EGame.GAME_UE4_21, "/Game/TestAsset");

        Assert.True(File.Exists(_tempFile));
        var bytes = File.ReadAllBytes(_tempFile);

        // Verify PACKAGE_FILE_TAG = 0x9E2A83C1
        var tag = BitConverter.ToUInt32(bytes, 0);
        Assert.Equal(0x9E2A83C1u, tag);

        // Verify LegacyFileVersion = -8
        var legacyVer = BitConverter.ToInt32(bytes, 4);
        Assert.Equal(-8, legacyVer);

        // Verify TotalHeaderSize patched into byte offset 24
        var headerSize = BitConverter.ToInt32(bytes, 24);
        Assert.True(headerSize > 0);
        Assert.Equal(bytes.Length, headerSize);
    }
}
