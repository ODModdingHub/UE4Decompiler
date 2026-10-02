using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs a level (.umap) from its <see cref="UWorld"/> / <see cref="ULevel"/> exports.
/// Enumerates every <c>AActor</c>, its components, and transform properties, and preserves
/// streaming-level references as soft object paths so the world graph stays intact on re-import.
/// </summary>
public sealed class LevelReconstructor
{
    public ReconstructionResult Reconstruct(ParsedAsset asset, string outputPathNoExt)
    {
        try
        {
            var world = asset.Exports.OfType<UWorld>().FirstOrDefault();
            var level = asset.Exports.OfType<ULevel>().FirstOrDefault();

            if (world is null && level is null)
                return ReconstructionResult.Failed("No UWorld/ULevel export found");

            // Prefer the persistent level referenced by the world; fall back to any ULevel export.
            if (level is null && world?.PersistentLevel is { } pl && pl.TryLoad(out var loaded) && loaded is ULevel ul)
                level = ul;

            var actors = new List<object>();
            if (level?.Actors is { } actorRefs)
            {
                foreach (var actorRef in actorRefs)
                {
                    if (actorRef is null || !actorRef.TryLoad(out var actor) || actor is null) continue;
                    actors.Add(BuildActor(actor));
                }
            }

            var streaming = new List<string?>();
            if (world?.StreamingLevels is { } sls)
            {
                foreach (var s in sls)
                    if (s.TryLoad(out var sl) && sl is not null)
                        streaming.Add(sl.GetOrDefault<FSoftObjectPath>("WorldAsset").ToString() ?? sl.Name);
            }

            var lightingActors = new List<object>();
            foreach (var a in actors)
            {
                var cls = a.GetType().GetProperty("Class")?.GetValue(a)?.ToString() ?? "";
                if (cls.Contains("Light", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("Fog", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("Atmosphere", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("PostProcess", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("Cloud", StringComparison.OrdinalIgnoreCase))
                {
                    lightingActors.Add(a);
                }
            }

            var model = new
            {
                AssetType = "World",
                asset.File.Path,
                ActorCount = actors.Count,
                LightingActorCount = lightingActors.Count,
                LightingActors = lightingActors,
                Actors = actors,
                StreamingLevels = streaming
            };

            Log.Information("Level {Name}: {Count} actor(s) ({Lights} lighting), {Streaming} streaming level ref(s)",
                Path.GetFileName(asset.File.Path), actors.Count, lightingActors.Count, streaming.Count);

            return new ReconstructionResult
            {
                Fidelity = Fidelity.Partial,
                Note = $"{actors.Count} actors placed ({lightingActors.Count} lighting), {streaming.Count} streaming refs preserved",
                Model = model
            };
        }
        catch (Exception ex)
        {
            return ReconstructionResult.Failed($"Level reconstruction error: {ex.Message}");
        }
    }

    private static object BuildActor(UObject actor)
    {
        // The actor's transform lives on its RootComponent; capture both the actor's properties
        // and any component sub-objects we can resolve for placement fidelity.
        var components = new List<object>();
        var seenCompNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. RootComponent
        if (actor.GetOrDefault<FPackageIndex>("RootComponent") is { } rcIndex &&
            rcIndex.TryLoad(out var rootComp) && rootComp is not null)
        {
            seenCompNames.Add(rootComp.Name);
            components.Add(new { rootComp.Name, Class = rootComp.ExportType, IsRoot = true, Properties = rootComp.Properties });
        }

        // 2. BlueprintCreatedComponents
        if (actor.GetOrDefault<FPackageIndex[]>("BlueprintCreatedComponents") is { } bcc)
        {
            foreach (var c in bcc)
            {
                if (c.TryLoad(out var comp) && comp is not null && seenCompNames.Add(comp.Name))
                    components.Add(new { comp.Name, Class = comp.ExportType, IsRoot = false, Properties = comp.Properties });
            }
        }

        // 3. Known native light/scene/fog components
        var candidateProps = new[] { "LightComponent", "SkyLightComponent", "Component", "PostProcessComponent" };
        foreach (var p in candidateProps)
        {
            if (actor.GetOrDefault<FPackageIndex>(p) is { } pi &&
                pi.TryLoad(out var sc) && sc is not null && seenCompNames.Add(sc.Name))
            {
                components.Add(new { sc.Name, Class = sc.ExportType, IsRoot = false, Properties = sc.Properties });
            }
        }

        return new
        {
            actor.Name,
            Class = actor.ExportType,
            RootComponent = actor.GetOrDefault<FPackageIndex>("RootComponent")?.ResolvedObject?.GetPathName(),
            ComponentCount = components.Count,
            Components = components,
            Properties = actor.Properties
        };
    }
}
