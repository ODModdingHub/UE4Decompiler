using CommandLine;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using Serilog;
using Serilog.Events;
using Spectre.Console;
using UE4Decompiler.Core;
using UE4Decompiler.Output;
using UE4Decompiler.Utils;

namespace UE4Decompiler;

public static class Program
{
    public sealed class Options
    {
        [Option("input", HelpText = "Path to a .pak/.utoc or a directory of containers.")]
        public string Input { get; set; } = "";

        [Option("output", HelpText = "Output project root directory.")]
        public string Output { get; set; } = "";

        [Option("version", HelpText = "UE version hint, e.g. 4.27 or 5.1 (auto-detected if omitted).")]
        public string? Version { get; set; }

        [Option("usmap", HelpText = "Path to a .usmap mappings file — REQUIRED for UE5 unversioned packages (PKG_UnversionedProperties). Dump one from the running game with UE4SS/Dumper-7.")]
        public string? Usmap { get; set; }

        [Option("aes-key", HelpText = "AES-256 key as hex (0x...); omit if unencrypted.")]
        public string? AesKey { get; set; }

        [Option("filter", HelpText = "Glob to extract a subset, e.g. \"Characters/**\".")]
        public string? Filter { get; set; }

        [Option("skip-blueprints", HelpText = "Emit blueprint stubs instead of attempting graph reconstruction.")]
        public bool SkipBlueprints { get; set; }

        [Option("dangerously-dump-bpgraph", HelpText = "Reconstruct EVERY blueprint by cloning its cooked guts + appending a graph. Shows full data but the BP editor CRASHES opening complex/widget BPs (cooked-only internals). Default only reconstructs simple, open-safe BPs.")]
        public bool DangerouslyDumpBpGraph { get; set; }

        [Option("full-recovery", HelpText = "Emit full Kismet bytecode + anim-graph ordering into JSON for the editor re-import commandlet.")]
        public bool FullRecovery { get; set; }

        [Option("force-write", HelpText = "Dev: bypass the eligibility whitelist and emit a real uncooked package for every asset (for testing risky types like maps).")]
        public bool ForceWrite { get; set; }

        [Option("emit-stubs", HelpText = "Generate compilable C++ stub modules for referenced game-native classes so assets parented to them load.")]
        public bool EmitStubs { get; set; }

        [Option("no-media-export", HelpText = "Skip the slow texture->PNG / mesh->glb / sound exports; still writes editor-loadable .uasset packages. Much faster for a full dump.")]
        public bool NoMediaExport { get; set; }

        [Option("sdk-dump", HelpText = "Path to an SDK dump dir (Dumper '// CLASS:' headers) to refine stub class prefixes/bases.")]
        public string? SdkDump { get; set; }

        [Option("dry-run", HelpText = "List what would be extracted without writing anything.")]
        public bool DryRun { get; set; }

        [Option("report", HelpText = "Write a JSON manifest of all assets + reconstruction fidelity.")]
        public bool Report { get; set; }

        [Option("verbose", HelpText = "Verbose (debug) logging.")]
        public bool Verbose { get; set; }

        [Option("validate-write", HelpText = "Dev: write one package's uncooked .uasset and re-parse it to diff (round-trip test).")]
        public string? ValidateWrite { get; set; }

        [Option("dump-vpath", HelpText = "Dev: load a mounted package by virtual-path substring and dump its exports/properties (works for UE5/Zen via the provider, unlike --dump-package).")]
        public string? DumpVPath { get; set; }

        [Option("list-files", HelpText = "Dev: list mounted virtual paths containing this substring, then exit.")]
        public string? ListFiles { get; set; }

        [Option("export-texture-pngs", HelpText = "Decode every UTexture2D to a PNG at <dir>/<game-relative-path>.png (+ .json sidecar with srgb/normal), then exit. Feed <dir> to the ReimportTextures.py editor script. Use --filter to scope.")]
        public string? ExportTexturePngs { get; set; }

        [Option("dump-mesh-uv", HelpText = "Dev: load a static mesh by vpath substring, TryConvert it, and print LOD0 vert count + UV range + first verts' Position/UV (to check the UV read).")]
        public string? DumpMeshUv { get; set; }

        [Option("find-hidden-actors", HelpText = "Dev: scan mounted .umap files and report actors hidden/invisible in the cooked source (bHidden / bVisible=false / bHiddenInGame). Use --filter to scope.")]
        public bool FindHiddenActors { get; set; }

        [Option("dump-actor-tree", HelpText = "Dev: load one .umap (by vpath substring) and print each PersistentLevel actor with its child components, their class/mesh/transform/material+visibility overrides.")]
        public string? DumpActorTree { get; set; }

        [Option("dump-built-data", HelpText = "Dev: load one .umap + its _BuiltData registry; report MeshBuildData entries (GUID -> lightmap textures) and how many of the map's static-mesh components have a MapBuildDataId that matches a registry key.")]
        public string? DumpBuiltData { get; set; }

        [Option("test-builtdata", HelpText = "Dev: round-trip the MapBuildDataRegistry serializer — load a _BuiltData by vpath substr, re-serialize its registry into a UE5 package, re-read via CUE4Parse, and compare MeshBuildData.")]
        public string? TestBuiltData { get; set; }

        [Option("probe-tex-bulk", HelpText = "Dev: load a LOCAL editor texture/_BuiltData .uasset (--version) and print the first LightMap/Texture2D's FEditorBulkData (Flags/PayloadSize/Offset + FCompressedBuffer header) and package-trailer presence.")]
        public string? ProbeTexBulk { get; set; }

        [Option("dump-shader", HelpText = "Dev: load a material by vpath substr WITH shader maps and dump the FUniformExpressionSet (scalar/vector/texture params + preshaders) + shader platform/code info — to see what the compiled material exposes.")]
        public string? DumpShader { get; set; }

        [Option("find-id-collisions", HelpText = "Dev: scan a Content dir, compute FPackageId per .uasset, and report paths that share an id (the FPackageId-collision crash). No pak mount.")]
        public string? FindIdCollisions { get; set; }

        [Option("fix-id-collisions", HelpText = "Fix FPackageId case-collisions in a Content dir IN PLACE: rewrite every /Game reference string to the canonical on-disk case (same length) + refresh name-table case hashes. No pak mount, no re-dump.")]
        public string? FixIdCollisions { get; set; }

        [Option("extract-file", HelpText = "Dev: extract one package (umap/uasset + uexp combined) by virtual-path substring to --output.")]
        public string? ExtractFile { get; set; }

        [Option("gen-mesh-real", HelpText = "Tier2 mesh: build a REAL-geometry editor StaticMesh from a cooked mesh (by virtual-path substring). Needs --cube. Writes to --output.")]
        public string? GenMeshReal { get; set; }

        [Option("decode", HelpText = "Dev: raw-decode a local .uasset/.umap summary+imports and exit (no pak mount).")]
        public string? Decode { get; set; }

        [Option("dump-package", HelpText = "Dev: parse a local .uasset via CUE4Parse and dump every export's properties (for reversing editor graph format).")]
        public string? DumpPackage { get; set; }

        [Option("compare-meshes", HelpText = "Dev: compare two local editor StaticMesh .uasset files. Format: bad.uasset|rebuilt.uasset")]
        public string? CompareMeshes { get; set; }

