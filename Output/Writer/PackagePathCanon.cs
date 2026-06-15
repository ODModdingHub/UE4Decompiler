using System;
using System.Collections.Generic;

namespace UE4Decompiler.Output.Writer;

/// <summary>
/// Canonical-case map for package paths, built once from the mounted provider's virtual file list.
///
/// WHY: the cooked game references the same package with inconsistent folder case
/// (e.g. "/Game/Effects/TackleBall" vs "/Game/Effects/Tackleball"). FPackageId is CityHash64 of the
/// LOWERCASED path, but the engine's registration assert (PackageId.cpp) compares the stored name
/// CASE-SENSITIVELY -> two spellings, same id, different name => fatal "FPackageId collision".
///
/// The fix is applied at NAME-TABLE serialization time (UncookedPackageWriter / SynthPackageWriter):
/// every "/Game/..." name is rewritten to the on-disk canonical case before its FName hashes are
/// computed. Because the hashes are produced fresh from the corrected string, this is lossless and
/// cannot corrupt the package (unlike the old in-place --fix-id-collisions byte patcher, which wrote a
/// 4-byte hash into export-data soft-ref FStrings and shredded the name table).
/// </summary>
public static class PackagePathCanon
{
    // lowercased package path  ->  canonical-case package path
    private static readonly Dictionary<string, string> _canon = new(StringComparer.Ordinal);
    private static bool _built;

    /// <summary>Build from the provider's virtual file keys (e.g. "A2/Content/A2/Maps/Foo.uasset").</summary>
    public static void Build(IEnumerable<string> virtualKeys)
    {
        _canon.Clear();
        foreach (var key in virtualKeys)
        {
            var pkg = ToPackagePath(key);
            if (pkg == null) continue;
            // first writer wins; on-disk files are one case so spellings are consistent here
            _canon.TryAdd(pkg.ToLowerInvariant(), pkg);
        }
        _built = true;
    }

    /// <summary>"Mount/Content/Rel/Name.uasset" -> "/Game/Rel/Name" (or "/Mount/Rel/Name" for plugins).</summary>
    private static string? ToPackagePath(string virtualKey)
    {
        var k = virtualKey.Replace('\\', '/');
        int ci = k.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (ci < 0) return null;
        var mount = k.Substring(0, ci);
        var rel = k.Substring(ci + "/Content/".Length);
        int dot = rel.LastIndexOf('.');
        if (dot >= 0) rel = rel.Substring(0, dot);
        // game content mounts to /Game/; anything else mounts to /<Mount>/
        bool isGame = mount.Equals("A2", StringComparison.OrdinalIgnoreCase)
                   || mount.IndexOf("/Game", StringComparison.OrdinalIgnoreCase) >= 0
                   || mount.IndexOf("Game", StringComparison.OrdinalIgnoreCase) == mount.Length - 4;
        // We only need /Game canonicalization for the collision; map every game-content key to /Game/Rel.
        return "/Game/" + rel;
    }

    /// <summary>Rewrite a name-table entry to canonical case if it is a known "/Game/..." package path.</summary>
    public static string Normalize(string name)
    {
        if (!_built || string.IsNullOrEmpty(name)) return name;
        if (name.Length < 7 || name[0] != '/') return name;
        if (!name.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) return name;
        // name-table package entries carry no '.'; a dotted string here would be an object path we leave alone
        if (name.IndexOf('.') >= 0) return name;
        return _canon.TryGetValue(name.ToLowerInvariant(), out var canon) ? canon : name;
    }
}
