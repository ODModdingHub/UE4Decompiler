using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Serilog;

namespace UE4Decompiler.Output.Writer;

/// <summary>Outcome of an uncooked write (for the manifest).</summary>
public sealed class UncookedWriteResult
{
    /// <summary>[10.1] "patched" | "skipped" | "ambiguous" | "n/a".</summary>
    public string BCookedPatched { get; init; } = "n/a";
    public int AssetRegistryRecords { get; init; }
}

/// <summary>
/// Writes a single-file, editor-loadable UE 4.21 uncooked <c>.uasset</c> from a CUE4Parse-parsed
/// cooked package. Strategy: the tagged-property byte format is identical between cooked and
/// uncooked, so we rebuild the package framework (summary + name/import/export tables) and copy the
/// real export payloads verbatim — preserving the source's versioning + custom-version container so
/// the editor deserializes them under the same assumptions the cooker used. Cooked-only flags are
/// cleared and payloads are relocated from the split .uexp into one file.
///
/// Layout: [Summary][NameTable][ImportMap][ExportMap][DependsMap][AssetRegistry][ExportData].
///
/// Eligibility is a WHITELIST ([9.1]): only classes whose editor-mode Serialize consumes exactly the
/// cooked SerialSize (pure tagged-property classes + the verified UStruct/UClass/BGC family) are safe
/// to verbatim-copy. Anything else — and anything with separate bulk / native binary serialization —
/// must fall back to a placeholder header, or the loader hits a Fatal SerialSize mismatch on open.
/// </summary>
public sealed class UncookedPackageWriter
{
    private readonly EGame _game;
    public UncookedPackageWriter(EGame game) => _game = game;

    // [4.1] RF_Load = the exact set of object flags the engine persists (ObjectMacros.h):
    // RF_Public|Standalone|Transactional|ClassDefaultObject|ArchetypeObject|DefaultSubObject|
    // TextExportTransient|InheritableComponentTemplate|DuplicateTransient|NonPIEDuplicateTransient.
    private const uint RF_Load = 0x02D4003B;

