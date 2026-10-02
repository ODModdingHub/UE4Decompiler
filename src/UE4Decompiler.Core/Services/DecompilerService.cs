using System.Diagnostics;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Serilog;
using UE4Decompiler.Core.Abstractions;
using UE4Decompiler.Core.Models;
using UE4Decompiler.Core.Utils;
using UE4Decompiler.Output;
using UE4Decompiler.Output.Stubs;

namespace UE4Decompiler.Core.Services;

/// <summary>
/// Main decompiler service coordinating discovery, parsing, reconstruction, and reporting (Phases 2 & 15).
/// </summary>
public sealed class DecompilerService : IDecompilerService
{
    private readonly IAssetDiscoveryService _discoveryService;
    private readonly IMappingProvider _mappingProvider;
    private readonly IValidationService _validationService;
    private readonly IDiagnosticsService _diagnosticsService;
    private readonly ICacheManager _cacheManager;

    public DecompilerService(
        IAssetDiscoveryService? discoveryService = null,
        IMappingProvider? mappingProvider = null,
        IValidationService? validationService = null,
        IDiagnosticsService? diagnosticsService = null,
        ICacheManager? cacheManager = null)
    {
        _discoveryService = discoveryService ?? new AssetDiscoveryService();
        _mappingProvider = mappingProvider ?? new MappingProvider();
        _validationService = validationService ?? new ValidationService();
        _diagnosticsService = diagnosticsService ?? new DiagnosticsService();
        _cacheManager = cacheManager ?? new CacheManager();
    }

    public async Task<ScanResult> ScanAsync(string inputPath, DecompileOptions options, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var containers = _discoveryService.DiscoverContainers(inputPath);
            var aesKey = AesKeyResolver.FromHex(options.AesKey);
            var hinted = VersionDetector.FromHint(options.EngineAssociation);
            var mountGame = hinted ?? options.Game;

            if (containers.Count == 0 && !File.Exists(inputPath) && !Directory.Exists(inputPath))
            {
                throw new FileNotFoundException($"Input path does not exist: {inputPath}");
            }

            using var extractor = new PakExtractor(inputPath, mountGame, aesKey, readScriptData: false);

            if (!string.IsNullOrWhiteSpace(options.MappingPath))
            {
                _mappingProvider.TryLoadMapping(options.MappingPath, extractor.Provider, out _);
            }

            var detectedGame = mountGame;
            var parser = new AssetParser(extractor.Provider);
            var packages = extractor.EnumeratePackages(options.FilterGlob).ToList();

            foreach (var pkg in packages.Take(10))
            {
                var probe = parser.Parse(pkg);
                if (probe is not null)
                {
                    detectedGame = hinted ?? VersionDetector.DetectFromPackage(probe.Package, mountGame);
                    break;
                }
            }

            var association = VersionDetector.ToEngineAssociation(detectedGame);
            var assets = _discoveryService.EnumerateAssets(extractor.Provider, options.FilterGlob);
            var countsByClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var asset in assets)
            {
                var ext = asset.Extension.ToLowerInvariant();
                var kind = ext switch
                {
                    "umap" => "World",
                    _ => "UAsset"
                };
                countsByClass[kind] = countsByClass.GetValueOrDefault(kind) + 1;
            }

            var isEncrypted = extractor.Provider.MountedVfs.Count == 0 && extractor.Provider.UnloadedVfs.Count > 0;
            var projectName = PathUtils.SanitizeFileName(Path.GetFileNameWithoutExtension(inputPath));

