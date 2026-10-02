using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes;
using Newtonsoft.Json;
using Serilog;
using Spectre.Console;
using UE4Decompiler.Core;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Core.Utils;
using UE4Decompiler.Output;
using UE4Decompiler.Output.Stubs;
using UE4Decompiler.Output.Writer;
using UE4Decompiler.Reconstructors;
using UE4Decompiler.Utils;

namespace UE4Decompiler.Cli.CommandHandlers;

/// <summary>
/// Preserves 100% backward compatibility for existing command-line arguments and test verbs (Phase 3).
/// </summary>
public static class LegacyHandler
{
    public static int Run(LegacyCliOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.Decode))
        {
            Log.Information("Decoding {File}", o.Decode);
            WriterSelfTest.RawDumpImports(File.ReadAllBytes(o.Decode));
            return ExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(o.DumpPackage))
        {
            WriterSelfTest.DumpPackage(o.DumpPackage);
            return ExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(o.GenMat))
        {
            var outDir = o.Output ?? Directory.GetCurrentDirectory();
            var outFile = Path.Combine(Path.GetFullPath(outDir), o.GenMat + ".uasset");
            Directory.CreateDirectory(Path.GetFullPath(outDir));
            MaterialWriter.WriteEditorMaterial(outFile, o.GenMat, "/Game/" + o.GenMat,
                o.ReskinPath ?? "/Game/DummySpriteTexture", o.ReskinName ?? "DummySpriteTexture");
            return ExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(o.Reemit))
        {
            WriterSelfTest.ReEmit(o.Reemit, o.Output ?? Directory.GetCurrentDirectory());
            return ExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(o.BuildGraph))
        {
            BlueprintGraphBuilder.Build(o.BuildGraph, o.Output ?? Directory.GetCurrentDirectory());
            return ExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(o.HexExport))
        {
            WriterSelfTest.HexDumpExports(o.HexExport);
            return ExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(o.CloneBp))
        {
            BlueprintGraphBuilder.Clone(o.CloneBp, o.Output ?? Directory.GetCurrentDirectory());
            return ExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(o.InjectEvent))
        {
            BlueprintGraphBuilder.InjectEvent(o.InjectEvent, o.Output ?? Directory.GetCurrentDirectory(), o.EventName ?? "ReceiveEndPlay");
            return ExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(o.WalkBytecode))
        {
            WriterSelfTest.WalkBytecode(o.WalkBytecode);
            return ExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(o.ProbeLevel))
        {
            WriterSelfTest.ProbeLevel(o.ProbeLevel);
            return ExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(o.InjectCall))
        {
            BlueprintGraphBuilder.InjectWiredCall(o.InjectCall, o.Output ?? Directory.GetCurrentDirectory(), o.CallMessage ?? "reconstructed");
            return ExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(o.Reconstruct))
        {
            BlueprintGraphBuilder.ReconstructChain(o.Reconstruct, o.Template ?? "", o.Output ?? Directory.GetCurrentDirectory(),
                o.ReskinName ?? "MinimalGameMode", o.ReskinPath ?? "/Game/Maps/MinimalGameMode");
            return ExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(o.PlaceActors))
        {
            BlueprintGraphBuilder.PlaceActors(o.PlaceActors, o.Template ?? "", o.Output ?? Directory.GetCurrentDirectory(),
                o.ReskinName ?? "placed", o.ReskinPath ?? "/Game/Maps/placed", o.Cube, o.ContentRoot);
            return ExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(o.Reskin))
        {
            BlueprintGraphBuilder.Reskin(o.Reskin, o.Output ?? Directory.GetCurrentDirectory(), o.ReskinName ?? "BP_HandProxyExample", o.ReskinPath ?? "/Game/Maps/BP_HandProxyExample");
            return ExitCodes.Success;
        }

        if (string.IsNullOrWhiteSpace(o.Input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --input is required when running recovery pipeline.");
            return ExitCodes.InvalidArguments;
        }

        var outputDir = o.Output ?? "./RecoveredProject";

        try
        {
            var aesKey = AesKeyResolver.FromHex(o.AesKey);
            var hinted = VersionDetector.FromHint(o.Version);
            var mountGame = hinted ?? EGame.GAME_UE4_27;
            if (hinted is null)
                Log.Warning("No --version hint; mounting as {Game} and auto-detecting. Pass --version for best accuracy.", mountGame);

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
                return ExitCodes.Success;
            }

            if (o.GenMeshAll)
            {
                if (string.IsNullOrWhiteSpace(o.Cube))
                {
                    Log.Error("--gen-mesh-all needs --cube");
                    return ExitCodes.InvalidArguments;
                }

                Directory.CreateDirectory(Path.GetFullPath(outputDir));

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
                            try { sm = meshPkg.ExportsLazy[i].Value as CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh; } catch { }
                        }

                        if (sm == null || !sm.TryConvert(out var cm) || cm.LODs.Count == 0)
                        {
                            skipped++;
                            continue;
                        }

                        var blob = MeshWriter.BuildFRawMesh(cm.LODs[0]);
                        var name = sm.Name;
                        var gamePath = "/Game/" + key.Substring(key.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase) + "/Content/".Length).Replace(".uasset", "");
                        var outFile = Path.Combine(Path.GetFullPath(outputDir), name + ".uasset");

                        BlueprintGraphBuilder.CloneMesh(o.Cube, outFile, name, gamePath, blob);
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
                return ExitCodes.Success;
            }

            if (!string.IsNullOrWhiteSpace(o.GenTex))
            {
                Directory.CreateDirectory(Path.GetFullPath(outputDir));
                var key = extractor.Provider.Files.Keys
                    .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && k.Contains(o.GenTex, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k).FirstOrDefault();
                if (key is null) { Log.Error("no texture matching '{S}'", o.GenTex); return ExitCodes.UnsupportedInput; }
                var tpkg = (CUE4Parse.UE4.Assets.Package)extractor.Provider.LoadPackage(key);
                CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D? tex = null;
                for (var i = 0; i < tpkg.ExportMap.Length && tex == null; i++)
                {
                    if (!tpkg.ExportMap[i].ClassName.Contains("Texture2D")) continue;
                    try { tex = tpkg.ExportsLazy[i].Value as CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D; } catch { }
                }
                if (tex is null) { Log.Error("no UTexture2D export in {K}", key); return ExitCodes.ParseFailure; }
                var gamePath = "/Game/" + key.Substring(key.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase) + "/Content/".Length).Replace(".uasset", "");
                var outFile = Path.Combine(Path.GetFullPath(outputDir), tex.Name + ".uasset");
                TextureWriter.WriteEditorTexture(tex, outFile, tex.Name, gamePath);
                return ExitCodes.Success;
            }

            if (!string.IsNullOrWhiteSpace(o.GenMeshReal))
            {
                var key = extractor.Provider.Files.Keys.FirstOrDefault(k =>
                    k.Contains(o.GenMeshReal, StringComparison.OrdinalIgnoreCase) && k.EndsWith(".uasset"));
                if (key == null) { Log.Error("no mesh .uasset match for '{S}'", o.GenMeshReal); return ExitCodes.UnsupportedInput; }
                var meshPkg = (CUE4Parse.UE4.Assets.Package)extractor.Provider.LoadPackage(key);
                CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh? sm = null;
                for (var i = 0; i < meshPkg.ExportMap.Length && sm == null; i++)
                {
                    if (meshPkg.ExportMap[i].ClassName != "StaticMesh") continue;
                    try { sm = meshPkg.ExportsLazy[i].Value as CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh; }
                    catch (Exception ex) { Log.Warning("mesh export {I} load: {M}", i, ex.Message); }
                }
                if (sm == null) { Log.Error("no loadable UStaticMesh export in {K}", key); return ExitCodes.ParseFailure; }
                if (!sm.TryConvert(out var cm) || cm.LODs.Count == 0) { Log.Error("mesh convert failed"); return ExitCodes.ParseFailure; }
                var blob = MeshWriter.BuildFRawMesh(cm.LODs[0], sm.StaticMaterials?.Length ?? 0);
                var name = sm.Name;
                var gamePath = "/Game/" + key.Substring(key.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase) + "/Content/".Length).Replace(".uasset", "");
                Directory.CreateDirectory(Path.GetFullPath(outputDir));
                var outFile = Path.Combine(Path.GetFullPath(outputDir), name + ".uasset");
                var mats = ContentWriter.ResolveMeshMaterials(sm, meshPkg);
                BlueprintGraphBuilder.CloneMesh(o.Cube ?? "", outFile, name, gamePath, blob,
                    materials: mats.Count > 0 ? mats : null);
                Log.Information("Real mesh -> {Out} (gamePath {G}, {M} material slot(s))", outFile, gamePath, mats.Count);
                return ExitCodes.Success;
            }

            if (!string.IsNullOrWhiteSpace(o.ExtractFile))
            {
                var key = extractor.Provider.Files.Keys.FirstOrDefault(k =>
                    k.Contains(o.ExtractFile, StringComparison.OrdinalIgnoreCase) &&
                    (k.EndsWith(".umap") || k.EndsWith(".uasset")));
                if (key == null) { Log.Error("no .umap/.uasset match for '{S}'", o.ExtractFile); return ExitCodes.UnsupportedInput; }
                var pkgFiles = extractor.Provider.SavePackage(key);
                Directory.CreateDirectory(Path.GetFullPath(outputDir));
                var baseName = Path.GetFileNameWithoutExtension(key);
                var headerExt = key.EndsWith(".umap") ? ".umap" : ".uasset";
                var header = pkgFiles.First(kv => kv.Key.EndsWith(headerExt)).Value;
                var uexp = pkgFiles.FirstOrDefault(kv => kv.Key.EndsWith(".uexp")).Value;
                var combined = uexp == null ? header : header.Concat(uexp).ToArray();
                var outPath = Path.Combine(Path.GetFullPath(outputDir), baseName + headerExt);
                File.WriteAllBytes(outPath, combined);
                Log.Information("Extracted {K} -> {Out}", key, outPath);
                return ExitCodes.Success;
            }

            if (!string.IsNullOrWhiteSpace(o.ValidateWrite))
            {
                Directory.CreateDirectory(Path.GetFullPath(outputDir));
                var pass = WriterSelfTest.Run(extractor.Provider, mountGame, o.ValidateWrite, Path.GetFullPath(outputDir));
                return pass ? ExitCodes.Success : ExitCodes.ValidationFailure;
            }

            var packages = extractor.EnumeratePackages(o.Filter).ToList();
            if (packages.Count == 0)
            {
                Log.Error("No packages found (check --filter / AES key / input path).");
                return ExitCodes.UnsupportedInput;
            }
            Log.Information("{Count} package(s) queued for processing", packages.Count);

            var detectedGame = mountGame;
            foreach (var pkg in packages)
            {
                var probe = parser.Parse(pkg);
                if (probe is null) continue;
                detectedGame = hinted ?? VersionDetector.DetectFromPackage(probe.Package, mountGame);
                break;
            }
            var association = VersionDetector.ToEngineAssociation(detectedGame);

            TextureWriter.MaxDim = o.TexMax > 0 ? o.TexMax : 1024;
            var projectName = PathUtils.SanitizeFileName(Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir))));
            var emitStubs = o.EmitStubs || !o.SkipBlueprints;
            var opts = new DecompileOptions
            {
                InputPath = o.Input,
                OutputRoot = Path.GetFullPath(outputDir),
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

            if (!o.DryRun) new ProjectScaffold(opts, extractor.Provider).Generate();

            var writer = new ContentWriter(opts, extractor.Provider);
            var manifestLock = new object();
            var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) };

            AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(),
                         new RemainingTimeColumn(), new SpinnerColumn())
                .Start(ctx =>
                {
                    var task = ctx.AddTask("[green]Decompiling assets[/]", maxValue: packages.Count);
                    Parallel.ForEach(packages, parallelOpts, pkg =>
                    {
                        try
                        {
                            var parsed = parser.Parse(pkg);
                            if (parsed is null)
                            {
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

            if (!o.DryRun)
            {
                writer.ScaffoldPlugins();
                if (writer.DiscoveredGameplayTags.Count > 0)
                {
                    new ProjectScaffold(opts, extractor.Provider).WriteDefaultGameplayTags(writer.DiscoveredGameplayTags.Keys);
                }
            }

            if (emitStubs && !o.DryRun)
                new StubModuleGenerator(o.SdkDump).Generate(opts.OutputRoot, writer.GameStubs.Values.ToList(), writer.StubBaseHints);

            if (o.Report && !o.DryRun)
            {
                var reportPath = Path.Combine(opts.OutputRoot, "decompile-report.json");
                var report = new
                {
                    opts.InputPath,
                    Engine = opts.EngineAssociation,
                    Generated = DateTime.UtcNow,
                    Total = writer.Manifest.Count,
                    ByFidelity = writer.Manifest.GroupBy(m => m.Fidelity).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                    Assets = writer.Manifest
                };
                File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
                Log.Information("Wrote report -> {Path}", reportPath);
            }

            var table = new Table().Title(o.DryRun ? "[yellow]Dry-run plan[/]" : "[green]Decompile summary[/]");
            table.AddColumn("Fidelity");
            table.AddColumn(new TableColumn("Count").RightAligned());
            foreach (var group in writer.Manifest.GroupBy(m => m.Fidelity).OrderBy(g => g.Key))
                table.AddRow(group.Key.ToString(), group.Count().ToString());
            table.AddRow("[bold]Total[/]", $"[bold]{writer.Manifest.Count}[/]");
            AnsiConsole.Write(table);

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error");
            return ExitCodes.GeneralFailure;
        }
    }
}