    // ── [9.1] Eligibility whitelist ──────────────────────────────────────────────────────────────
    // Verified pure tagged-property classes (no native serialize beyond Super::Serialize, no bulk),
    // matched against CUE4Parse export ClassName (no 'U' prefix).
    // CORRECTED: only types whose Serialize is byte-IDENTICAL cooked vs uncooked verbatim-copy safely.
    // Materials/particles/anim/physics were WRONG here — UMaterial::Serialize (and friends) have
    // cooked-conditional / editor-only branches, so cooked bytes ≠ what the editor reads uncooked →
    // tagged-property stream desync ("Invalid boolean" / "Bad name index 0x3F000000=0.5f") → crash.
    // Verified DataTable round-trips; these are the pure-data types in the same shape.
    private static readonly HashSet<string> EligibleClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CurveFloat", "CurveVector", "CurveLinearColor", "CurveTable",
        "DataTable", "StringTable", "UserDefinedEnum", "UserDefinedStruct",
        "SubsurfaceProfile",
    };

    // Classes that must NEVER verbatim-copy: FByteBulkData and/or cook-conditional native serialize.
    // (A package containing ANY of these as an export is excluded — its payloads carry bulk offsets
    //  into .ubulk or editor-only blocks the editor would read past the cooked SerialSize → Fatal.)
    //   Texture2D/TextureCube/Texture2DArray/VolumeTexture -- FByteBulkData (mips)
    //   StaticMesh/SkeletalMesh                            -- FByteBulkData + native serialize
    //   SoundWave/SoundCue                                 -- FByteBulkData (raw audio)
    //   AnimSequence/AnimSequenceBase                      -- CompressedByteStream native serialize
    //   Model/Polys                                        -- native serialize
    //   BodySetup                                          -- FByteBulkData in cooked geometry
    //   Font/FontFace                                      -- FByteBulkData
    //   LandscapeComponent/LandscapeLayerInfoObject        -- native serialize
    // Note: this is asset-class driven. Component SUB-objects (SkeletalMeshComponent, SceneComponent,
    // SCS_Node, …) inside an eligible blueprint are tagged-property-only in 4.21 and ride along safely;
    // a Component that is itself the primary asset simply isn't in the whitelist → placeholder.
    private static readonly HashSet<string> HardExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Texture2D", "TextureCube", "Texture2DArray", "VolumeTexture",
        "StaticMesh", "SkeletalMesh",
        "SoundWave", "SoundCue",
        "AnimSequence", "AnimSequenceBase",
        "Model", "Polys",
        "BodySetup",
        "Font", "FontFace",
        "LandscapeComponent", "LandscapeLayerInfoObject",
    };

    private static bool IsUStructFamily(string className) => className is
        "Class" or "BlueprintGeneratedClass" or "AnimBlueprintGeneratedClass" or "WidgetBlueprintGeneratedClass"
        or "Struct" or "Function" or "Enum" or "ScriptStruct"
        or "Blueprint" or "AnimBlueprint" or "WidgetBlueprint";

    // [9.1] A class is verbatim-safe if it's a pure-data whitelisted type or the UStruct/Blueprint family.
    // (ParticleModule removed — cooked-conditional serialize desyncs like Material did.)
    private static bool IsEligible(string className) =>
        EligibleClasses.Contains(className)
        || IsUStructFamily(className);

    /// <summary>
    /// [9.1] Package is eligible for real uncooked output iff no export is a hard-excluded
    /// (bulk/native) class AND at least one export is a verified-safe asset class.
    /// </summary>
    public static bool IsPackageEligible(Package package)
    {
        // Maps: verbatim-copy is safe (World/Level/Model native data round-trips — proven on connect_spinner),
        // so allow them even though they contain a Model (otherwise HardExcluded). Lets the normal pipeline
        // emit real .umap files instead of placeholders, without a separate --force-write pass.
        if (package.ExportMap.Any(e => e.ClassName is "World")) return true;
        foreach (var e in package.ExportMap)
            if (HardExcludedClasses.Contains(e.ClassName)) return false;
        return package.ExportMap.Any(e => IsEligible(e.ClassName));
    }

    // UClass-derived exports carry the bCooked bool we patch in [10.1].
    private static bool IsClassExport(string className) =>
        className == "Class" || className.EndsWith("BlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase);

    public UncookedWriteResult Write(Package package, byte[] virtualStream, string outputPath)
    {
        var summary = package.Summary;
        var names = package.NameMap;
        var imports = package.ImportMap;
        var exports = package.ExportMap;

        // Slice each export's real payload bytes out of the combined stream.
        var payloads = new byte[exports.Length][];
        for (var i = 0; i < exports.Length; i++)
        {
            var off = (int)exports[i].SerialOffset;
            var size = (int)exports[i].SerialSize;
            payloads[i] = new byte[size];
            Array.Copy(virtualStream, off, payloads[i], 0, size);
        }

        // [10.1] Patch bCooked=false in copied UClass-family payloads (in place; size unchanged).
        var bCookedStatus = PatchBCooked(exports, names, payloads);

        var namesBuf = SerializeNames(names);
        var importsBuf = SerializeImports(imports);
        var dependsBuf = new byte[exports.Length * 4];      // one empty (count=0) dependency array per export
        var (arBuf, arRecords) = SerializeAssetRegistry(exports); // [6.1]

        // Pass 1: measure summary + export-map sizes (independent of the offset values they contain).
        var summarySize = SerializeSummary(summary, exports, names, 0, 0, 0, 0, 0, 0, 0).Length;
        var exportsSize = SerializeExports(exports).Length;

        var nameOffset = summarySize;
        var importOffset = nameOffset + namesBuf.Length;
        var exportOffset = importOffset + importsBuf.Length;
        var dependsOffset = exportOffset + exportsSize;
        var arOffset = dependsOffset + dependsBuf.Length;
        var headerSize = arOffset + arBuf.Length;           // payloads begin here

        // Compute relocated SerialOffsets (absolute within the single file).
        var newSerialOffsets = new int[exports.Length];
        var cursor = headerSize;
        var totalPayload = 0;
        for (var i = 0; i < exports.Length; i++)
        {
            newSerialOffsets[i] = cursor;
            cursor += payloads[i].Length;
            totalPayload += payloads[i].Length;
        }
        var bulkDataStart = headerSize + totalPayload;

        // Pass 2: final buffers with real offsets.
        var exportsBuf = SerializeExports(exports, newSerialOffsets);
        var summaryBuf = SerializeSummary(summary, exports, names,
            headerSize, nameOffset, importOffset, exportOffset, dependsOffset, arOffset, bulkDataStart);

        if (summaryBuf.Length != summarySize)
            throw new InvalidOperationException($"Summary size drift: {summaryBuf.Length} != {summarySize}");
        if (exportsBuf.Length != exportsSize)
            throw new InvalidOperationException($"Export map size drift: {exportsBuf.Length} != {exportsSize}");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using (var fs = File.Create(outputPath))
        {
            fs.Write(summaryBuf); fs.Write(namesBuf); fs.Write(importsBuf);
            fs.Write(exportsBuf); fs.Write(dependsBuf); fs.Write(arBuf);
            for (var i = 0; i < exports.Length; i++) fs.Write(payloads[i]);
            // Trailing PACKAGE_FILE_TAG: LinkerLoad reads the last 4 bytes (TotalSize-4) and rejects
            // the package ("Post Tag is not valid") if absent. SavePackage.cpp writes it at EOF.
            fs.Write(BitConverter.GetBytes(FPackageFileSummary.PACKAGE_FILE_TAG));
        }

        Log.Information("Wrote uncooked .uasset {File} ({Exports} exports, header {Hdr}B, payload {Pay}B, bCooked={BC}, AR={AR})",
            Path.GetFileName(outputPath), exports.Length, headerSize, totalPayload, bCookedStatus, arRecords);

        return new UncookedWriteResult { BCookedPatched = bCookedStatus, AssetRegistryRecords = arRecords };
    }

    /// <summary>
    /// [10.1] Set bCooked=false in UClass-derived payloads. In 4.21 UClass::Serialize writes (after the
    /// tagged-property stream) bDeprecatedForceScriptOrder (int32 bool, 0), FName Dummy=NAME_None
    /// (nameIndex+number), then bCooked (int32 bool, 1 in cooked). We locate the 16-byte signature
    /// [00000000][noneIndex][00000000][01000000] and zero the final int32. Skip (don't corrupt) if the
    /// signature is missing or ambiguous.
    /// </summary>
    private static string PatchBCooked(FObjectExport[] exports, FNameEntrySerialized[] names, byte[][] payloads)
    {
        if (!exports.Any(e => IsClassExport(e.ClassName))) return "n/a";

        var noneIndex = Array.FindIndex(names, n => string.Equals(n.Name, "None", StringComparison.Ordinal));
        if (noneIndex < 0) return "skipped"; // can't build the Dummy=NAME_None signature

        Span<byte> sig = stackalloc byte[16];
        // bDeprecatedForceScriptOrder = 0
        // Dummy FName = (noneIndex, 0)
        BitConverter.TryWriteBytes(sig.Slice(4, 4), noneIndex);
        // bCooked = 1
        sig[12] = 0x01;

        var patched = 0; var ambiguous = false;
        for (var i = 0; i < exports.Length; i++)
        {
            if (!IsClassExport(exports[i].ClassName)) continue;
            var p = payloads[i];
            var matches = FindAll(p, sig);
            if (matches.Count == 1)
            {
                var at = matches[0] + 12;            // the bCooked int32
                p[at] = 0; p[at + 1] = 0; p[at + 2] = 0; p[at + 3] = 0;
                patched++;
            }
            else if (matches.Count > 1)
            {
                ambiguous = true;
                Log.Warning("bCooked signature ambiguous ({N} matches) in export {Name}; skipping patch", matches.Count, exports[i].ObjectName.Text);
            }
        }
        return ambiguous ? "ambiguous" : patched > 0 ? "patched" : "skipped";
    }

    private static List<int> FindAll(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        var hits = new List<int>();
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) hits.Add(i);
        }
        return hits;
    }

    /// <summary>True if any export's payload references a separate .ubulk (legacy guard, kept for callers).</summary>
    public static bool HasSeparateBulk(Package package) => !IsPackageEligible(package);

    /// <summary>
    /// [6.1] Build the in-package asset-registry block in the 4.21 engine format
    /// (UPackage::SaveAssetRegistryData): int32 ObjectCount, then per IsAsset export
    /// { FString ObjectPath (name relative to package), FString ClassName, int32 TagCount=0 }.
    /// </summary>
    private static (byte[] buf, int count) SerializeAssetRegistry(FObjectExport[] exports)
    {
        var assets = exports.Where(e => e.IsAsset).ToList();
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        w.Write(assets.Count);
        foreach (var e in assets)
        {
            w.WriteFString(e.ObjectName.Text);  // path relative to outermost == object name for top-level assets
            w.WriteFString(e.ClassName);
            w.Write(0);                          // Tags map count = 0 (editor recomputes on scan)
        }
        w.Flush();
        return (ms.ToArray(), assets.Count);
    }

    private static byte[] SerializeNames(FNameEntrySerialized[] names)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        foreach (var n in names)
        {
            var s = PackagePathCanon.Normalize(n.Name ?? "None");
            w.WriteFString(s);
            w.Write(FCrc.NonCasePreservingHash(s));
            w.Write(FCrc.CasePreservingHash(s));
        }
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] SerializeImports(FObjectImport[] imports)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        foreach (var imp in imports)
        {
            w.WriteFName(imp.ClassPackage);
            w.WriteFName(imp.ClassName);
            w.Write(imp.OuterIndex?.Index ?? 0);
            w.WriteFName(imp.ObjectName);
            // 4.21: no PackageName (NON_OUTER_PACKAGE_IMPORT) / no ImportOptional.
        }
        w.Flush();
        return ms.ToArray();
    }

    private byte[] SerializeExports(FObjectExport[] exports, int[]? serialOffsets = null)
    {
        const uint cookedMask = unchecked((uint)(0x80000000 /*PKG_FilterEditorOnly*/ | 0x00000200 /*PKG_Cooked*/ | 0x00002000 /*PKG_UnversionedProperties*/));
        var ver = _game.GetVersion();
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);
        for (var i = 0; i < exports.Length; i++)
        {
            var e = exports[i];
            w.Write(e.ClassIndex?.Index ?? 0);
            w.Write(e.SuperIndex?.Index ?? 0);
            if (ver >= EUnrealEngineObjectUE4Version.TemplateIndex_IN_COOKED_EXPORTS)
                w.Write(e.TemplateIndex?.Index ?? 0);
            w.Write(e.OuterIndex?.Index ?? 0);
            w.WriteFName(e.ObjectName);
            w.Write(e.ObjectFlags & RF_Load);          // [4.1] engine masks to RF_Load on save
            if (ver < EUnrealEngineObjectUE4Version.e64BIT_EXPORTMAP_SERIALSIZES)
            { w.Write((int)e.SerialSize); w.Write(serialOffsets?[i] ?? 0); }
            else
            { w.Write(e.SerialSize); w.Write((long)(serialOffsets?[i] ?? 0)); }
            w.WriteBool(e.ForcedExport);               // FArchive bools are int32
            w.WriteBool(e.NotForClient);
            w.WriteBool(e.NotForServer);
            w.WriteGuid(e.PackageGuid);                // present pre-UE5
            w.Write(e.PackageFlags & ~cookedMask);     // strip cooked flags
            if (ver >= EUnrealEngineObjectUE4Version.LOAD_FOR_EDITOR_GAME) w.WriteBool(e.NotAlwaysLoadedForEditorGame);
            if (ver >= EUnrealEngineObjectUE4Version.COOKED_ASSETS_IN_EDITOR_SUPPORT) w.WriteBool(e.IsAsset);
            if (ver >= EUnrealEngineObjectUE4Version.PRELOAD_DEPENDENCIES_IN_COOKED_EXPORTS)
            { w.Write(-1); w.Write(0); w.Write(0); w.Write(0); w.Write(0); } // uncooked: no preload deps
        }
        w.Flush();
        return ms.ToArray();
    }

    private byte[] SerializeSummary(FPackageFileSummary s, FObjectExport[] exports, FNameEntrySerialized[] names,
        int totalHeaderSize, int nameOffset, int importOffset, int exportOffset, int dependsOffset, int arOffset, int bulkDataStart)
    {
        const uint cookedMask = unchecked((uint)(0x80000000 | 0x00000200 | 0x00002000));
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);

        var ver = _game.GetVersion();

        w.Write(FPackageFileSummary.PACKAGE_FILE_TAG);      // 0x9E2A83C1
        w.Write(-7);                                         // LegacyFileVersion (4.21)
        w.Write(864);                                        // LegacyUE3Version (versioned packages write 864)

        // CRITICAL: the editor REFUSES unversioned packages — PackageFileSummary.cpp bails when
        // FileVersionUE4==0 && FileVersionLicenseeUE4==0 and GAllowUnversionedContentInEditor is false
        // (it is, with no ini/CVar override in 4.21). The cooked source was unversioned, but we MUST
        // emit a VERSIONED package or nothing loads in-editor. (Supersedes earlier [1.1]/[1.2] mirroring.)
        w.Write(ver.FileVersionUE4);                        // 517 (4.21) — non-zero => versioned
        w.Write(0);                                         // FileVersionLicenseeUE4 (Epic branch, no licensee)

        // Custom versions (Optimized format at legacy -7): the 4.21 registered object versions at their
        // exact per-game values (CUE4Parse encodes the per-EGame value for each). The unversioned cook's
        // runtime used GetRegistered(); declaring the same set keeps Ar.CustomVer() answers identical so
        // payloads deserialize correctly (an empty/missing version => CustomVer returns -1 => old-format
        // branch => SerialSize mismatch => Fatal).
        var cvs = Build421CustomVersions();
        w.Write(cvs.Count);
        foreach (var cv in cvs) { w.WriteGuid(cv.Key); w.Write(cv.Version); }

        w.Write(totalHeaderSize);
        w.WriteFString(s.PackageName);                       // FolderName ("None")
        w.Write((uint)s.PackageFlags & ~cookedMask);         // strip cooked flags from package

        w.Write(names.Length); w.Write(nameOffset);

        // The remaining optional sections mirror FPackageFileSummary's reader gates exactly, evaluated
        // against the effective (4.21) version, so writer<->reader field order can never drift.

        if (ver >= EUnrealEngineObjectUE4Version.ADDED_PACKAGE_SUMMARY_LOCALIZATION_ID)
            w.WriteFString(string.Empty);                    // LocalizationId (editor-only branch; flags cleared)
        if (ver >= EUnrealEngineObjectUE4Version.SERIALIZE_TEXT_IN_PACKAGES)
        { w.Write(0); w.Write(0); }                          // GatherableTextDataCount / Offset

        w.Write(exports.Length); w.Write(exportOffset);
        w.Write(s.ImportCount); w.Write(importOffset);
        w.Write(dependsOffset);

        if (ver >= EUnrealEngineObjectUE4Version.ADD_STRING_ASSET_REFERENCES_MAP)
        { w.Write(0); w.Write(0); }                          // SoftPackageReferencesCount / Offset
        if (ver >= EUnrealEngineObjectUE4Version.ADDED_SEARCHABLE_NAMES)
            w.Write(0);                                      // SearchableNamesOffset

        w.Write(0);                                          // ThumbnailTableOffset
        w.WriteGuid(s.Guid);                                 // [1.4] preserve source package Guid

        if (ver >= EUnrealEngineObjectUE4Version.ADDED_PACKAGE_OWNER)
        {
            w.WriteGuid(s.Guid);                             // PersistentGuid (not reached at 4.21)
            if (ver < EUnrealEngineObjectUE4Version.NON_OUTER_PACKAGE_IMPORT)
                w.WriteGuid(default);                        // OwnerPersistentGuid (this version range only)
        }

        // Generations: one entry describing this save.
        w.Write(1);
        w.Write(exports.Length); w.Write(names.Length);

        // SavedBy / CompatibleWith engine version.
        if (ver >= EUnrealEngineObjectUE4Version.ENGINE_VERSION_OBJECT)
            w.WriteEngineVersion(4, 21, 2, 0, "++UE4+Release-4.21");
        else
            w.Write(0);                                      // legacy engine changelist
        if (ver >= EUnrealEngineObjectUE4Version.PACKAGE_SUMMARY_HAS_COMPATIBLE_ENGINE_VERSION)
            w.WriteEngineVersion(4, 21, 2, 0, "++UE4+Release-4.21");

        w.Write((uint)0);                                    // CompressionFlags
        w.Write(0);                                          // CompressedChunks count
        w.Write(s.PackageSource);                            // PackageSource
        w.Write(0);                                          // AdditionalPackagesToCook count
        // NumTextureAllocations only when legacy > -7; we use -7, so omit.
        w.Write(arOffset);                                   // [1.3] AssetRegistryDataOffset (unconditional in 4.21)
        w.Write((long)bulkDataStart);                        // [1.3] BulkDataStartOffset int64 (unconditional in 4.21)
        if (ver >= EUnrealEngineObjectUE4Version.WORLD_LEVEL_INFO)
            w.Write(0);                                      // WorldTileInfoDataOffset
        if (ver >= EUnrealEngineObjectUE4Version.CHANGED_CHUNKID_TO_BE_AN_ARRAY_OF_CHUNKIDS)
            w.Write(0);                                      // ChunkIds count
        else if (ver >= EUnrealEngineObjectUE4Version.ADDED_CHUNKID_TO_ASSETDATA_AND_UPACKAGE)
            w.Write(-1);                                     // single ChunkId (none)
        if (ver >= EUnrealEngineObjectUE4Version.PRELOAD_DEPENDENCIES_IN_COOKED_EXPORTS)
        { w.Write(-1); w.Write(0); }                         // [7.1] PreloadDependencyCount=-1 (uncooked sentinel) / Offset

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// The vanilla-4.21 registered custom-version set with EXACT values, captured as ground truth from
    /// the editor's own startup LogDevObjectVersion dump (22 versions) plus a saved .umap
    /// (FReleaseObjectVersion=20). The unversioned cook assumed FCustomVersionContainer::GetRegistered(),
    /// so declaring this full set at these exact values makes Ar.CustomVer() answers identical and the
    /// copied payloads deserialize correctly. GUIDs are the engine-source GUID(A,B,C,D) literals (UE's
    /// hyphenated display is NOT A-B-C-D). FDestructionObjectVersion is intentionally omitted — it is not
    /// in this build's registered set; declaring an unregistered GUID risks "unrecognizable data".
    /// </summary>
    internal static List<FCustomVersion> Build421CustomVersions()
    {
        static FCustomVersion CV(uint a, uint b, uint c, uint d, int v) => new(new FGuid(a, b, c, d), v);
        return new List<FCustomVersion>
        {
            CV(0xB0D832E4, 0x1F894F0D, 0xACCF7EB7, 0x36FD4AA2, 10), // Dev-Blueprints
            CV(0xE1C64328, 0xA22C4D53, 0xA36C8E86, 0x6417BD8C,  0), // Dev-Build
            CV(0x375EC13C, 0x06E448FB, 0xB50084F0, 0x262A717E,  2), // Dev-Core
            CV(0xE4B068ED, 0xF49442E9, 0xA231DA0B, 0x2E46BB41, 26), // Dev-Editor
            CV(0xCFFC743F, 0x43B04480, 0x939114DF, 0x171D2073, 34), // Dev-Framework
            CV(0xB02B49B5, 0xBB2044E9, 0xA30432B7, 0x52E40360,  2), // Dev-Mobile
            CV(0xA4E4105C, 0x59A149B5, 0xA7C540C4, 0x547EDFEE,  0), // Dev-Networking
            CV(0x39C831C9, 0x5AE647DC, 0x9A449C17, 0x3E1C8E7C,  0), // Dev-Online
            CV(0x78F01B33, 0xEBEA4F98, 0xB9B484EA, 0xCCB95AA2,  0), // Dev-Physics
            CV(0x6631380F, 0x2D4D43E0, 0x8009CF27, 0x6956A95A,  0), // Dev-Platform
            CV(0x12F88B9F, 0x88754AFC, 0xA67CD90C, 0x383ABD29, 27), // Dev-Rendering
            CV(0x7B5AE74C, 0xD2704C10, 0xA9585798, 0x0B212A5A,  9), // Dev-Sequencer
            CV(0xD7296918, 0x1DD64BDD, 0x9DE264A8, 0x3CC13884,  2), // Dev-VR
            CV(0xC2A15278, 0xBFE74AFE, 0x6C1790FF, 0x531DF755,  1), // Dev-LoadTimes
            CV(0x6EACA3D4, 0x40EC4CC1, 0xB7868BED, 0x09428FC5,  3), // Private-Geometry
            CV(0x29E575DD, 0xE0A34627, 0x9D10D276, 0x232CDCEA, 17), // Dev-AnimPhys
            CV(0xAF43A65D, 0x7FD34947, 0x98733E8E, 0xD9C1BB05,  2), // Dev-Anim
            CV(0x6B266CEC, 0x1EC74B8F, 0xA30BE4D9, 0x0942FC07,  1), // Dev-ReflectionCapture
            CV(0x0DF73D61, 0xA23F47EA, 0xB72789E9, 0x0C41499A,  1), // Dev-Automation
            CV(0x601D1886, 0xAC644F84, 0xAA16D3DE, 0x0DEAC7D6, 17), // FortniteMain
            CV(0x9DFFBCD6, 0x494F0158, 0xE2211282, 0x3C92A888,  4), // Dev-Enterprise
            CV(0xF2AED0AC, 0x9AFE416F, 0x8664AA7F, 0xFA26D6FC,  1), // Dev-Niagara
            CV(0x9C54D522, 0xA8264FBE, 0x94210746, 0x61B482D0, 20), // Release
        };
    }
}
