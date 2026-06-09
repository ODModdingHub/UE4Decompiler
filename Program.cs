using CommandLine;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes;
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
        [Option("input", Required = true, HelpText = "Path to a .pak/.utoc or a directory of containers.")]
        public string Input { get; set; } = "";

        [Option("output", Required = true, HelpText = "Output project root directory.")]
        public string Output { get; set; } = "";

        [Option("version", HelpText = "UE version hint, e.g. 4.27 or 5.1 (auto-detected if omitted).")]
        public string? Version { get; set; }

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

        [Option("list-files", HelpText = "Dev: list mounted virtual paths containing this substring, then exit.")]
        public string? ListFiles { get; set; }

        [Option("extract-file", HelpText = "Dev: extract one package (umap/uasset + uexp combined) by virtual-path substring to --output.")]
        public string? ExtractFile { get; set; }

        [Option("gen-mesh-real", HelpText = "Tier2 mesh: build a REAL-geometry editor StaticMesh from a cooked mesh (by virtual-path substring). Needs --cube. Writes to --output.")]
        public string? GenMeshReal { get; set; }

        [Option("decode", HelpText = "Dev: raw-decode a local .uasset/.umap summary+imports and exit (no pak mount).")]
        public string? Decode { get; set; }

        [Option("dump-package", HelpText = "Dev: parse a local .uasset via CUE4Parse and dump every export's properties (for reversing editor graph format).")]
        public string? DumpPackage { get; set; }

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

        if (!string.IsNullOrWhiteSpace(o.DumpPackage))
        {
            Output.Writer.WriterSelfTest.DumpPackage(o.DumpPackage);
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
            var parser = new AssetParser(extractor.Provider);

            if (!string.IsNullOrWhiteSpace(o.ListFiles))
            {
                var hits = extractor.Provider.Files.Keys
                    .Where(k => k.Contains(o.ListFiles, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k).Take(200).ToList();
                Log.Information("{N} match(es) for '{S}':", hits.Count, o.ListFiles);
                foreach (var h in hits) Console.WriteLine("  " + h);
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
                blob
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
                var meshPkg = (CUE4Parse.UE4.Assets.Package)extractor.Provider.LoadPackage(key);
                CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh? sm = null;
                for (var i = 0; i < meshPkg.ExportMap.Length && sm == null; i++)
                {
                    if (meshPkg.ExportMap[i].ClassName != "StaticMesh") continue;
                    try { sm = meshPkg.ExportsLazy[i].Value as CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh; }
                    catch (Exception ex) { Log.Warning("mesh export {I} load: {M}", i, ex.Message); }
                }
                if (sm == null) { Log.Error("no loadable UStaticMesh export in {K}", key); return 1; }
                if (!sm.TryConvert(out var cm) || cm.LODs.Count == 0) { Log.Error("mesh convert failed"); return 1; }
                var blob = Output.Writer.MeshWriter.BuildFRawMesh(cm.LODs[0], sm.StaticMaterials?.Length ?? 0);
                var name = sm.Name;
                var gamePath = "/Game/" + key.Substring(key.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase) + "/Content/".Length).Replace(".uasset", "");
                Directory.CreateDirectory(Path.GetFullPath(o.Output));
                var outFile = Path.Combine(Path.GetFullPath(o.Output), name + ".uasset");
                var mats = Output.ContentWriter.ResolveMeshMaterials(sm, meshPkg);
                Reconstructors.BlueprintGraphBuilder.CloneMesh(o.Cube ?? "", outFile, name, gamePath, blob,
                    materials: mats.Count > 0 ? mats : null);
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
                CubePath = o.Cube
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
                new Output.Stubs.StubModuleGenerator(o.SdkDump).Generate(opts.OutputRoot, writer.GameStubs.Values.ToList(), writer.StubBaseHints);

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