        [Option("reemit", HelpText = "Dev: parse a local editor .uasset and re-emit it via UncookedPackageWriter to --output (round-trip test for editor-graph packages).")]
        public string? Reemit { get; set; }

        [Option("build-graph", HelpText = "Tier2: build an editor-openable blueprint (UBlueprint+EventGraph+nodes) from a cooked BP .uasset, to --output.")]
        public string? BuildGraph { get; set; }

        [Option("hex-export", HelpText = "Dev: hex-dump each export's native payload from a local .uasset.")]
        public string? HexExport { get; set; }

        [Option("clone-bp", HelpText = "Dev: clone an editor .uasset verbatim via SynthPackageWriter and byte-diff vs original.")]
        public string? CloneBp { get; set; }

        [Option("inject-event", HelpText = "Tier2: clone an editor BP and inject a synthesized K2Node_Event into its EventGraph, to --output.")]
        public string? InjectEvent { get; set; }

        [Option("walk-bytecode", HelpText = "Dev: walk every UFunction's Kismet bytecode in a cooked BP and print the opcode IR.")]
        public string? WalkBytecode { get; set; }

        [Option("probe-level", HelpText = "Dev: print a map's PersistentLevel native int32s (Actors array) after the tagged props.")]
        public string? ProbeLevel { get; set; }

        [Option("inject-call", HelpText = "Tier2: inject a wired CallFunction(PrintString) off the template's BeginPlay. Value = editor BP path.")]
        public string? InjectCall { get; set; }

        [Option("reconstruct", HelpText = "Tier2: reconstruct a cooked BP's BeginPlay graph onto a template. Value = cooked BP path.")]
        public string? Reconstruct { get; set; }

        [Option("place-actors", HelpText = "Tier2: synthesize a cooked map's no-mesh actors onto a template map. Value = cooked map path.")]
        public string? PlaceActors { get; set; }

        [Option("cube", HelpText = "Engine Cube StaticMesh path, cloned as a loadable placeholder for each referenced mesh.")]
        public string? Cube { get; set; }

        [Option("bp-template", HelpText = "Empty 4.21 editor Blueprint (.uasset) reskinned per cooked BP so blueprints show + open in the content browser (works for UE5/Zen).")]
        public string? BpTemplate { get; set; }

        [Option("bp-recover-calls", HelpText = "Experimental: recover cooked Blueprint bytecode calls as K2 CallFunction graph nodes. Off by default because malformed refs can destabilize editor load.")]
        public bool BpRecoverCalls { get; set; }

        [Option("emit-stub-methods", HelpText = "Experimental: emit recovered name-only UFUNCTION methods on generated game-native C++ stubs. Off by default; use with --bp-recover-calls when testing call binding.")]
        public bool EmitStubMethods { get; set; }

        [Option("content-root", HelpText = "Project Content dir; placeholder meshes are written here at their /Game paths.")]
        public string? ContentRoot { get; set; }

        [Option("template", HelpText = "Loadable editor BP template path for --reconstruct.")]
        public string? Template { get; set; }

        [Option("gen-mesh-all", HelpText = "Generate real meshes for every loadable StaticMesh.")]
public bool GenMeshAll { get; set; }

        [Option("tex-max", Default = 1024, HelpText = "Max texture source dimension; decodes the largest mip <= this (default 1024). Smaller = much faster dump + smaller assets.")]
        public int TexMax { get; set; }

        [Option("gen-tex", HelpText = "Dev: build one editor texture from the first pak texture whose path contains this substring. Writes to --output.")]
        public string? GenTex { get; set; }

        [Option("gen-mat", HelpText = "Dev: clone the material template (--template) pointing at a texture (--reskin-path = /Game tex pkg, --reskin-name = tex name). Writes to --output.")]
        public string? GenMat { get; set; }

        [Option("call-message", Default = "reconstructed", HelpText = "PrintString message for --inject-call.")]
        public string? CallMessage { get; set; }

        [Option("reskin", HelpText = "Tier2: reskin a loadable editor BP template into a target name. Value = template path.")]
        public string? Reskin { get; set; }

        [Option("reskin-name", Default = "BP_HandProxyExample", HelpText = "Target short name for --reskin.")]
        public string? ReskinName { get; set; }

        [Option("reskin-path", Default = "/Game/Maps/BP_HandProxyExample", HelpText = "Target package path for --reskin.")]
        public string? ReskinPath { get; set; }

        [Option("event-name", Default = "ReceiveEndPlay", HelpText = "Event name for --inject-event (override event on the parent class).")]
        public string? EventName { get; set; }
    }

    public static int Main(string[] args)
    {
        // Disable the built-in --version verb so the spec's --version (UE version hint) is usable.
        using var parser = new Parser(with =>
        {
            with.AutoVersion = false;
            with.HelpWriter = Console.Error;
            with.CaseInsensitiveEnumValues = true;
        });
        return parser.ParseArguments<Options>(args).MapResult(Run, _ => 1);
    }