            return new ScanResult
            {
                InputPath = inputPath,
                DetectedGame = detectedGame,
                EngineAssociation = association,
                Containers = containers,
                Assets = assets,
                AssetCountsByClass = countsByClass,
                IsEncrypted = isEncrypted,
                RequiresMapping = detectedGame >= EGame.GAME_UE4_26,
                DetectedProjectName = projectName
            };
        }, ct);
    }

    public async Task<RecoveryReport> RecoverAsync(
        string inputPath,
        string outputPath,
        DecompileOptions options,
        IProgress<RecoveryProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            ct.ThrowIfCancellationRequested();

            var safeOutput = PathUtils.NormalizeAndValidateDirectory(outputPath);
            var aesKey = AesKeyResolver.FromHex(options.AesKey);
            var hinted = VersionDetector.FromHint(options.EngineAssociation);
            var mountGame = hinted ?? options.Game;

            var readScript = options.FullRecovery || !options.SkipBlueprints;
            using var extractor = new PakExtractor(inputPath, mountGame, aesKey, readScript);

            if (!string.IsNullOrWhiteSpace(options.MappingPath))
            {
                _mappingProvider.TryLoadMapping(options.MappingPath, extractor.Provider, out _);
            }

            var parser = new AssetParser(extractor.Provider);
            var packages = extractor.EnumeratePackages(options.FilterGlob).ToList();

            var detectedGame = mountGame;
            foreach (var pkg in packages.Take(10))
            {
                var probe = parser.Parse(pkg);
                if (probe is not null)
                {
                    detectedGame = hinted ?? VersionDetector.DetectFromPackage(probe.Package, mountGame);
                    break;
                }
            }
            var association = VersionDetector.ToEngineAssociation(detectedGame);

            var resolvedOptions = new DecompileOptions
            {
                InputPath = inputPath,
                OutputRoot = safeOutput,
                Game = detectedGame,
                EngineAssociation = association,
                FilterGlob = options.FilterGlob,
                MappingPath = options.MappingPath,
                AesKey = options.AesKey,
                SkipBlueprints = options.SkipBlueprints,
                FullRecovery = options.FullRecovery,
                ForceWrite = options.ForceWrite,
                NoMediaExport = options.NoMediaExport,
                DryRun = options.DryRun,
                Verbose = options.Verbose,
                EmitStubs = options.EmitStubs,
                DangerBpGraph = options.DangerBpGraph,
                ProjectName = options.ProjectName,
                MapTemplate = options.MapTemplate,
                CubePath = options.CubePath,
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                CacheDirectory = options.CacheDirectory,
                UseCache = options.UseCache,
                Profile = options.Profile
            };

            if (!resolvedOptions.DryRun)
            {
                new ProjectScaffold(resolvedOptions, extractor.Provider).Generate();
            }

            var writer = new ContentWriter(resolvedOptions, extractor.Provider);
            var manifestLock = new object();
            var processed = 0;
            var total = packages.Count;

            var parallelDegree = resolvedOptions.MaxDegreeOfParallelism > 0
                ? resolvedOptions.MaxDegreeOfParallelism
                : Math.Max(2, Environment.ProcessorCount);

            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelDegree,
                CancellationToken = ct
            };

            try
            {
                Parallel.ForEach(packages, parallelOpts, pkg =>
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var parsed = parser.Parse(pkg);
                        if (parsed is null)
                        {
                            lock (manifestLock)
                            {
                                writer.Manifest.Add(new ManifestEntry
                                {
                                    VirtualPath = pkg.Path,
                                    OutputPath = "(parse failed)",
                                    AssetType = "Unknown",
                                    Fidelity = Fidelity.Failed,
                                    Note = "Package failed to parse"
                                });
                            }
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
                        var current = Interlocked.Increment(ref processed);
                        progress?.Report(new RecoveryProgress(
                            ProcessedCount: current,
                            TotalCount: total,
                            CurrentAsset: pkg.Path,
                            Stage: "Recovering Assets",
                            Percentage: total > 0 ? (double)current / total * 100.0 : 100.0
                        ));
                    }
                });
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Recovery canceled by user.");
            }

            if (!resolvedOptions.DryRun)
            {
                writer.ScaffoldPlugins();
                var scaffold = new Output.ProjectScaffold(resolvedOptions, extractor.Provider);
                if (writer.DiscoveredGameplayTags.Count > 0)
                {
                    scaffold.WriteDefaultGameplayTags(writer.DiscoveredGameplayTags.Keys);
                }
                if (writer.DiscoveredCollisionProfiles.Count > 0 || writer.DiscoveredCollisionChannels.Count > 0)
                {
                    scaffold.WriteDefaultCollision(writer.DiscoveredCollisionProfiles.Keys, writer.DiscoveredCollisionChannels.Keys);
                }
                if (writer.DiscoveredPhysicalSurfaces.Count > 0)
                {
                    scaffold.WriteDefaultPhysics(writer.DiscoveredPhysicalSurfaces);
                }
                if (resolvedOptions.EmitStubs)
                {
                    new StubModuleGenerator(null).Generate(resolvedOptions.OutputRoot, writer.GameStubs.Values.ToList(), writer.StubBaseHints);
                }
            }

            sw.Stop();

            var report = new RecoveryReport
            {
                InputPath = inputPath,
                OutputRoot = safeOutput,
                Engine = association,
                ElapsedTime = sw.Elapsed,
                TotalAssets = writer.Manifest.Count,
                RecoveredCount = writer.Manifest.Count(m => m.Fidelity is Fidelity.Full or Fidelity.Copied),
                PartialCount = writer.Manifest.Count(m => m.Fidelity == Fidelity.Partial),
                StubCount = writer.Manifest.Count(m => m.Fidelity == Fidelity.Stub),
                SkippedCount = writer.Manifest.Count(m => m.Note != null && m.Note.Contains("skipped", StringComparison.OrdinalIgnoreCase)),
                FailedCount = writer.Manifest.Count(m => m.Fidelity == Fidelity.Failed),
                Manifest = writer.Manifest.ToList()
            };

            foreach (var g in writer.Manifest.GroupBy(m => m.Fidelity))
            {
                report.ByFidelity[g.Key.ToString()] = g.Count();
            }

            foreach (var g in writer.Manifest.GroupBy(m => m.AssetType))
            {
                report.ByAssetClass[g.Key] = g.Count();
            }

            if (!resolvedOptions.DryRun)
            {
                var reportPath = Path.Combine(safeOutput, "decompile-report.json");
                File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            }

            return report;
        }, ct);
    }

    public async Task<ValidationReport> ValidateAsync(string outputPath, CancellationToken ct = default)
    {
        return await _validationService.ValidateDirectoryAsync(outputPath, EGame.GAME_UE4_21, ct);
    }

    public async Task<DiagnosticReport> RunDoctorAsync(string? inputPath = null, string? outputPath = null, CancellationToken ct = default)
    {
        return await _diagnosticsService.RunDiagnosticsAsync(inputPath, outputPath, ct);
    }

    public CapabilityMatrix GetCapabilities()
    {
        var engines = new List<EngineCapabilityEntry>
        {
            new("UE 4.10 - 4.20", true, false, "Read supported. Recovery to JSON IR + glTF/PNG media. Binary writer targeted at 4.21+."),
            new("UE 4.21 - 4.25", true, true, "Full native read and 4.21 uncooked package writing (.uasset + FPackageFileSummary + export tables)."),
            new("UE 4.26 - 4.27", true, true, "Full native read with .usmap mapping support. Uncooked packages written with 4.21 structure."),
            new("UE 5.0 - 5.1", true, false, "Full IoStore/Zen Loader read. Recovers to high-fidelity JSON IR, glTF meshes, PNG textures, and Unreal Editor JsonAssetImport assets."),
            new("UE 5.2 - 5.5", true, false, "Full IoStore/Zen Loader read with Enhanced Input, Chaos, Virtual Textures. Recovers to JSON IR + decoded media.")
        };

        var containers = new List<ContainerCapabilityEntry>
        {
            new("Standard .pak", true, "Unreal Engine 4 and 5 PAK containers with Oodle/Zlib/LZ4 compression."),
            new("IoStore (.utoc / .ucas)", true, "Unreal Engine 4.25+ and UE5 IoStore containers with Zen package format."),
            new("AES-256 Encrypted Containers", true, "Single or multi-GUID AES-256 encrypted PAK and IoStore containers."),
            new("Unversioned Packages (.usmap)", true, "Supports loading .usmap mapping files to resolve unversioned struct/class property schemas.")
        };

        var assets = new List<AssetCapabilityEntry>
        {
            new("UBlueprint / *BlueprintGeneratedClass", AssetRecoveryCapability.Recovered, "Kismet bytecode walker, pseudo-C++, Graphviz DOT, Mermaid diagrams, template synthesis", ".uasset, .json, .dot, .mmd"),
            new("UStaticMesh", AssetRecoveryCapability.Recovered, "Decoded to glTF 2.0 (.glb) with LODs and materials; editor-openable FRawMesh package", ".glb, .uasset, .json"),
            new("USkeletalMesh", AssetRecoveryCapability.Recovered, "Decoded to glTF 2.0 (.glb) with bones, weights, and morph targets", ".glb, .json"),
            new("UTexture2D / UTextureCube", AssetRecoveryCapability.Recovered, "Decoded to PNG; uncooked package writer for editor textures", ".png, .uasset, .json"),
            new("UMaterial / UMaterialInstanceConstant", AssetRecoveryCapability.PartiallyRecovered, "Full expression tree and parameter graph in JSON IR; HLSL shader export", ".json, .hlsl, .uasset"),
            new("UWorld / ULevel Lighting & Rig", AssetRecoveryCapability.Recovered, "Full environmental lighting (Sun, Sky, Lights, Volumetric Fog, Atmosphere, PostProcess) + Unreal Editor Python rebuild script", ".umap, .json, _reconstruct.py, _lighting.json"),
            new("USoundWave / USoundCue", AssetRecoveryCapability.Recovered, "Decompresses cooked ADPCM/Vorbis/PCM streams directly to playable audio files", ".wav, .ogg, .json"),
            new("UDataTable / UStringTable / UCurveTable", AssetRecoveryCapability.Recovered, "Decodes rows and tables to editor-compliant RFC4180 CSV and JSON with FTableRowBase C++ stubs", ".csv, .json"),
            new("USkeleton / UAnimSequence / UAnimMontage", AssetRecoveryCapability.Recovered, "Bone hierarchies, sockets, anim notify timelines, montage sections, and editor Python socket setup scripts", ".json, _sockets.py"),
            new("UInputAction / UInputMappingContext", AssetRecoveryCapability.Recovered, "Enhanced Input axis types, player mappable settings, triggers, modifiers, and key bindings", ".json, _setup.py"),
            new("Gameplay Tags & Project Config", AssetRecoveryCapability.Recovered, "Harvests all discovered GameplayTags into DefaultGameplayTags.ini, configures EnhancedInput & Python plugins", ".ini, .py"),
            new("Game Native C++ Stubs", AssetRecoveryCapability.Recovered, "Generates compilable C++ modules (.Build.cs, .h, .cpp) for referenced game classes", ".h, .cpp, .cs")
        };

        return new CapabilityMatrix
        {
            Engines = engines,
            Containers = containers,
            Assets = assets
        };
    }
}
