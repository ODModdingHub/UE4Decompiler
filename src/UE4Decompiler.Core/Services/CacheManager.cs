using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Serilog;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;

namespace UE4Decompiler.Core.Services;

/// <summary>
/// Persistent asset recovery cache for accelerated incremental recovery (Phase 37 &amp; 67).
/// </summary>
public sealed class CacheManager : ICacheManager
{
    private readonly string _cacheDirectory;
    private readonly Dictionary<string, CachedAssetResult> _memoryIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public CacheManager(string? cacheDir = null)
    {
        _cacheDirectory = cacheDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ue4decompiler", "cache");
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            LoadIndex();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to initialize cache directory {Dir}", _cacheDirectory);
        }
    }

    private string IndexFilePath => Path.Combine(_cacheDirectory, "cache-index.json");

    private void LoadIndex()
    {
        lock (_lock)
        {
            if (!File.Exists(IndexFilePath)) return;
            try
            {
                var json = File.ReadAllText(IndexFilePath);
                var items = JsonConvert.DeserializeObject<List<CachedAssetResult>>(json);
                if (items != null)
                {
                    foreach (var item in items)
                        _memoryIndex[item.CacheKey] = item;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not load cache index; cache will start fresh");
            }
        }
    }

    private void SaveIndex()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonConvert.SerializeObject(_memoryIndex.Values.ToList(), Formatting.Indented);
                File.WriteAllText(IndexFilePath, json);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to save cache index");
            }
        }
    }

    public string ComputeCacheKey(string assetPath, long size, string engineVersion, string optionsFingerprint)
    {
        using var sha = SHA256.Create();
        var raw = $"{assetPath}|{size}|{engineVersion}|{optionsFingerprint}|v2.0";
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public bool TryGetCached(string cacheKey, out CachedAssetResult? cached)
    {
        lock (_lock)
        {
            return _memoryIndex.TryGetValue(cacheKey, out cached);
        }
    }

    public void Store(string cacheKey, CachedAssetResult result)
    {
        lock (_lock)
        {
            _memoryIndex[cacheKey] = result;
            SaveIndex();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _memoryIndex.Clear();
            if (File.Exists(IndexFilePath))
            {
                try { File.Delete(IndexFilePath); } catch { }
            }
        }
    }
}
