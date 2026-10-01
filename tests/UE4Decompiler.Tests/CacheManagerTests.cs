using UE4Decompiler.Core.Models;
using UE4Decompiler.Core.Services;
using Xunit;

namespace UE4Decompiler.Tests;

public class CacheManagerTests : IDisposable
{
    private readonly string _testCacheDir;
    private readonly CacheManager _cache;

    public CacheManagerTests()
    {
        _testCacheDir = Path.Combine(Path.GetTempPath(), "ue4decompiler_test_cache_" + Guid.NewGuid().ToString("N"));
        _cache = new CacheManager(_testCacheDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testCacheDir))
        {
            try { Directory.Delete(_testCacheDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ComputeCacheKey_DeterministicForIdenticalInput()
    {
        var key1 = _cache.ComputeCacheKey("Characters/Hero.uasset", 1024, "4.27", "opt-v1");
        var key2 = _cache.ComputeCacheKey("Characters/Hero.uasset", 1024, "4.27", "opt-v1");
        var key3 = _cache.ComputeCacheKey("Characters/Hero.uasset", 2048, "4.27", "opt-v1");

        Assert.Equal(key1, key2);
        Assert.NotEqual(key1, key3);
        Assert.Equal(64, key1.Length); // SHA256 hex string
    }

    [Fact]
    public void StoreAndRetrieve_StoresEntryOnDisk()
    {
        var key = _cache.ComputeCacheKey("Maps/Level.umap", 4096, "5.3", "v1");
        var entry = new CachedAssetResult
        {
            CacheKey = key,
            VirtualPath = "Maps/Level.umap",
            ClassName = "World",
            Status = RecoveryStatus.Recovered,
            CreatedFiles = new List<string> { "Level.umap", "Level.json" },
            TimestampUtc = DateTime.UtcNow
        };

        _cache.Store(key, entry);

        var found = _cache.TryGetCached(key, out var retrieved);
        Assert.True(found);
        Assert.NotNull(retrieved);
        Assert.Equal("Maps/Level.umap", retrieved.VirtualPath);
        Assert.Equal(2, retrieved.CreatedFiles.Count);
    }

    [Fact]
    public void Clear_RemovesAllCachedEntries()
    {
        var key = _cache.ComputeCacheKey("Test/Asset.uasset", 100, "4.21", "v1");
        _cache.Store(key, new CachedAssetResult
        {
            CacheKey = key,
            VirtualPath = "Test/Asset.uasset",
            ClassName = "Texture2D",
            Status = RecoveryStatus.Recovered,
            CreatedFiles = new List<string>(),
            TimestampUtc = DateTime.UtcNow
        });

        _cache.Clear();

        var found = _cache.TryGetCached(key, out _);
        Assert.False(found);
    }
}
