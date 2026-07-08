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
            var pkg = ToEditorPath(key);
            if (pkg == null || !pkg.StartsWith("/Game/", StringComparison.Ordinal)) continue;   // only /Game needs case-canon
            // first writer wins; on-disk files are one case so spellings are consistent here
            _canon.TryAdd(pkg.ToLowerInvariant(), pkg);
        }
        _built = true;
    }

    private static readonly string[] PkgExts = { ".uasset", ".umap", ".uexp", ".ubulk", ".uptnl" };

    /// <summary>Convert a mount-relative virtual/resolved path to the EDITOR package path the editor mounts:
    ///   "Mount/Content/Rel/Name.uasset" -> "/Game/Rel/Name"
    ///   "Engine/Content/Rel"            -> "/Engine/Rel"
    ///   "X/Plugins/.../PluginName/Content/Rel" -> "/PluginName/Rel"
    /// Legacy .pak packages resolve to this mount-relative form (unlike IoStore which is already "/Game/..."); if the
    /// import table keeps it, the editor reports "package root is unknown" and the reference never loads.</summary>
    public static string? ToEditorPath(string virtualKey)
    {
        var k = virtualKey.Replace('\\', '/');
        int ci = k.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (ci < 0) return null;
        var mount = k.Substring(0, ci);
        var rel = k.Substring(ci + "/Content/".Length);
        foreach (var ext in PkgExts)
            if (rel.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { rel = rel[..^ext.Length]; break; }

        string root;
        if (mount.IndexOf("/Plugins/", StringComparison.OrdinalIgnoreCase) >= 0)
            root = "/" + mount.Substring(mount.LastIndexOf('/') + 1) + "/";     // plugin mounts as /<PluginName>/
        else if (mount.Equals("Engine", StringComparison.OrdinalIgnoreCase) || mount.EndsWith("/Engine", StringComparison.OrdinalIgnoreCase))
            root = "/Engine/";
        else
            root = "/Game/";                                                    // project content -> /Game/
        return root + rel;
    }

    /// <summary>Rewrite a name-table entry: mount-relative virtual path -> editor package path (Game/Engine/Plugin),
    /// then case-canonicalize "/Game/..." entries against the on-disk casing.</summary>
    public static string Normalize(string name)
    {
        if (!_built || string.IsNullOrEmpty(name)) return name;
        // Legacy-pak import paths are mount-relative: no leading '/', contain "/Content/". Convert to editor form.
        if (name[0] != '/' && name.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var editor = ToEditorPath(name);
            if (editor != null) name = editor;
        }
        // Case-normalize /Game package entries (no dotted object paths).
        if (name.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) && name.IndexOf('.') < 0
            && _canon.TryGetValue(name.ToLowerInvariant(), out var canon))
            return canon;
        return name;
    }
}
