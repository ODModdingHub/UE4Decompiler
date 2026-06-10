using System.Text.RegularExpressions;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using Serilog;

namespace UE4Decompiler.Core;

/// <summary>
/// Mounts a PAK / IoStore container (or a directory of them) via CUE4Parse's
/// <see cref="DefaultFileProvider"/>, submits the AES key, and enumerates game files.
/// Oodle / Zlib / LZ4 decompression is handled transparently by CUE4Parse.
/// </summary>
public sealed class PakExtractor : IDisposable
{
    public DefaultFileProvider Provider { get; }

    public PakExtractor(string input, EGame game, FAesKey? aesKey, bool readScriptData = false)
    {
        // Accept either a directory of paks or a single .pak/.utoc; mount the containing directory.
        string directory;
        if (Directory.Exists(input))
            directory = input;
        else if (File.Exists(input))
            directory = Path.GetDirectoryName(Path.GetFullPath(input))
                        ?? throw new DirectoryNotFoundException($"Cannot resolve directory of '{input}'.");
        else
            throw new FileNotFoundException($"Input path does not exist: {input}");

        // UE5 (and some UE4) containers are Oodle-compressed; load the native oo2core dll (downloaded next to
        // the exe on first run). Without this every compressed read throws "Oodle ... not initialized".
        try
        {
            string? oodlePath = null;
            CUE4Parse.Compression.OodleHelper.DownloadOodleDll(ref oodlePath);   // no-op if already present
            CUE4Parse.Compression.OodleHelper.Initialize(oodlePath);
            Log.Information("Oodle initialized ({Path})", oodlePath ?? "default");
        }
        catch (Exception ex) { Log.Warning("Oodle init failed ({M}); Oodle-compressed entries won't read", ex.Message); }

        Log.Information("Mounting containers from {Dir} (engine {Game})", directory, game);
        Provider = new DefaultFileProvider(directory, SearchOption.TopDirectoryOnly, isCaseInsensitive: true,
            new VersionContainer(game));
        // Kismet bytecode is only deserialized when this is enabled (defaults off for perf).
        // Required for any Blueprint graph/bytecode recovery.
        Provider.ReadScriptData = readScriptData;
        Provider.Initialize();

        // Mounting is triggered by SubmitKeys (keyed on each reader's EncryptionKeyGuid). Unencrypted
        // containers carry the zero GUID, so we ALWAYS submit a key for the empty GUID — using the
        // real key if given, otherwise a zero key (ignored for unencrypted readers). Encrypted readers
        // with a wrong/missing key throw InvalidAesKeyException internally and simply stay unmounted.
        var mainKey = aesKey ?? new FAesKey(new byte[32]);
        var submitted = Provider.SubmitKey(new FGuid(), mainKey);
        Log.Information("Submitted main key for empty GUID, {Count} container(s) mounted", submitted);

        // Additional dynamic-key GUIDs (rare for plain PAK games) reuse the supplied key if present.
        if (aesKey is not null)
        {
            var pending = Provider.RequiredKeys.Where(g => g != new FGuid()).ToList();
            if (pending.Count > 0)
            {
                var more = Provider.SubmitKeys(pending.Select(g => new KeyValuePair<FGuid, FAesKey>(g, aesKey)));
                Log.Information("Submitted key for {N} additional dynamic GUID(s), {More} mounted", pending.Count, more);
            }
        }

        if (Provider.MountedVfs.Count == 0 && Provider.UnloadedVfs.Count > 0)
            Log.Warning("No containers mounted — they are likely AES-encrypted. Supply --aes-key.");

        Provider.PostMount();
        Log.Information("Mounted {Mounted} container(s); {Files:N0} files visible",
            Provider.MountedVfs.Count, Provider.Files.Count);
    }

    /// <summary>
    /// Enumerate distinct game packages (.uasset/.umap), optionally filtered by a glob like
    /// "Characters/**" matched against the virtual path. Payload-only entries (.ubulk/.uexp/.uptnl)
    /// are excluded — CUE4Parse resolves them automatically when the package is loaded.
    /// </summary>
    public IEnumerable<GameFile> EnumeratePackages(string? filterGlob)
    {
        Regex? filter = filterGlob is null ? null : GlobToRegex(filterGlob);

        foreach (var file in Provider.Files.Values)
        {
            if (!file.IsUePackage) continue;             // .uasset / .umap only
            if (file.Extension is "uexp" or "ubulk" or "uptnl") continue; // safety: payload siblings
            if (filter is not null && !filter.IsMatch(file.Path)) continue;
            yield return file;
        }
    }

    /// <summary>Convert a UE-style glob ("Characters/**", "*.uasset") to an anchored regex.</summary>
    public static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '.': case '(': case ')': case '+': case '|': case '^':
                case '$': case '{': case '}': case '[': case ']': case '\\':
                    sb.Append('\\').Append(c); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public void Dispose() => Provider.Dispose();
}