    private static int Run(Options o)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(o.Verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.Console(outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        if (!string.IsNullOrWhiteSpace(o.Decode))
        {
            Log.Information("Decoding {File}", o.Decode);
            Output.Writer.WriterSelfTest.RawDumpImports(File.ReadAllBytes(o.Decode));
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.FindIdCollisions))
        {
            // Scan an output Content dir, compute FPackageId (CityHash64 of the lowercased /Game path) per .uasset,
            // and report ids shared by >1 distinct path — the exact pairs that trip UE5's "FPackageId collision" assert.
            var contentDir = o.FindIdCollisions.TrimEnd('\\', '/');
            var byId = new Dictionary<ulong, HashSet<string>>();
            var enc = System.Text.Encoding.Latin1;
            var rx = new System.Text.RegularExpressions.Regex(@"/Game/[A-Za-z0-9_/.]+");
            static string PkgOf(string s) { var d = s.IndexOf('.'); return d >= 0 ? s[..d] : s; }  // strip .Object suffix
            void AddPath(string p)
            {
                p = PkgOf(p);
                var id = CUE4Parse.UE4.IO.Objects.FPackageId.FromName(p).id;
                if (!byId.TryGetValue(id, out var set)) byId[id] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(p);
            }
            int files = 0;
            foreach (var file in Directory.EnumerateFiles(contentDir, "*.u*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) continue;
                files++;
                var rel = file[(contentDir.Length + 1)..].Replace('\\', '/');
                if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) rel = rel[..^7];
                else if (rel.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) rel = rel[..^5];
                AddPath("/Game/" + rel);                                   // the asset's own (disk-derived) package name
                try
                {
                    foreach (System.Text.RegularExpressions.Match m in rx.Matches(enc.GetString(File.ReadAllBytes(file))))
                        AddPath(m.Value);                                   // every /Game reference inside (full file)
                }
                catch { }
            }
            int collisions = 0;
            foreach (var kv in byId)
            {
                if (kv.Value.Count < 2) continue;                          // same id, >1 distinct spelling = the crash
                collisions++;
                Log.Warning("FPackageId {Id} <- {N} spellings:", kv.Key, kv.Value.Count);
                foreach (var p in kv.Value) Console.WriteLine("    " + p);
            }
            Log.Information("Scanned {F} .uasset under {Dir}: {C} colliding id(s)", files, contentDir, collisions);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.FixIdCollisions))
        {
            var contentDir = o.FixIdCollisions.TrimEnd('\\', '/');
            var enc = System.Text.Encoding.Latin1;
            var rx = new System.Text.RegularExpressions.Regex(@"/Game/[A-Za-z0-9_/.]+");
            bool IsPkg(string f) => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);
            string RelPkg(string file)
            {
                var rel = file[(contentDir.Length + 1)..].Replace('\\', '/');
                if (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) rel = rel[..^7];
                else if (rel.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) rel = rel[..^5];
                return "/Game/" + rel;
            }
            // Canonical case = the on-disk package path (what the editor mounts each file as) — covers .uasset AND .umap.
            var canon = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(contentDir, "*.u*", SearchOption.AllDirectories))
                if (IsPkg(file)) { var gp = RelPkg(file); canon[gp.ToLowerInvariant()] = gp; }
            int filesChanged = 0, refsFixed = 0;
            foreach (var file in Directory.EnumerateFiles(contentDir, "*.u*", SearchOption.AllDirectories))
            {
                if (!IsPkg(file)) continue;
                byte[] bytes;
                try { bytes = File.ReadAllBytes(file); } catch { continue; }
                bool dirty = false;
                foreach (System.Text.RegularExpressions.Match m in rx.Matches(enc.GetString(bytes)))
                {
                    var s = m.Value;
                    var dot = s.IndexOf('.');
                    var pkg = dot >= 0 ? s[..dot] : s;                       // /Game/Pkg.Object -> normalize just /Game/Pkg
                    if (!canon.TryGetValue(pkg.ToLowerInvariant(), out var cpkg) || cpkg == pkg || cpkg.Length != pkg.Length) continue;
                    var start = m.Index;
                    for (var i = 0; i < cpkg.Length; i++) bytes[start + i] = (byte)cpkg[i];   // same-length case overwrite of the package part
                    // Name-table FString entry? ([int32 len=chars+1][chars][\0][u32 nonCaseHash][u32 caseHash]) -> refresh
                    // the case-preserving hash over the FULL (modified) string. Case-insensitive hash is unchanged.
                    if (start >= 4 && BitConverter.ToInt32(bytes, start - 4) == s.Length + 1)
                    {
                        var full = dot >= 0 ? cpkg + s[dot..] : cpkg;
                        var caseHashPos = start + s.Length + 1 + 4;
                        if (caseHashPos + 4 <= bytes.Length)
                            BitConverter.GetBytes(Output.Writer.FCrc.CasePreservingHash(full)).CopyTo(bytes, caseHashPos);
                    }
                    dirty = true; refsFixed++;
                }
                if (dirty) { File.WriteAllBytes(file, bytes); filesChanged++; }
            }
            Log.Information("Fixed {R} case-mismatched reference(s) across {F} file(s) in {Dir}", refsFixed, filesChanged, contentDir);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.DumpPackage))
        {
            // Honors --version (e.g. 5.1) so UE5 editor ground-truth assets dump correctly; defaults to 4.21.
            Output.Writer.WriterSelfTest.DumpPackage(o.DumpPackage,
                VersionDetector.FromHint(o.Version) ?? EGame.GAME_UE4_21);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.ProbeTexBulk))
        {
            var bytes = File.ReadAllBytes(o.ProbeTexBulk);
            var game = VersionDetector.FromHint(o.Version) ?? EGame.GAME_UE5_1;
            var pkg = new CUE4Parse.UE4.Assets.Package(
                new CUE4Parse.UE4.Readers.FByteArchive("probe", bytes, new CUE4Parse.UE4.Versions.VersionContainer(game)),
                (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null,
                (CUE4Parse.FileProvider.IFileProvider?)null, false);
            Console.WriteLine($"Trailer present: {pkg.Trailer != null};  DataResourceMap: {(pkg.DataResourceMap?.Length.ToString() ?? "null")}");
            int shown = 0;
            for (var i = 0; i < pkg.ExportsLazy.Length && shown < 4; i++)
            {
                CUE4Parse.UE4.Assets.Exports.Texture.UTexture? tex = null;
                try { tex = pkg.ExportsLazy[i].Value as CUE4Parse.UE4.Assets.Exports.Texture.UTexture; } catch (Exception ex) { Console.WriteLine($"  exp[{i}] parse err: {ex.Message}"); continue; }
                if (tex?.EditorData is not { } eb) continue;
                shown++;
                Console.WriteLine($"  {tex.Name} ({tex.ExportType}): EditorData Flags={eb.Flags} PayloadSize={eb.PayloadSize} OffsetInFile={eb.OffsetInFile}");
                Console.WriteLine($"      FCompressedBuffer: Method={eb.Payload?.Header.Method} Compressor={eb.Payload?.Header.Compressor} BlockCount={eb.Payload?.Header.BlockCount} RawSize={eb.Payload?.Header.TotalRawSize} CompSize={eb.Payload?.Header.TotalCompressedSize}");
            }
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.CompareMeshes))
        {
            var parts = o.CompareMeshes.Split('|', 2);
            if (parts.Length != 2)
            {
                Log.Error("--compare-meshes expects bad.uasset|rebuilt.uasset");
                return 1;
            }
            Output.Writer.WriterSelfTest.CompareStaticMeshes(parts[0], parts[1],
                VersionDetector.FromHint(o.Version) ?? EGame.GAME_UE4_21);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.GenMat))
        {
            // --gen-mat <materialShortName>  --reskin-path <texture /Game pkg path>  --reskin-name <texture object name>
            var outFile = Path.Combine(Path.GetFullPath(o.Output), o.GenMat + ".uasset");
            Directory.CreateDirectory(Path.GetFullPath(o.Output));
            Output.Writer.MaterialWriter.WriteEditorMaterial(outFile, o.GenMat, "/Game/" + o.GenMat,
                o.ReskinPath ?? "/Game/DummySpriteTexture", o.ReskinName ?? "DummySpriteTexture");
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.Reemit))
        {
            Output.Writer.WriterSelfTest.ReEmit(o.Reemit, o.Output);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.BuildGraph))
        {
            Reconstructors.BlueprintGraphBuilder.Build(o.BuildGraph, o.Output);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.HexExport))
        {
            Output.Writer.WriterSelfTest.HexDumpExports(o.HexExport);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.CloneBp))
        {
            Reconstructors.BlueprintGraphBuilder.Clone(o.CloneBp, o.Output);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.InjectEvent))
        {
            Reconstructors.BlueprintGraphBuilder.InjectEvent(o.InjectEvent, o.Output, o.EventName ?? "ReceiveEndPlay");
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.WalkBytecode))
        {
            Output.Writer.WriterSelfTest.WalkBytecode(o.WalkBytecode);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.ProbeLevel))
        {
            Output.Writer.WriterSelfTest.ProbeLevel(o.ProbeLevel);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.InjectCall))
        {
            Reconstructors.BlueprintGraphBuilder.InjectWiredCall(o.InjectCall, o.Output, o.CallMessage ?? "reconstructed");
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.Reconstruct))
        {
            Reconstructors.BlueprintGraphBuilder.ReconstructChain(o.Reconstruct, o.Template ?? "", o.Output,
                o.ReskinName ?? "MinimalGameMode", o.ReskinPath ?? "/Game/Maps/MinimalGameMode");
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.PlaceActors))
        {
            Reconstructors.BlueprintGraphBuilder.PlaceActors(o.PlaceActors, o.Template ?? "", o.Output,
                o.ReskinName ?? "placed", o.ReskinPath ?? "/Game/Maps/placed", o.Cube, o.ContentRoot);
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(o.Reskin))
        {
            Reconstructors.BlueprintGraphBuilder.Reskin(o.Reskin, o.Output, o.ReskinName ?? "BP_HandProxyExample", o.ReskinPath ?? "/Game/Maps/BP_HandProxyExample");
            return 0;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(o.Input) || string.IsNullOrWhiteSpace(o.Output))
            {
                Log.Error("--input and --output are required for extraction. Local dev commands such as --dump-package and --compare-meshes do not need them.");
                return 1;
            }

            // 1. Resolve AES key + initial engine version (hint, or a sensible default for mounting).
            var aesKey = AesKeyResolver.FromHex(o.AesKey);
            var hinted = VersionDetector.FromHint(o.Version);
            var mountGame = hinted ?? EGame.GAME_UE4_27;
            if (hinted is null)
                Log.Warning("No --version hint; mounting as {Game} and auto-detecting. Pass --version for best accuracy.", mountGame);

            // 2. Mount containers. Enable bytecode reading when reconstructing blueprints
            //    (always needed for --full-recovery; also lets the default BP reconstructor see opcodes).
            var readScript = o.FullRecovery || !o.SkipBlueprints;
            using var extractor = new PakExtractor(o.Input, mountGame, aesKey, readScript);
            // UE5 unversioned properties need type mappings; without a usmap those packages fail to parse.
            if (!string.IsNullOrWhiteSpace(o.Usmap))
            {
                extractor.Provider.MappingsContainer = new CUE4Parse.MappingsProvider.FileUsmapTypeMappingsProvider(o.Usmap);
                Log.Information("Loaded usmap mappings from {Path}", o.Usmap);
            }
            else if (mountGame >= EGame.GAME_UE5_0)
                Log.Warning("UE5 target without --usmap: unversioned packages will NOT parse. Dump a .usmap from the running game (UE4SS/Dumper-7) and pass --usmap.");
            var parser = new AssetParser(extractor.Provider);

            // Canonical-case map for /Game package paths, built from the mounted file list. Writers consult this
            // at name-table serialization time to emit one consistent folder-case spelling, preventing the
            // "FPackageId collision" assert without any lossy in-place patching. See PackagePathCanon.
            Output.Writer.PackagePathCanon.Build(extractor.Provider.Files.Keys);

            if (!string.IsNullOrWhiteSpace(o.DumpVPath))
            {
                var key = extractor.Provider.Files.Keys.FirstOrDefault(k =>
                    k.Contains(o.DumpVPath, StringComparison.OrdinalIgnoreCase) &&
                    (k.EndsWith(".uasset") || k.EndsWith(".umap")));
                if (key == null) { Log.Error("no package match for '{S}'", o.DumpVPath); return 1; }
                Log.Information("Dumping {Key}", key);
                Output.Writer.WriterSelfTest.DumpLoadedPackage(extractor.Provider.LoadPackage(key));
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(o.ListFiles))
            {
                var hits = extractor.Provider.Files.Keys
                    .Where(k => k.Contains(o.ListFiles, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k).Take(200).ToList();
                Log.Information("{N} match(es) for '{S}':", hits.Count, o.ListFiles);
                foreach (var h in hits) Console.WriteLine("  " + h);
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(o.ExportTexturePngs))
            {
                var outDir = o.ExportTexturePngs;
                var keys = extractor.EnumeratePackages(o.Filter)
                    .Select(f => f.Path)
                    .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                int ok = 0, skip = 0, fail = 0;
                foreach (var key in keys)
                {
                    CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D? tex = null;
                    try
                    {
                        var pkg = extractor.Provider.LoadPackage(key);
                        for (var i = 0; i < pkg.ExportsLazy.Length; i++)
                            try { if (pkg.ExportsLazy[i].Value is CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D t) { tex = t; break; } } catch { }
                    }
                    catch { }
                    if (tex is null) { skip++; continue; }
                    try
                    {
                        var decoded = tex.Decode(ETexturePlatform.DesktopMobile);
                        if (decoded is null) { fail++; continue; }
                        using var bmp = decoded.ToSkBitmap();
                        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                        // virtual key "<Mount>/Content/<rel>.uasset" -> game-relative "<rel>", mirrored under outDir.
                        var ci = key.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
                        var rel = (ci >= 0 ? key[(ci + 9)..] : key);
                        rel = rel[..^".uasset".Length];
                        var pngPath = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar) + ".png");
                        Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
                        using (var fs = File.Create(pngPath)) data.SaveTo(fs);
                        File.WriteAllText(Path.ChangeExtension(pngPath, ".json"),
                            $"{{\"game\":\"/Game/{rel}\",\"srgb\":{(tex.SRGB ? "true" : "false")},\"normal\":{(tex.IsNormalMap ? "true" : "false")},\"compression\":\"{tex.CompressionSettings}\"}}");
                        ok++;
                    }
                    catch (Exception ex) { Log.Warning("texture {K}: {M}", key, ex.Message); fail++; }
                }
                Log.Information("Exported {OK} texture PNG(s) ({Skip} non-texture, {Fail} failed) -> {Dir}", ok, skip, fail, outDir);
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(o.DumpShader))
            {
                extractor.Provider.ReadShaderMaps = true;
                var key = extractor.Provider.Files.Keys.FirstOrDefault(k =>
                    k.Contains(o.DumpShader, StringComparison.OrdinalIgnoreCase) && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase));
                if (key == null) { Log.Error("no .uasset match for '{S}'", o.DumpShader); return 1; }
                var pkg = extractor.Provider.LoadPackage(key);
                var mat = pkg.GetExports().OfType<CUE4Parse.UE4.Assets.Exports.Material.UMaterial>().FirstOrDefault();
                if (mat == null) { Log.Error("no UMaterial export in {K}", key); return 1; }
                Console.WriteLine($"Material {mat.Name}: BlendMode={mat.BlendMode} ShadingModel={mat.ShadingModel} TwoSided={mat.TwoSided}");
                Console.WriteLine($"  ReferencedTextures ({mat.ReferencedTextures.Count}):");
                foreach (var tex in mat.ReferencedTextures) Console.WriteLine($"    {tex?.Name}");
                Console.WriteLine($"  LoadedMaterialResources: {mat.LoadedMaterialResources.Count}");
                foreach (var res in mat.LoadedMaterialResources)
                {
                    var sm = res.LoadedShaderMap;
                    if (sm == null) { Console.WriteLine("    (resource has no shadermap)"); continue; }
                    Console.WriteLine($"    ShaderPlatform={sm.ShaderPlatform}  sharedCode={sm.Code == null}");
                    if (sm.Content is CUE4Parse.UE4.Assets.Exports.Material.FMaterialShaderMapContent c)
                    {
                        Console.WriteLine($"      OrderedMeshShaderMaps={c.OrderedMeshShaderMaps?.Length}");
                        var u = c.MaterialCompilationOutput?.UniformExpressionSet;
                        if (u != null)
                        {
                            Console.WriteLine($"      UniformExpressionSet: scalarParams={u.UniformScalarParameters.Length} vectorParams={u.UniformVectorParameters.Length} numericParams={u.UniformNumericParameters.Length} vectorPreshaders={u.UniformVectorPreshaders.Length} scalarPreshaders={u.UniformScalarPreshaders.Length} preshaders={u.UniformPreshaders.Length} defaultValuesBytes={u.DefaultValues?.Length} preshaderBufSize={u.UniformPreshaderBufferSize}");
                            foreach (var p in u.UniformScalarParameters) Console.WriteLine($"        scalar  '{p.ParameterName}' default={p.DefaultValue}");
                            foreach (var p in u.UniformVectorParameters) Console.WriteLine($"        vector  '{p.ParameterName}' default={p.DefaultValue}");
                            if (u.UniformTextureParameters != null)
                                foreach (var arr in u.UniformTextureParameters)
                                    foreach (var tp in arr) Console.WriteLine($"        texture '{tp.ParameterName}'");
                        }
                        else Console.WriteLine("      (no UniformExpressionSet)");
                    }
                }
                return 0;
            }

            if (o.FindHiddenActors)
            {
                var keys = extractor.EnumeratePackages(o.Filter)
                    .Select(f => f.Path)
                    .Where(k => k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                int mapsWithHidden = 0, hiddenTotal = 0;
                foreach (var key in keys)
                {
                    List<string> hits = new();
                    try
                    {
                        var pkg = extractor.Provider.LoadPackage(key);
                        var exports = pkg.GetExports().ToList();
                        foreach (var e in exports)
                        {
                            if (e.Outer?.Name.Text != "PersistentLevel") continue;
                            var cls = e.ExportType;
                            if (cls.EndsWith("Component") || cls is "Model" or "Brush" or "Polys" or "Level" or "World" or "WorldSettings") continue;
                            bool aHidden = e.GetOrDefault<bool>("bHidden", false);
                            bool editorOnly = e.GetOrDefault<bool>("bIsEditorOnlyActor", false);
                            // rendering component: mesh-bearing child, else any child component
                            var comps = exports.Where(c => c.Outer?.Name.Text == e.Name && c.ExportType.EndsWith("Component")).ToList();
                            var rc = comps.FirstOrDefault(c => c.ExportType.Contains("StaticMesh")) ?? comps.FirstOrDefault();
                            bool cInvis = rc != null && !rc.GetOrDefault<bool>("bVisible", true);
                            bool cHig = rc != null && rc.GetOrDefault<bool>("bHiddenInGame", false);
                            if (aHidden || editorOnly || cInvis || cHig)
                            {
                                var flags = string.Join(",",
                                    (aHidden ? new[] { "bHidden" } : System.Array.Empty<string>())
                                    .Concat(editorOnly ? new[] { "bIsEditorOnlyActor" } : System.Array.Empty<string>())
                                    .Concat(cInvis ? new[] { "bVisible=false" } : System.Array.Empty<string>())
                                    .Concat(cHig ? new[] { "bHiddenInGame" } : System.Array.Empty<string>()));
                                hits.Add($"    {e.Name} ({cls}) [{flags}]");
                            }
                        }
                    }
                    catch { continue; }
                    if (hits.Count > 0)
                    {
                        mapsWithHidden++; hiddenTotal += hits.Count;
                        Console.WriteLine($"{key}  ({hits.Count} hidden)");
                        foreach (var h in hits.Take(12)) Console.WriteLine(h);
                        if (hits.Count > 12) Console.WriteLine($"    ... +{hits.Count - 12} more");
                    }
                }
                Log.Information("Scanned {N} map(s): {M} have hidden/invisible actors ({T} total)", keys.Count, mapsWithHidden, hiddenTotal);
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(o.DumpActorTree))
            {
                var key = extractor.Provider.Files.Keys.FirstOrDefault(k =>
                    k.Contains(o.DumpActorTree, StringComparison.OrdinalIgnoreCase) && k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase));
                if (key == null) { Log.Error("no .umap match for '{S}'", o.DumpActorTree); return 1; }
                Log.Information("Actor tree for {Key}", key);
                var pkg = extractor.Provider.LoadPackage(key);
                var exports = pkg.GetExports().ToList();
                string V(CUE4Parse.UE4.Assets.Exports.UObject c, string n, string def = "") {
                    var fp = c.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex>(n)?.ResolvedObject;
                    if (fp != null) return fp.Name.Text;
                    return def;
                }
                string Vec(CUE4Parse.UE4.Assets.Exports.UObject c, string n) {
                    var s = c.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>(n);
                    return $"{s.X:0.#},{s.Y:0.#},{s.Z:0.#}";
                }
                foreach (var e in exports)
                {
                    if (e.Outer?.Name.Text != "PersistentLevel") continue;
                    if (e.ExportType.EndsWith("Component")) continue;
                    bool isBp = !e.Class?.Name.Text?.StartsWith("/Script") == true || (e.Class?.Name.Text?.EndsWith("_C") ?? false);
                    Console.WriteLine($"ACTOR {e.Name} : {e.ExportType}  (class={e.Class?.Name.Text})  bHidden={e.GetOrDefault<bool>("bHidden", false)}");
                    var comps = exports.Where(c => c.Outer?.Name.Text == e.Name && c.ExportType.EndsWith("Component")).ToList();
                    foreach (var c in comps)
                    {
                        var mesh = V(c, "StaticMesh");
                        var om = c.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex[]>("OverrideMaterials");
                        var omStr = om == null ? "" : "  OverrideMaterials=[" + string.Join(",", om.Select(x => x?.ResolvedObject?.Name.Text ?? "null")) + "]";
                        string flags = "";
                        if (!c.GetOrDefault<bool>("bVisible", true)) flags += " bVisible=false";
                        if (c.GetOrDefault<bool>("bHiddenInGame", false)) flags += " bHiddenInGame";
                        if (c.GetOrDefault<bool>("bAbsoluteLocation", false)) flags += " absLoc";
                        Console.WriteLine($"    COMP {c.Name} : {c.ExportType}  loc={Vec(c,"RelativeLocation")} scale={Vec(c,"RelativeScale3D")}  mesh={(string.IsNullOrEmpty(mesh)?"-":mesh)}{omStr}{flags}");
                    }
                }
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(o.TestBuiltData))
            {
                var bdKey = extractor.Provider.Files.Keys.FirstOrDefault(k =>
                    k.Contains(o.TestBuiltData, StringComparison.OrdinalIgnoreCase) && k.Contains("_BuiltData", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase));
                if (bdKey == null) { Log.Error("no _BuiltData match for '{S}'", o.TestBuiltData); return 1; }
                var srcReg = extractor.Provider.LoadPackage(bdKey).GetExports()
                    .OfType<CUE4Parse.UE4.Assets.Exports.BuildData.UMapBuildDataRegistry>().FirstOrDefault();
                if (srcReg == null) { Log.Error("no MapBuildDataRegistry export in {K}", bdKey); return 1; }
                int srcCount = srcReg.MeshBuildData?.Count ?? 0;
                Log.Information("Source registry {K}: {N} MeshBuildData entries", bdKey, srcCount);

                var (rov, refl, fort) = Output.Writer.BuiltDataWriter.GatesForGame(mountGame);
                var native = Output.Writer.BuiltDataWriter.SerializeRegistryNative(srcReg, rov, refl, fort,
                    fp => fp?.Index ?? 0);   // identity remap (round-trip; texture refs are just int32s on re-read)

                var ci = bdKey.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
                var rel = ci >= 0 ? bdKey[(ci + 9)..] : bdKey; rel = rel[..^".uasset".Length];
                var regPkgPath = "/Game/" + rel;
                var regObjName = srcReg.Name;

                var spw = new Output.Writer.SynthPackageWriter(mountGame, regPkgPath);
                spw.CustomVersionsOverride = new List<(CUE4Parse.UE4.Objects.Core.Misc.FGuid, int)>();   // empty -> CUE4Parse/editor fall back to EGame defaults (UE5.1 = modern lightmap layout)
                int enginePkg = spw.AddImport("/Script/CoreUObject", "Package", 0, "/Script/Engine");
                int regClass = spw.AddImport("/Script/CoreUObject", "Class", enginePkg, "MapBuildDataRegistry");
                byte[] payload;
                using (var ms = new MemoryStream()) { using var aw = new Output.Writer.FArchiveWriter(ms);
                    var t = new Output.Writer.TaggedPropertyWriter(aw, spw.Name);
                    t.Enum("LevelLightingQuality", "ELightingBuildQuality", "ELightingBuildQuality::Quality_Preview");
                    t.WriteNone();
                    aw.Write(0);                 // UObject bSerializeGuid = 0
                    aw.WriteBytes(native);
                    aw.Flush(); payload = ms.ToArray();
                }
                spw.AddExport(regObjName, regClass, 0, 0, payload, 0x1u | 0x8u, 0, true);
                var tmp = Path.Combine(Path.GetTempPath(), "rt_builtdata.uasset");
                spw.Write(tmp);
                Log.Information("Wrote round-trip package {T} ({B} bytes, native {N})", tmp, new FileInfo(tmp).Length, native.Length);

                try
                {
                    var bytes = File.ReadAllBytes(tmp);
                    var rp = new CUE4Parse.UE4.Assets.Package(
                        new CUE4Parse.UE4.Readers.FByteArchive("rt", bytes, new CUE4Parse.UE4.Versions.VersionContainer(mountGame)),
                        (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null, (CUE4Parse.UE4.Readers.FArchive?)null,
                        (CUE4Parse.FileProvider.IFileProvider?)null, false);
                    CUE4Parse.UE4.Assets.Exports.BuildData.UMapBuildDataRegistry? reg2 = null;
                    for (var ei = 0; ei < rp.ExportsLazy.Length; ei++)
                        try { if (rp.ExportsLazy[ei].Value is CUE4Parse.UE4.Assets.Exports.BuildData.UMapBuildDataRegistry r) { reg2 = r; break; } } catch { }
                    int rtCount = reg2?.MeshBuildData?.Count ?? 0;
                    Log.Information("Round-trip re-read: {N} MeshBuildData entries (source {S}) -> {R}", rtCount, srcCount, rtCount == srcCount ? "MATCH" : "MISMATCH");
                    if (reg2?.MeshBuildData != null && srcReg.MeshBuildData != null)
                    {
                        foreach (var kv in srcReg.MeshBuildData)
                        {
                            if (!reg2.MeshBuildData.TryGetValue(kv.Key, out var v2)) { Log.Warning("  key {K} MISSING after round-trip", kv.Key); continue; }
                            var a = (kv.Value.LightMap as CUE4Parse.UE4.Assets.Exports.BuildData.FLightMap2D)?.CoordinateScale;
                            var b = (v2.LightMap as CUE4Parse.UE4.Assets.Exports.BuildData.FLightMap2D)?.CoordinateScale;
                            Log.Information("  key {K}: coordScale src=({AX},{AY}) rt=({BX},{BY})", kv.Key, a?.X, a?.Y, b?.X, b?.Y);
                            break;
                        }
                    }
                }
                catch (Exception ex) { Log.Error(ex, "round-trip re-read FAILED"); }
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(o.DumpBuiltData))
            {
                var mapKey = extractor.Provider.Files.Keys.FirstOrDefault(k =>
                    k.Contains(o.DumpBuiltData, StringComparison.OrdinalIgnoreCase) && k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase));
                if (mapKey == null) { Log.Error("no .umap match for '{S}'", o.DumpBuiltData); return 1; }
                var shortName = System.IO.Path.GetFileNameWithoutExtension(mapKey);
                var bdKey = extractor.Provider.Files.Keys.FirstOrDefault(k =>
                    k.Contains(shortName + "_BuiltData", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase));
                Log.Information("Map {M}\nBuiltData {B}", mapKey, bdKey ?? "(none found)");

                var reg = bdKey == null ? null : extractor.Provider.LoadPackage(bdKey).GetExports()
                    .OfType<CUE4Parse.UE4.Assets.Exports.BuildData.UMapBuildDataRegistry>().FirstOrDefault();
                var mesh = reg?.MeshBuildData;
                Log.Information("Registry MeshBuildData entries: {N}", mesh?.Count ?? 0);
                if (mesh != null)
                {
                    int shown = 0;
                    foreach (var kv in mesh)
                    {
                        if (shown++ >= 6) break;
                        var lm = kv.Value.LightMap as CUE4Parse.UE4.Assets.Exports.BuildData.FLightMap2D;
                        var texs = lm?.Textures == null ? "-" : string.Join(",", lm.Textures.Select(t => t?.ResolvedObject?.Name.Text ?? "null"));
                        Console.WriteLine($"  {kv.Key}  lightmapTex=[{texs}]  shadowMap={(kv.Value.ShadowMap != null ? "yes" : "no")}");
                    }
                }
                var regKeys = mesh != null ? new HashSet<CUE4Parse.UE4.Objects.Core.Misc.FGuid>(mesh.Keys) : new();

                var exports = extractor.Provider.LoadPackage(mapKey).GetExports().ToList();
                int smc = 0, withId = 0, matched = 0;
                foreach (var e in exports)
                {
                    if (e is not CUE4Parse.UE4.Assets.Exports.Component.StaticMesh.UStaticMeshComponent c) continue;
                    smc++;
                    var id = c.LODData != null && c.LODData.Length > 0 ? c.LODData[0].MapBuildDataId : default;
                    if (id != default) withId++;
                    if (regKeys.Contains(id)) { matched++; if (matched <= 6) Console.WriteLine($"  COMP {e.Name}  MapBuildDataId={id}  -> MATCH"); }
                }
                Log.Information("StaticMeshComponents: {S} total, {W} carry a MapBuildDataId, {M} match a registry key", smc, withId, matched);
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(o.DumpMeshUv))
            {
                var key = extractor.Provider.Files.Keys.FirstOrDefault(k =>
                    k.Contains(o.DumpMeshUv, StringComparison.OrdinalIgnoreCase) && k.EndsWith(".uasset"));
                if (key == null) { Log.Error("no package match for '{S}'", o.DumpMeshUv); return 1; }
                var pkg = extractor.Provider.LoadPackage(key);
                CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh? sm = null;
                for (var i = 0; i < pkg.ExportsLazy.Length; i++)
                    try { if (pkg.ExportsLazy[i].Value is CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh m) { sm = m; break; } } catch { }
                if (sm is null) { Log.Error("no UStaticMesh in {K}", key); return 1; }
                if (!sm.TryConvert(out var cm) || cm.LODs.Count == 0) { Log.Error("TryConvert failed / no LODs"); return 1; }
                var lod = cm.LODs[0];
                var verts = lod.Verts!;
                float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
                foreach (var vv in verts) { var uv = vv.UV; if (uv.U < uMin) uMin = uv.U; if (uv.U > uMax) uMax = uv.U; if (uv.V < vMin) vMin = uv.V; if (uv.V > vMax) vMax = uv.V; }
                Log.Information("Mesh {K}: {V} verts, {T} texcoords, UV range U[{Umin}..{Umax}] V[{Vmin}..{Vmax}]",
                    key, verts.Length, lod.NumTexCoords, uMin, uMax, vMin, vMax);
                for (var i = 0; i < Math.Min(8, verts.Length); i++)
                    Log.Information("  vert[{I}] pos=({X},{Y},{Z}) uv=({U},{V})", i,
                        verts[i].Position.X, verts[i].Position.Y, verts[i].Position.Z, verts[i].UV.U, verts[i].UV.V);
                return 0;
            }
            if (o.GenMeshAll)
{
    if (string.IsNullOrWhiteSpace(o.Cube))
    {
        Log.Error("--gen-mesh-all needs --cube");
        return 1;
    }

    Directory.CreateDirectory(Path.GetFullPath(o.Output));

    var keys = extractor.Provider.Files.Keys
        .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        .OrderBy(k => k)
        .ToList();

    var made = 0;
    var skipped = 0;

    foreach (var key in keys)
    {
        try
        {
            var meshPkg = (CUE4Parse.UE4.Assets.Package)extractor.Provider.LoadPackage(key);

            CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh? sm = null;

            for (var i = 0; i < meshPkg.ExportMap.Length && sm == null; i++)
            {
                if (meshPkg.ExportMap[i].ClassName != "StaticMesh") continue;

                try
                {
                    sm = meshPkg.ExportsLazy[i].Value as CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh;
                }
                catch
                {
                    // not loadable, skip
                }
            }

            if (sm == null)
            {
                skipped++;
                continue;
            }

            if (!sm.TryConvert(out var cm) || cm.LODs.Count == 0)
            {
                skipped++;
                continue;
            }

            var blob = Output.Writer.MeshWriter.BuildFRawMesh(cm.LODs[0]);   // this path writes the cube's 1 slot
            var bounds = Output.Writer.MeshWriter.CalculateBounds(cm.LODs[0]);
            var name = sm.Name;

            var gamePath =
                "/Game/" +
                key.Substring(key.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase) + "/Content/".Length)
                   .Replace(".uasset", "");

            var outFile = Path.Combine(Path.GetFullPath(o.Output), name + ".uasset");

            Reconstructors.BlueprintGraphBuilder.CloneMesh(
                o.Cube,
                outFile,
                name,
                gamePath,
                blob,
                bounds: bounds
            );

            Log.Information("Real mesh -> {Out} (gamePath {G})", outFile, gamePath);
            made++;
        }
        catch (Exception ex)
        {
            Log.Warning("skip {K}: {M}", key, ex.Message);
            skipped++;
        }
    }

    Log.Information("gen-mesh-all done: {Made} made, {Skipped} skipped", made, skipped);
    return 0;
}

            if (!string.IsNullOrWhiteSpace(o.GenTex))
            {
                Directory.CreateDirectory(Path.GetFullPath(o.Output));
                var key = extractor.Provider.Files.Keys
                    .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && k.Contains(o.GenTex, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k).FirstOrDefault();
                if (key is null) { Log.Error("no texture matching '{S}'", o.GenTex); return 1; }
                var tpkg = (CUE4Parse.UE4.Assets.Package)extractor.Provider.LoadPackage(key);
                CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D? tex = null;
                for (var i = 0; i < tpkg.ExportMap.Length && tex == null; i++)
                {
                    if (!tpkg.ExportMap[i].ClassName.Contains("Texture2D")) continue;
                    try { tex = tpkg.ExportsLazy[i].Value as CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D; } catch { }
                }
                if (tex is null) { Log.Error("no UTexture2D export in {K}", key); return 1; }
                var gamePath = "/Game/" + key.Substring(key.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase) + "/Content/".Length).Replace(".uasset", "");
                var outFile = Path.Combine(Path.GetFullPath(o.Output), tex.Name + ".uasset");
                Output.Writer.TextureWriter.WriteEditorTexture(tex, outFile, tex.Name, gamePath);
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(o.GenMeshReal))
            {
                var key = extractor.Provider.Files.Keys.FirstOrDefault(k =>
                    k.Contains(o.GenMeshReal, StringComparison.OrdinalIgnoreCase) && k.EndsWith(".uasset"));
                if (key == null) { Log.Error("no mesh .uasset match for '{S}'", o.GenMeshReal); return 1; }
                var meshPkg = extractor.Provider.LoadPackage(key);
                var exports = ((CUE4Parse.UE4.Assets.IPackage)meshPkg).GetExports();
                var sm = exports.OfType<CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh>().FirstOrDefault();
                if (sm == null) { Log.Error("no loadable UStaticMesh export in {K}", key); return 1; }
                if (!sm.TryConvert(out var cm) || cm.LODs.Count == 0) { Log.Error("mesh convert failed"); return 1; }
                var blob = Output.Writer.MeshWriter.BuildFRawMesh(cm.LODs[0], sm.StaticMaterials?.Length ?? 0);
                var bounds = Output.Writer.MeshWriter.CalculateBounds(cm.LODs[0]);
                var name = sm.Name;
                var gamePath = "/Game/" + key.Substring(key.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase) + "/Content/".Length).Replace(".uasset", "");
                Directory.CreateDirectory(Path.GetFullPath(o.Output));
                var outFile = Path.Combine(Path.GetFullPath(o.Output), name + ".uasset");
                var mats = meshPkg is CUE4Parse.UE4.Assets.Package legacyPkg
                    ? Output.ContentWriter.ResolveMeshMaterials(sm, legacyPkg)
                    : new List<(string pkg, string name, string slot)>();
                Reconstructors.BlueprintGraphBuilder.CloneMesh(o.Cube ?? "", outFile, name, gamePath, blob,
                    materials: mats.Count > 0 ? mats : null, bounds: bounds);
                Log.Information("Real mesh -> {Out} (gamePath {G}, {M} material slot(s))", outFile, gamePath, mats.Count);
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(o.ExtractFile))
            {
                var key = extractor.Provider.Files.Keys.FirstOrDefault(k =>
                    k.Contains(o.ExtractFile, StringComparison.OrdinalIgnoreCase) &&
                    (k.EndsWith(".umap") || k.EndsWith(".uasset")));
                if (key == null) { Log.Error("no .umap/.uasset match for '{S}'", o.ExtractFile); return 1; }
                var pkgFiles = extractor.Provider.SavePackage(key);   // {virtualPath: bytes} for .uasset/.umap + .uexp + .ubulk
                Directory.CreateDirectory(Path.GetFullPath(o.Output));
                var baseName = Path.GetFileNameWithoutExtension(key);
                var headerExt = key.EndsWith(".umap") ? ".umap" : ".uasset";
                var header = pkgFiles.First(kv => kv.Key.EndsWith(headerExt)).Value;
                var uexp = pkgFiles.FirstOrDefault(kv => kv.Key.EndsWith(".uexp")).Value;
                var combined = uexp == null ? header : header.Concat(uexp).ToArray();   // editor-combined single file
                var outPath = Path.Combine(Path.GetFullPath(o.Output), baseName + headerExt);
                File.WriteAllBytes(outPath, combined);
                Log.Information("Extracted {K} -> {Out} ({H}B header + {E}B uexp = {T}B)", key, outPath, header.Length, uexp?.Length ?? 0, combined.Length);
                return 0;
            }

            // Dev round-trip test: write one package as uncooked .uasset and re-parse it.
            if (!string.IsNullOrWhiteSpace(o.ValidateWrite))
            {
                Directory.CreateDirectory(Path.GetFullPath(o.Output));
                var pass = Output.Writer.WriterSelfTest.Run(extractor.Provider, mountGame,
                    o.ValidateWrite, Path.GetFullPath(o.Output));
                return pass ? 0 : 3;
            }

            var packages = extractor.EnumeratePackages(o.Filter).ToList();
            // Dedup case-insensitively: A2 has case-inconsistent paths (e.g. Districts/Tackleball vs Districts/TackleBall)
            // that write to the same file on Windows but register as the SAME UE package -> FPackageId collision crash.
            var beforeDedup = packages.Count;
            packages = packages.GroupBy(p => p.Path.ToLowerInvariant()).Select(g => g.First()).ToList();
            if (packages.Count != beforeDedup)
                Log.Information("Deduped {N} case-variant duplicate package path(s)", beforeDedup - packages.Count);
            if (packages.Count == 0)
            {
                Log.Error("No packages found (check --filter / AES key / input path).");
                return 2;
            }
            Log.Information("{Count} package(s) queued for processing", packages.Count);

            // 3. Detect version from the first parseable package; refine the engine association.
            var detectedGame = mountGame;
            foreach (var pkg in packages)
            {
                var probe = parser.Parse(pkg);
                if (probe is null) continue;
                detectedGame = hinted ?? VersionDetector.DetectFromPackage(probe.Package, mountGame);
                break;
            }
            var association = VersionDetector.ToEngineAssociation(detectedGame);

            Output.Writer.TextureWriter.MaxDim = o.TexMax > 0 ? o.TexMax : 1024;
            var projectName = SanitizeProjectName(o.Output);
            // Stubs are what let blueprints that subclass game-native classes reconstruct without crashing, so emit
            // them whenever we're reconstructing blueprints (unless explicitly disabled via --skip-blueprints).
            var emitStubs = o.EmitStubs || !o.SkipBlueprints;
            var opts = new DecompileOptions
            {
                InputPath = o.Input,
                OutputRoot = Path.GetFullPath(o.Output),
                Game = detectedGame,
                EngineAssociation = association,
                FilterGlob = o.Filter,
                SkipBlueprints = o.SkipBlueprints,
                FullRecovery = o.FullRecovery,
                ForceWrite = o.ForceWrite,
                NoMediaExport = o.NoMediaExport,
                DryRun = o.DryRun,
                Verbose = o.Verbose,
                ProjectName = projectName,
                EmitStubs = emitStubs,
                DangerBpGraph = o.DangerouslyDumpBpGraph,
                MapTemplate = o.Template,
                CubePath = o.Cube,
                BpTemplate = o.BpTemplate,
                BpRecoverCalls = o.BpRecoverCalls,
                EmitStubMethods = o.EmitStubMethods
            };

            // 4. Scaffold project (skipped on dry-run).
            if (!o.DryRun) new ProjectScaffold(opts, extractor.Provider).Generate();

            // 5. Process every package with a progress bar.
            var writer = new ContentWriter(opts, extractor.Provider);
            RunPipeline(packages, parser, writer);

            // 5b. Scaffold a content-only .uplugin for each discovered plugin so the editor mounts "/<Name>/" and the
            //     plugin's cooked content references (e.g. /CustomMapTools/...) resolve instead of crashing.
            if (!o.DryRun) writer.ScaffoldPlugins();

            // 6. Generate C++ stub modules for referenced game-native classes (so game-subclassed blueprints resolve).
            if (emitStubs && !o.DryRun)
                new Output.Stubs.StubModuleGenerator(o.SdkDump).Generate(opts.OutputRoot, writer.GameStubs.Values.ToList(),
                    writer.StubBaseHints, opts.EmitStubMethods ? writer.StubMethodHints.Keys.ToList() : new List<string>());

            // 7. Manifest + summary.
            if (o.Report && !o.DryRun) WriteReport(opts, writer.Manifest);
            PrintSummary(writer.Manifest, o.DryRun);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void RunPipeline(List<CUE4Parse.FileProvider.Objects.GameFile> packages, AssetParser parser, ContentWriter writer)
    {
        AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(),
                     new RemainingTimeColumn(), new SpinnerColumn())
            .Start(ctx =>
            {
                var task = ctx.AddTask("[green]Decompiling assets[/]", maxValue: packages.Count);
                var manifestLock = new object();
                var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) };
                Parallel.ForEach(packages, opts, pkg =>
                {
                    try
                    {
                        var parsed = parser.Parse(pkg);
                        if (parsed is null)
                        {
                            // Parsing failed: record a Failed manifest row.
                            lock (manifestLock) writer.Manifest.Add(new ManifestEntry
                            {
                                VirtualPath = pkg.Path,
                                OutputPath = "(parse failed)",
                                AssetType = "Unknown",
                                Fidelity = Fidelity.Failed,
                                Note = "Package failed to parse"
                            });
                        }
                        else
                        {
                            writer.Process(parsed);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Unhandled error processing {Path}", pkg.Path);
                    }
                    finally
                    {
                        task.Increment(1);
                    }
                });
            });
    }

    private static void WriteReport(DecompileOptions opts, List<ManifestEntry> manifest)
    {
        var reportPath = Path.Combine(opts.OutputRoot, "decompile-report.json");
        var report = new
        {
            opts.InputPath,
            Engine = opts.EngineAssociation,
            Generated = DateTime.UtcNow,
            Total = manifest.Count,
            ByFidelity = manifest.GroupBy(m => m.Fidelity).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            Assets = manifest
        };
        File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
        Log.Information("Wrote report -> {Path}", reportPath);
    }

    private static void PrintSummary(List<ManifestEntry> manifest, bool dryRun)
    {
        var table = new Table().Title(dryRun ? "[yellow]Dry-run plan[/]" : "[green]Decompile summary[/]");
        table.AddColumn("Fidelity");
        table.AddColumn(new TableColumn("Count").RightAligned());
        foreach (var group in manifest.GroupBy(m => m.Fidelity).OrderBy(g => g.Key))
            table.AddRow(group.Key.ToString(), group.Count().ToString());
        table.AddRow("[bold]Total[/]", $"[bold]{manifest.Count}[/]");
        AnsiConsole.Write(table);

        var reconstructed = manifest.Count(m => m.Fidelity is Fidelity.Full or Fidelity.Partial or Fidelity.Copied);
        var stubbed = manifest.Count(m => m.Fidelity == Fidelity.Stub);
        var failed = manifest.Count(m => m.Fidelity == Fidelity.Failed);
        Log.Information("Done: {R} reconstructed, {S} stubbed, {F} failed", reconstructed, stubbed, failed);
    }

    private static string SanitizeProjectName(string outputPath)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputPath)));
        if (string.IsNullOrWhiteSpace(name)) name = "DecompiledProject";
        var cleaned = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (cleaned.Length == 0 || char.IsDigit(cleaned[0])) cleaned = "P" + cleaned;
        return cleaned;
    }
}
