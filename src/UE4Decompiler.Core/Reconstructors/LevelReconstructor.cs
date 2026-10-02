using System.Globalization;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using UE4Decompiler.Core;

namespace UE4Decompiler.Reconstructors;

/// <summary>
/// Reconstructs a level (.umap) from its <see cref="UWorld"/> / <see cref="ULevel"/> exports.
/// Extracts full environmental lighting (Sun DirectionalLight, SkyLight, Point/Spot/Rect lights,
/// ExponentialHeightFog, SkyAtmosphere, PostProcessVolumes), streaming level references, and
/// placed actor transform hierarchies.
/// Emits structured lighting metadata JSON and an automated Unreal Engine Python script
/// (&lt;MapName&gt;_reconstruct.py) that recreates lighting and actor placements in the Unreal Editor.
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

            var rawActors = new List<UObject>();
            if (level?.Actors is { } actorRefs)
            {
                foreach (var actorRef in actorRefs)
                {
                    if (actorRef is null || !actorRef.TryLoad(out var actor) || actor is null) continue;
                    rawActors.Add(actor);
                }
            }

            var streaming = new List<string?>();
            if (world?.StreamingLevels is { } sls)
            {
                foreach (var s in sls)
                    if (s.TryLoad(out var sl) && sl is not null)
                        streaming.Add(sl.GetOrDefault<FSoftObjectPath>("WorldAsset").ToString() ?? sl.Name);
            }

            // Extract all placed actors and lighting environment
            var actors = new List<LevelActorData>();
            var lighting = new LevelLightingEnvironment();

            foreach (var a in rawActors)
            {
                var actorData = BuildActorData(a, asset.Exports);
                if (actorData != null)
                {
                    actors.Add(actorData);
                    CategorizeLighting(actorData, lighting);
                }
            }

            var mapName = Path.GetFileNameWithoutExtension(asset.File.Path);
            var dir = Path.GetDirectoryName(outputPathNoExt);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var sidecars = new List<string>();

            // 1. Emit structured lighting JSON sidecar
            var lightingJsonPath = outputPathNoExt + "_lighting.json";
            var lightingModel = new
            {
                MapName = mapName,
                VirtualPath = asset.File.Path,
                TotalActors = actors.Count,
                LightingSummary = new
                {
                    DirectionalLights = lighting.DirectionalLights.Count,
                    SkyLights = lighting.SkyLights.Count,
                    PointLights = lighting.PointLights.Count,
                    SpotLights = lighting.SpotLights.Count,
                    RectLights = lighting.RectLights.Count,
                    HeightFog = lighting.HeightFogs.Count,
                    SkyAtmosphere = lighting.SkyAtmospheres.Count,
                    PostProcessVolumes = lighting.PostProcessVolumes.Count,
                    Decals = lighting.Decals.Count,
                    AmbientSounds = lighting.AmbientSounds.Count,
                    Cameras = lighting.Cameras.Count
                },
                Lighting = lighting
            };

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(lightingJsonPath, JsonSerializer.Serialize(lightingModel, jsonOptions));
            sidecars.Add(lightingJsonPath);

            // 2. Emit automated Unreal Editor Python reconstruction script
            var pythonScriptPath = outputPathNoExt + "_reconstruct.py";
            var pyScript = GenerateUnrealPythonScript(mapName, asset.File.Path, lighting, actors);
            File.WriteAllText(pythonScriptPath, pyScript, Encoding.UTF8);
            sidecars.Add(pythonScriptPath);

            var model = new
            {
                AssetType = "World",
                asset.File.Path,
                ActorCount = actors.Count,
                LightingActorCount = lighting.TotalLightingCount,
                Lighting = lightingModel.LightingSummary,
                StreamingLevels = streaming,
                Actors = actors.Select(a => new
                {
                    a.Name,
                    a.Class,
                    a.Location,
                    a.Rotation,
                    a.Scale,
                    a.MeshPath,
                    MaterialCount = a.OverrideMaterials.Count
                }).ToList()
            };

            Log.Information("Level {Name}: {Count} actor(s) ({Lights} lighting), {Streaming} streaming level ref(s). Emitted lighting JSON & Python reconstruct script.",
                mapName, actors.Count, lighting.TotalLightingCount, streaming.Count);

            return new ReconstructionResult
            {
                Fidelity = Fidelity.Full,
                Note = $"{actors.Count} actors placed ({lighting.TotalLightingCount} lighting rigs recovered); Python rebuild script & lighting JSON emitted",
                Model = model,
                SidecarFiles = sidecars
            };
        }
        catch (Exception ex)
        {
            return ReconstructionResult.Failed($"Level reconstruction error: {ex.Message}");
        }
    }

    private static LevelActorData? BuildActorData(UObject actor, IReadOnlyList<UObject> allExports)
    {
        var cls = actor.ExportType;
        if (cls is "Model" or "Brush" or "Polys" or "Level" or "World" or "WorldSettings" or "None")
            return null;

        var components = allExports.Where(c => c.Outer?.Name.Text == actor.Name && c.ExportType.EndsWith("Component")).ToList();

        var rootRef = actor.GetOrDefault<FPackageIndex>("RootComponent")?.ResolvedObject;
        var rootComp = (rootRef != null ? components.FirstOrDefault(c => c.Name == rootRef.Name) : null)
                       ?? components.FirstOrDefault();

        var loc = ReadVector(rootComp, "RelativeLocation", 0f, 0f, 0f);
        var rot = ReadRotator(rootComp, "RelativeRotation", 0f, 0f, 0f);
        var scl = ReadVector(rootComp, "RelativeScale3D", 1f, 1f, 1f);

        // Find primary visual/light component
        var meshComp = components.FirstOrDefault(c => c.ExportType.Contains("StaticMesh")) ?? rootComp;
        string? meshPath = null;
        if (meshComp != null)
        {
            var sm = meshComp.GetOrDefault<FPackageIndex>("StaticMesh")?.ResolvedObject;
            meshPath = sm?.GetPathName();
        }

        var overrideMats = new List<string>();
        if (meshComp != null)
        {
            var omArr = meshComp.GetOrDefault<FPackageIndex[]>("OverrideMaterials");
            if (omArr != null)
            {
                foreach (var om in omArr)
                {
                    var ro = om?.ResolvedObject;
                    if (ro != null && !string.IsNullOrEmpty(ro.GetPathName()))
                        overrideMats.Add(ro.GetPathName());
                }
            }
        }

        return new LevelActorData
        {
            Name = actor.Name,
            Class = cls,
            Location = loc,
            Rotation = rot,
            Scale = scl,
            MeshPath = meshPath,
            OverrideMaterials = overrideMats,
            RawActor = actor,
            Components = components,
            PrimaryComponent = meshComp ?? rootComp
        };
    }

    private static void CategorizeLighting(LevelActorData actor, LevelLightingEnvironment env)
    {
        var cls = actor.Class;
        var comp = actor.PrimaryComponent;
        var compCls = comp?.ExportType ?? "";

        // Directional Light (Sun)
        if (cls.Contains("DirectionalLight", StringComparison.OrdinalIgnoreCase) ||
            compCls.Contains("DirectionalLightComponent", StringComparison.OrdinalIgnoreCase))
        {
            var light = new DirectionalLightData
            {
                ActorName = actor.Name,
                Location = actor.Location,
                Rotation = actor.Rotation,
                Intensity = ReadFloat(comp, "Intensity", 10.0f),
                LightColor = ReadColor(comp, "LightColor", 255, 255, 255),
                Temperature = ReadFloat(comp, "Temperature", 6500f),
                UseTemperature = ReadBool(comp, "bUseTemperature", false),
                LightSourceAngle = ReadFloat(comp, "LightSourceAngle", 0.5357f),
                LightSourceSoftAngle = ReadFloat(comp, "LightSourceSoftAngle", 0.0f),
                AtmosphereSunLightIndex = ReadInt(comp, "AtmosphereSunLightIndex", 0),
                UsedAsAtmosphereSunLight = ReadBool(comp, "bUsedAsAtmosphereSunLight", true),
                CastShadows = ReadBool(comp, "CastShadows", true),
                CastVolumetricShadow = ReadBool(comp, "bCastVolumetricShadow", true),
                VolumetricScatteringIntensity = ReadFloat(comp, "VolumetricScatteringIntensity", 1.0f),
                IndirectLightingIntensity = ReadFloat(comp, "IndirectLightingIntensity", 1.0f)
            };
            env.DirectionalLights.Add(light);
            return;
        }

        // Sky Light
        if (cls.Contains("SkyLight", StringComparison.OrdinalIgnoreCase) ||
            compCls.Contains("SkyLightComponent", StringComparison.OrdinalIgnoreCase))
        {
            var sky = new SkyLightData
            {
                ActorName = actor.Name,
                Location = actor.Location,
                Intensity = ReadFloat(comp, "Intensity", 1.0f),
                LightColor = ReadColor(comp, "LightColor", 255, 255, 255),
                LowerHemisphereColor = ReadColor(comp, "LowerHemisphereColor", 0, 0, 0),
                LowerHemisphereIsBlack = ReadBool(comp, "bLowerHemisphereIsBlack", true),
                Cubemap = comp?.GetOrDefault<FPackageIndex>("Cubemap")?.ResolvedObject?.GetPathName()
                          ?? comp?.GetOrDefault<FSoftObjectPath>("Cubemap").ToString(),
                SourceType = comp?.GetOrDefault<FName>("SourceType").Text ?? "SLS_CapturedScene",
                CastShadows = ReadBool(comp, "CastShadows", true)
            };
            env.SkyLights.Add(sky);
            return;
        }

        // Point Light
        if (cls.Contains("PointLight", StringComparison.OrdinalIgnoreCase) ||
            compCls.Contains("PointLightComponent", StringComparison.OrdinalIgnoreCase))
        {
            var pl = new PointLightData
            {
                ActorName = actor.Name,
                Location = actor.Location,
                Rotation = actor.Rotation,
                Intensity = ReadFloat(comp, "Intensity", 5000.0f),
                LightColor = ReadColor(comp, "LightColor", 255, 255, 255),
                AttenuationRadius = ReadFloat(comp, "AttenuationRadius", 1000.0f),
                SourceRadius = ReadFloat(comp, "SourceRadius", 0.0f),
                SoftSourceRadius = ReadFloat(comp, "SoftSourceRadius", 0.0f),
                SourceLength = ReadFloat(comp, "SourceLength", 0.0f),
                CastShadows = ReadBool(comp, "CastShadows", true),
                Temperature = ReadFloat(comp, "Temperature", 6500f),
                UseTemperature = ReadBool(comp, "bUseTemperature", false)
            };
            env.PointLights.Add(pl);
            return;
        }

        // Spot Light
        if (cls.Contains("SpotLight", StringComparison.OrdinalIgnoreCase) ||
            compCls.Contains("SpotLightComponent", StringComparison.OrdinalIgnoreCase))
        {
            var sl = new SpotLightData
            {
                ActorName = actor.Name,
                Location = actor.Location,
                Rotation = actor.Rotation,
                Intensity = ReadFloat(comp, "Intensity", 5000.0f),
                LightColor = ReadColor(comp, "LightColor", 255, 255, 255),
                AttenuationRadius = ReadFloat(comp, "AttenuationRadius", 1000.0f),
                InnerConeAngle = ReadFloat(comp, "InnerConeAngle", 0.0f),
                OuterConeAngle = ReadFloat(comp, "OuterConeAngle", 44.0f),
                CastShadows = ReadBool(comp, "CastShadows", true)
            };
            env.SpotLights.Add(sl);
            return;
        }

        // Rect Light
        if (cls.Contains("RectLight", StringComparison.OrdinalIgnoreCase) ||
            compCls.Contains("RectLightComponent", StringComparison.OrdinalIgnoreCase))
        {
            var rl = new RectLightData
            {
                ActorName = actor.Name,
                Location = actor.Location,
                Rotation = actor.Rotation,
                Intensity = ReadFloat(comp, "Intensity", 5000.0f),
                LightColor = ReadColor(comp, "LightColor", 255, 255, 255),
                AttenuationRadius = ReadFloat(comp, "AttenuationRadius", 1000.0f),
                SourceWidth = ReadFloat(comp, "SourceWidth", 64.0f),
                SourceHeight = ReadFloat(comp, "SourceHeight", 64.0f),
                BarnDoorAngle = ReadFloat(comp, "BarnDoorAngle", 88.0f),
                CastShadows = ReadBool(comp, "CastShadows", true)
            };
            env.RectLights.Add(rl);
            return;
        }

        // Exponential Height Fog
        if (cls.Contains("Fog", StringComparison.OrdinalIgnoreCase) ||
            compCls.Contains("ExponentialHeightFogComponent", StringComparison.OrdinalIgnoreCase))
        {
            var fog = new HeightFogData
            {
                ActorName = actor.Name,
                Location = actor.Location,
                FogDensity = ReadFloat(comp, "FogDensity", 0.02f),
                FogHeightFalloff = ReadFloat(comp, "FogHeightFalloff", 0.2f),
                FogInscatteringColor = ReadColor(comp, "FogInscatteringColor", 114, 150, 204),
                FogMaxOpacity = ReadFloat(comp, "FogMaxOpacity", 1.0f),
                StartDistance = ReadFloat(comp, "StartDistance", 0.0f),
                EnableVolumetricFog = ReadBool(comp, "bEnableVolumetricFog", true),
                VolumetricFogScatteringDistribution = ReadFloat(comp, "VolumetricFogScatteringDistribution", 0.2f),
                VolumetricFogExtinctionScale = ReadFloat(comp, "VolumetricFogExtinctionScale", 1.0f),
                VolumetricFogDistance = ReadFloat(comp, "VolumetricFogDistance", 6000.0f)
            };
            env.HeightFogs.Add(fog);
            return;
        }

        // Sky Atmosphere
        if (cls.Contains("Atmosphere", StringComparison.OrdinalIgnoreCase) ||
            compCls.Contains("SkyAtmosphereComponent", StringComparison.OrdinalIgnoreCase))
        {
            var atmo = new SkyAtmosphereData
            {
                ActorName = actor.Name,
                Location = actor.Location,
                BottomRadius = ReadFloat(comp, "BottomRadius", 6360.0f),
                AtmosphereHeight = ReadFloat(comp, "AtmosphereHeight", 60.0f),
                MultiScatteringFactor = ReadFloat(comp, "MultiScatteringFactor", 1.0f),
                RayleighScatteringScale = ReadFloat(comp, "RayleighScatteringScale", 0.0331f)
            };
            env.SkyAtmospheres.Add(atmo);
            return;
        }

        // Post Process Volume
        if (cls.Contains("PostProcess", StringComparison.OrdinalIgnoreCase) ||
            compCls.Contains("PostProcessComponent", StringComparison.OrdinalIgnoreCase))
        {
            var pp = new PostProcessData
            {
                ActorName = actor.Name,
                Location = actor.Location,
                Unbound = ReadBool(actor.RawActor, "bUnbound", ReadBool(comp, "bUnbound", true)),
                Priority = ReadFloat(actor.RawActor, "Priority", ReadFloat(comp, "Priority", 1.0f)),
                BlendWeight = ReadFloat(actor.RawActor, "BlendWeight", ReadFloat(comp, "BlendWeight", 1.0f))
            };
            env.PostProcessVolumes.Add(pp);
            return;
        }

        // Decal Actor
        if (cls.Contains("Decal", StringComparison.OrdinalIgnoreCase) ||
            compCls.Contains("DecalComponent", StringComparison.OrdinalIgnoreCase))
        {
            var decalMat = comp?.GetOrDefault<FPackageIndex>("DecalMaterial")?.ResolvedObject?.GetPathName();
            var decalSize = ReadVector(comp, "DecalSize", 128f, 256f, 256f);
            env.Decals.Add(new DecalActorData
            {
                ActorName = actor.Name,
                Location = actor.Location,
                Rotation = actor.Rotation,
                Scale = actor.Scale,
                DecalMaterial = decalMat,
                DecalSize = decalSize
            });
            return;
        }

        // Ambient Sound
        if (cls.Contains("AmbientSound", StringComparison.OrdinalIgnoreCase) ||
            compCls.Contains("AudioComponent", StringComparison.OrdinalIgnoreCase))
        {
            var soundPath = comp?.GetOrDefault<FPackageIndex>("Sound")?.ResolvedObject?.GetPathName();
            var volume = ReadFloat(comp, "VolumeMultiplier", 1.0f);
            var pitch = ReadFloat(comp, "PitchMultiplier", 1.0f);
            env.AmbientSounds.Add(new AmbientSoundData
            {
                ActorName = actor.Name,
                Location = actor.Location,
                SoundPath = soundPath,
                VolumeMultiplier = volume,
                PitchMultiplier = pitch
            });
            return;
        }

        // Camera Actor
        if (cls.Contains("CameraActor", StringComparison.OrdinalIgnoreCase) ||
            compCls.Contains("CameraComponent", StringComparison.OrdinalIgnoreCase))
        {
            var fov = ReadFloat(comp, "FieldOfView", 90.0f);
            var aspect = ReadFloat(comp, "AspectRatio", 1.777778f);
            env.Cameras.Add(new CameraActorData
            {
                ActorName = actor.Name,
                Location = actor.Location,
                Rotation = actor.Rotation,
                FieldOfView = fov,
                AspectRatio = aspect
            });
            return;
        }
    }

    private static string GenerateUnrealPythonScript(string mapName, string virtualPath, LevelLightingEnvironment lighting, List<LevelActorData> actors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine($"# Unreal Engine Level Reconstruction Script for: {mapName}");
        sb.AppendLine($"# Source Package: {virtualPath}");
        sb.AppendLine("# Generated by UE4Decompiler High-Fidelity Asset Recovery Suite");
        sb.AppendLine("#");
        sb.AppendLine("# INSTRUCTIONS:");
        sb.AppendLine("# 1. Open the project in Unreal Editor (UE4 or UE5).");
        sb.AppendLine("# 2. Open or create the target level map.");
        sb.AppendLine("# 3. In Unreal Editor, open Output Log -> switch command line to Python.");
        sb.AppendLine($"# 4. Run: py \"{mapName}_reconstruct.py\"");
        sb.AppendLine("# ===========================================================================");
        sb.AppendLine("import unreal");
        sb.AppendLine();
        sb.AppendLine($"def reconstruct_{SanitizePy(mapName)}():");
        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Starting level reconstruction: {mapName}')");
        sb.AppendLine("    actor_sub = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)");
        sb.AppendLine("    asset_sub = unreal.get_editor_subsystem(unreal.EditorAssetSubsystem)");
        sb.AppendLine("    if not actor_sub:");
        sb.AppendLine("        unreal.log_error('EditorActorSubsystem not available!')");
        sb.AppendLine("        return");
        sb.AppendLine();

        // 1. Directional Lights (Sun)
        if (lighting.DirectionalLights.Count > 0)
        {
            sb.AppendLine("    # -------------------------------------------------------------");
            sb.AppendLine("    # 1. Directional Lights (Sun / Key Light)");
            sb.AppendLine("    # -------------------------------------------------------------");
            foreach (var sun in lighting.DirectionalLights)
            {
                sb.AppendLine("    try:");
                sb.AppendLine($"        loc = unreal.Vector({sun.Location[0]:F2}, {sun.Location[1]:F2}, {sun.Location[2]:F2})");
                sb.AppendLine($"        rot = unreal.Rotator({sun.Rotation[0]:F2}, {sun.Rotation[1]:F2}, {sun.Rotation[2]:F2})");
                sb.AppendLine("        sun_actor = actor_sub.spawn_actor_from_class(unreal.DirectionalLight, loc, rot)");
                sb.AppendLine($"        sun_actor.set_actor_label('{sun.ActorName}')");
                sb.AppendLine("        comp = sun_actor.get_component_by_class(unreal.DirectionalLightComponent)");
                sb.AppendLine("        if comp:");
                sb.AppendLine($"            comp.set_intensity({sun.Intensity.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_light_color(unreal.LinearColor({sun.LightColor[0] / 255f:F3}, {sun.LightColor[1] / 255f:F3}, {sun.LightColor[2] / 255f:F3}, 1.0))");
                sb.AppendLine($"            comp.set_editor_property('LightSourceAngle', {sun.LightSourceAngle.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_editor_property('bUsedAsAtmosphereSunLight', {PyBool(sun.UsedAsAtmosphereSunLight)})");
                sb.AppendLine($"            comp.set_editor_property('AtmosphereSunLightIndex', {sun.AtmosphereSunLightIndex})");
                sb.AppendLine($"            comp.set_editor_property('CastShadows', {PyBool(sun.CastShadows)})");
                sb.AppendLine($"            comp.set_editor_property('VolumetricScatteringIntensity', {sun.VolumetricScatteringIntensity.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_editor_property('IndirectLightingIntensity', {sun.IndirectLightingIntensity.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_mobility(unreal.ComponentMobility.MOVABLE)");
                sb.AppendLine($"        unreal.log('Placed DirectionalLight: {sun.ActorName}')");
                sb.AppendLine("    except Exception as ex:");
                sb.AppendLine($"        unreal.log_warning(f'Failed to setup DirectionalLight {sun.ActorName}: {{ex}}')");
                sb.AppendLine();
            }
        }

        // 2. Sky Light
        if (lighting.SkyLights.Count > 0)
        {
            sb.AppendLine("    # -------------------------------------------------------------");
            sb.AppendLine("    # 2. Sky Light & Ambient Environment");
            sb.AppendLine("    # -------------------------------------------------------------");
            foreach (var sky in lighting.SkyLights)
            {
                sb.AppendLine("    try:");
                sb.AppendLine($"        loc = unreal.Vector({sky.Location[0]:F2}, {sky.Location[1]:F2}, {sky.Location[2]:F2})");
                sb.AppendLine("        sky_actor = actor_sub.spawn_actor_from_class(unreal.SkyLight, loc)");
                sb.AppendLine($"        sky_actor.set_actor_label('{sky.ActorName}')");
                sb.AppendLine("        comp = sky_actor.get_component_by_class(unreal.SkyLightComponent)");
                sb.AppendLine("        if comp:");
                sb.AppendLine($"            comp.set_intensity({sky.Intensity.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_light_color(unreal.LinearColor({sky.LightColor[0] / 255f:F3}, {sky.LightColor[1] / 255f:F3}, {sky.LightColor[2] / 255f:F3}, 1.0))");
                sb.AppendLine($"            comp.set_editor_property('bLowerHemisphereIsBlack', {PyBool(sky.LowerHemisphereIsBlack)})");
                sb.AppendLine($"            comp.set_editor_property('CastShadows', {PyBool(sky.CastShadows)})");
                sb.AppendLine($"            comp.set_mobility(unreal.ComponentMobility.MOVABLE)");
                sb.AppendLine($"        unreal.log('Placed SkyLight: {sky.ActorName}')");
                sb.AppendLine("    except Exception as ex:");
                sb.AppendLine($"        unreal.log_warning(f'Failed to setup SkyLight {sky.ActorName}: {{ex}}')");
                sb.AppendLine();
            }
        }

        // 3. Exponential Height Fog
        if (lighting.HeightFogs.Count > 0)
        {
            sb.AppendLine("    # -------------------------------------------------------------");
            sb.AppendLine("    # 3. Exponential Height Fog & Volumetric Fog");
            sb.AppendLine("    # -------------------------------------------------------------");
            foreach (var fog in lighting.HeightFogs)
            {
                sb.AppendLine("    try:");
                sb.AppendLine($"        loc = unreal.Vector({fog.Location[0]:F2}, {fog.Location[1]:F2}, {fog.Location[2]:F2})");
                sb.AppendLine("        fog_actor = actor_sub.spawn_actor_from_class(unreal.ExponentialHeightFog, loc)");
                sb.AppendLine($"        fog_actor.set_actor_label('{fog.ActorName}')");
                sb.AppendLine("        comp = fog_actor.get_component_by_class(unreal.ExponentialHeightFogComponent)");
                sb.AppendLine("        if comp:");
                sb.AppendLine($"            comp.set_fog_density({fog.FogDensity.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_fog_height_falloff({fog.FogHeightFalloff.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_fog_inscattering_color(unreal.LinearColor({fog.FogInscatteringColor[0] / 255f:F3}, {fog.FogInscatteringColor[1] / 255f:F3}, {fog.FogInscatteringColor[2] / 255f:F3}, 1.0))");
                sb.AppendLine($"            comp.set_editor_property('bEnableVolumetricFog', {PyBool(fog.EnableVolumetricFog)})");
                sb.AppendLine($"            comp.set_editor_property('VolumetricFogScatteringDistribution', {fog.VolumetricFogScatteringDistribution.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_editor_property('VolumetricFogDistance', {fog.VolumetricFogDistance.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"        unreal.log('Placed ExponentialHeightFog: {fog.ActorName}')");
                sb.AppendLine("    except Exception as ex:");
                sb.AppendLine($"        unreal.log_warning(f'Failed to setup HeightFog {fog.ActorName}: {{ex}}')");
                sb.AppendLine();
            }
        }

        // 4. Sky Atmosphere
        if (lighting.SkyAtmospheres.Count > 0)
        {
            sb.AppendLine("    # -------------------------------------------------------------");
            sb.AppendLine("    # 4. Sky Atmosphere");
            sb.AppendLine("    # -------------------------------------------------------------");
            foreach (var atmo in lighting.SkyAtmospheres)
            {
                sb.AppendLine("    try:");
                sb.AppendLine($"        loc = unreal.Vector({atmo.Location[0]:F2}, {atmo.Location[1]:F2}, {atmo.Location[2]:F2})");
                sb.AppendLine("        atmo_actor = actor_sub.spawn_actor_from_class(unreal.SkyAtmosphere, loc)");
                sb.AppendLine($"        atmo_actor.set_actor_label('{atmo.ActorName}')");
                sb.AppendLine("        comp = atmo_actor.get_component_by_class(unreal.SkyAtmosphereComponent)");
                sb.AppendLine("        if comp:");
                sb.AppendLine($"            comp.set_editor_property('BottomRadius', {atmo.BottomRadius.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_editor_property('AtmosphereHeight', {atmo.AtmosphereHeight.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_editor_property('MultiScatteringFactor', {atmo.MultiScatteringFactor.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_editor_property('RayleighScatteringScale', {atmo.RayleighScatteringScale.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"        unreal.log('Placed SkyAtmosphere: {atmo.ActorName}')");
                sb.AppendLine("    except Exception as ex:");
                sb.AppendLine($"        unreal.log_warning(f'Failed to setup SkyAtmosphere {atmo.ActorName}: {{ex}}')");
                sb.AppendLine();
            }
        }

        // 5. Post Process Volumes
        if (lighting.PostProcessVolumes.Count > 0)
        {
            sb.AppendLine("    # -------------------------------------------------------------");
            sb.AppendLine("    # 5. Global Post Process Volumes");
            sb.AppendLine("    # -------------------------------------------------------------");
            foreach (var pp in lighting.PostProcessVolumes)
            {
                sb.AppendLine("    try:");
                sb.AppendLine($"        loc = unreal.Vector({pp.Location[0]:F2}, {pp.Location[1]:F2}, {pp.Location[2]:F2})");
                sb.AppendLine("        pp_actor = actor_sub.spawn_actor_from_class(unreal.PostProcessVolume, loc)");
                sb.AppendLine($"        pp_actor.set_actor_label('{pp.ActorName}')");
                sb.AppendLine($"        pp_actor.set_editor_property('bUnbound', {PyBool(pp.Unbound)})");
                sb.AppendLine($"        pp_actor.set_editor_property('Priority', {pp.Priority.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"        unreal.log('Placed PostProcessVolume: {pp.ActorName}')");
                sb.AppendLine("    except Exception as ex:");
                sb.AppendLine($"        unreal.log_warning(f'Failed to setup PostProcessVolume {pp.ActorName}: {{ex}}')");
                sb.AppendLine();
            }
        }

        // 6. Point, Spot & Rect Lights
        var localLights = lighting.PointLights.Count + lighting.SpotLights.Count + lighting.RectLights.Count;
        if (localLights > 0)
        {
            sb.AppendLine("    # -------------------------------------------------------------");
            sb.AppendLine($"    # 6. Local Lights ({localLights} Point/Spot/Rect lights)");
            sb.AppendLine("    # -------------------------------------------------------------");
            foreach (var pl in lighting.PointLights)
            {
                sb.AppendLine("    try:");
                sb.AppendLine($"        loc = unreal.Vector({pl.Location[0]:F2}, {pl.Location[1]:F2}, {pl.Location[2]:F2})");
                sb.AppendLine($"        rot = unreal.Rotator({pl.Rotation[0]:F2}, {pl.Rotation[1]:F2}, {pl.Rotation[2]:F2})");
                sb.AppendLine("        light_actor = actor_sub.spawn_actor_from_class(unreal.PointLight, loc, rot)");
                sb.AppendLine($"        light_actor.set_actor_label('{pl.ActorName}')");
                sb.AppendLine("        comp = light_actor.get_component_by_class(unreal.PointLightComponent)");
                sb.AppendLine("        if comp:");
                sb.AppendLine($"            comp.set_intensity({pl.Intensity.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_light_color(unreal.LinearColor({pl.LightColor[0] / 255f:F3}, {pl.LightColor[1] / 255f:F3}, {pl.LightColor[2] / 255f:F3}, 1.0))");
                sb.AppendLine($"            comp.set_attenuation_radius({pl.AttenuationRadius.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_source_radius({pl.SourceRadius.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_editor_property('CastShadows', {PyBool(pl.CastShadows)})");
                sb.AppendLine($"            comp.set_mobility(unreal.ComponentMobility.MOVABLE)");
                sb.AppendLine("    except Exception: pass");
            }

            foreach (var sl in lighting.SpotLights)
            {
                sb.AppendLine("    try:");
                sb.AppendLine($"        loc = unreal.Vector({sl.Location[0]:F2}, {sl.Location[1]:F2}, {sl.Location[2]:F2})");
                sb.AppendLine($"        rot = unreal.Rotator({sl.Rotation[0]:F2}, {sl.Rotation[1]:F2}, {sl.Rotation[2]:F2})");
                sb.AppendLine("        light_actor = actor_sub.spawn_actor_from_class(unreal.SpotLight, loc, rot)");
                sb.AppendLine($"        light_actor.set_actor_label('{sl.ActorName}')");
                sb.AppendLine("        comp = light_actor.get_component_by_class(unreal.SpotLightComponent)");
                sb.AppendLine("        if comp:");
                sb.AppendLine($"            comp.set_intensity({sl.Intensity.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_light_color(unreal.LinearColor({sl.LightColor[0] / 255f:F3}, {sl.LightColor[1] / 255f:F3}, {sl.LightColor[2] / 255f:F3}, 1.0))");
                sb.AppendLine($"            comp.set_attenuation_radius({sl.AttenuationRadius.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_inner_cone_angle({sl.InnerConeAngle.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_outer_cone_angle({sl.OuterConeAngle.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            comp.set_editor_property('CastShadows', {PyBool(sl.CastShadows)})");
                sb.AppendLine($"            comp.set_mobility(unreal.ComponentMobility.MOVABLE)");
                sb.AppendLine("    except Exception: pass");
            }
            sb.AppendLine();
        }

        // 7. Static Mesh Actors (Props and Geometry)
        var meshActors = actors.Where(a => !string.IsNullOrEmpty(a.MeshPath)).ToList();
        if (meshActors.Count > 0)
        {
            sb.AppendLine("    # -------------------------------------------------------------");
            sb.AppendLine($"    # 7. Static Mesh Actors ({meshActors.Count} placed props)");
            sb.AppendLine("    # -------------------------------------------------------------");
            sb.AppendLine("    mesh_cache = {}");
            foreach (var a in meshActors)
            {
                sb.AppendLine("    try:");
                sb.AppendLine($"        loc = unreal.Vector({a.Location[0]:F2}, {a.Location[1]:F2}, {a.Location[2]:F2})");
                sb.AppendLine($"        rot = unreal.Rotator({a.Rotation[0]:F2}, {a.Rotation[1]:F2}, {a.Rotation[2]:F2})");
                sb.AppendLine($"        scl = unreal.Vector({a.Scale[0]:F3}, {a.Scale[1]:F3}, {a.Scale[2]:F3})");
                sb.AppendLine("        act = actor_sub.spawn_actor_from_class(unreal.StaticMeshActor, loc, rot)");
                sb.AppendLine($"        act.set_actor_label('{a.Name}')");
                sb.AppendLine("        act.set_actor_scale3d(scl)");
                sb.AppendLine("        sm_comp = act.get_component_by_class(unreal.StaticMeshComponent)");
                sb.AppendLine($"        m_path = '{a.MeshPath}'");
                sb.AppendLine("        if m_path not in mesh_cache:");
                sb.AppendLine("            mesh_cache[m_path] = unreal.EditorAssetLibrary.load_asset(m_path) if asset_sub else None");
                sb.AppendLine("        if sm_comp and mesh_cache.get(m_path):");
                sb.AppendLine("            sm_comp.set_static_mesh(mesh_cache[m_path])");
                if (a.OverrideMaterials.Count > 0)
                {
                    for (int mi = 0; mi < a.OverrideMaterials.Count; mi++)
                    {
                        var matPath = a.OverrideMaterials[mi];
                        sb.AppendLine($"            mat_{mi} = unreal.EditorAssetLibrary.load_asset('{matPath}')");
                        sb.AppendLine($"            if mat_{mi}: sm_comp.set_material({mi}, mat_{mi})");
                    }
                }
                sb.AppendLine("    except Exception: pass");
            }
            sb.AppendLine();
        }

        // 8. Decal Actors
        if (lighting.Decals.Count > 0)
        {
            sb.AppendLine("    # -------------------------------------------------------------");
            sb.AppendLine($"    # 8. Decal Actors ({lighting.Decals.Count} placed decals)");
            sb.AppendLine("    # -------------------------------------------------------------");
            foreach (var d in lighting.Decals)
            {
                sb.AppendLine("    try:");
                sb.AppendLine($"        loc = unreal.Vector({d.Location[0]:F2}, {d.Location[1]:F2}, {d.Location[2]:F2})");
                sb.AppendLine($"        rot = unreal.Rotator({d.Rotation[0]:F2}, {d.Rotation[1]:F2}, {d.Rotation[2]:F2})");
                sb.AppendLine("        decal_act = actor_sub.spawn_actor_from_class(unreal.DecalActor, loc, rot)");
                sb.AppendLine($"        decal_act.set_actor_label('{d.ActorName}')");
                sb.AppendLine("        decal_comp = decal_act.get_component_by_class(unreal.DecalComponent)");
                sb.AppendLine("        if decal_comp:");
                sb.AppendLine($"            decal_comp.set_editor_property('DecalSize', unreal.Vector({d.DecalSize[0]:F2}, {d.DecalSize[1]:F2}, {d.DecalSize[2]:F2}))");
                if (!string.IsNullOrEmpty(d.DecalMaterial))
                {
                    sb.AppendLine($"            dmat = unreal.EditorAssetLibrary.load_asset('{d.DecalMaterial}')");
                    sb.AppendLine("            if dmat: decal_comp.set_decal_material(dmat)");
                }
                sb.AppendLine("    except Exception: pass");
            }
            sb.AppendLine();
        }

        // 9. Ambient Sound Actors
        if (lighting.AmbientSounds.Count > 0)
        {
            sb.AppendLine("    # -------------------------------------------------------------");
            sb.AppendLine($"    # 9. Ambient Sound Actors ({lighting.AmbientSounds.Count} placed audio sources)");
            sb.AppendLine("    # -------------------------------------------------------------");
            foreach (var s in lighting.AmbientSounds)
            {
                sb.AppendLine("    try:");
                sb.AppendLine($"        loc = unreal.Vector({s.Location[0]:F2}, {s.Location[1]:F2}, {s.Location[2]:F2})");
                sb.AppendLine("        snd_act = actor_sub.spawn_actor_from_class(unreal.AmbientSound, loc)");
                sb.AppendLine($"        snd_act.set_actor_label('{s.ActorName}')");
                sb.AppendLine("        audio_comp = snd_act.get_component_by_class(unreal.AudioComponent)");
                sb.AppendLine("        if audio_comp:");
                sb.AppendLine($"            audio_comp.set_volume_multiplier({s.VolumeMultiplier.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            audio_comp.set_pitch_multiplier({s.PitchMultiplier.ToString(CultureInfo.InvariantCulture)})");
                if (!string.IsNullOrEmpty(s.SoundPath))
                {
                    sb.AppendLine($"            snd = unreal.EditorAssetLibrary.load_asset('{s.SoundPath}')");
                    sb.AppendLine("            if snd: audio_comp.set_sound(snd)");
                }
                sb.AppendLine("    except Exception: pass");
            }
            sb.AppendLine();
        }

        // 10. Camera Actors
        if (lighting.Cameras.Count > 0)
        {
            sb.AppendLine("    # -------------------------------------------------------------");
            sb.AppendLine($"    # 10. Camera Actors ({lighting.Cameras.Count} placed cameras)");
            sb.AppendLine("    # -------------------------------------------------------------");
            foreach (var c in lighting.Cameras)
            {
                sb.AppendLine("    try:");
                sb.AppendLine($"        loc = unreal.Vector({c.Location[0]:F2}, {c.Location[1]:F2}, {c.Location[2]:F2})");
                sb.AppendLine($"        rot = unreal.Rotator({c.Rotation[0]:F2}, {c.Rotation[1]:F2}, {c.Rotation[2]:F2})");
                sb.AppendLine("        cam_act = actor_sub.spawn_actor_from_class(unreal.CameraActor, loc, rot)");
                sb.AppendLine($"        cam_act.set_actor_label('{c.ActorName}')");
                sb.AppendLine("        cam_comp = cam_act.get_component_by_class(unreal.CameraComponent)");
                sb.AppendLine("        if cam_comp:");
                sb.AppendLine($"            cam_comp.set_editor_property('FieldOfView', {c.FieldOfView.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine($"            cam_comp.set_editor_property('AspectRatio', {c.AspectRatio.ToString(CultureInfo.InvariantCulture)})");
                sb.AppendLine("    except Exception: pass");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"    unreal.log('>>> [UE4Decompiler] Level reconstruction complete for: {mapName}')");
        sb.AppendLine();
        sb.AppendLine("if __name__ == '__main__':");
        sb.AppendLine($"    reconstruct_{SanitizePy(mapName)}()");
        return sb.ToString();
    }

    private static string SanitizePy(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString().Trim('_');
    }

    private static string PyBool(bool b) => b ? "True" : "False";

    private static float[] ReadVector(UObject? obj, string propName, float defX, float defY, float defZ)
    {
        if (obj == null) return new[] { defX, defY, defZ };
        var vec = obj.GetOrDefault<FVector>(propName);
        if (vec.X != 0 || vec.Y != 0 || vec.Z != 0)
            return new[] { (float)vec.X, (float)vec.Y, (float)vec.Z };
        return new[] { defX, defY, defZ };
    }

    private static float[] ReadRotator(UObject? obj, string propName, float defPitch, float defYaw, float defRoll)
    {
        if (obj == null) return new[] { defPitch, defYaw, defRoll };
        var rot = obj.GetOrDefault<FRotator>(propName);
        if (rot.Pitch != 0 || rot.Yaw != 0 || rot.Roll != 0)
            return new[] { (float)rot.Pitch, (float)rot.Yaw, (float)rot.Roll };
        return new[] { defPitch, defYaw, defRoll };
    }

    private static float ReadFloat(UObject? obj, string propName, float defVal) =>
        obj?.GetOrDefault<float>(propName, defVal) ?? defVal;

    private static int ReadInt(UObject? obj, string propName, int defVal) =>
        obj?.GetOrDefault<int>(propName, defVal) ?? defVal;

    private static bool ReadBool(UObject? obj, string propName, bool defVal) =>
        obj?.GetOrDefault<bool>(propName, defVal) ?? defVal;

    private static byte[] ReadColor(UObject? obj, string propName, byte defR, byte defG, byte defB)
    {
        if (obj == null) return new[] { defR, defG, defB, (byte)255 };
        try
        {
            var col = obj.GetOrDefault<FColor>(propName);
            if (col.R != 0 || col.G != 0 || col.B != 0)
                return new[] { col.R, col.G, col.B, col.A };
            var lcol = obj.GetOrDefault<FLinearColor>(propName);
            if (lcol.R != 0 || lcol.G != 0 || lcol.B != 0)
            {
                var fc = lcol.ToFColor(true);
                return new[] { fc.R, fc.G, fc.B, fc.A };
            }
        }
        catch { }
        return new[] { defR, defG, defB, (byte)255 };
    }
}

public sealed class LevelActorData
{
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public float[] Location { get; set; } = { 0, 0, 0 };
    public float[] Rotation { get; set; } = { 0, 0, 0 };
    public float[] Scale { get; set; } = { 1, 1, 1 };
    public string? MeshPath { get; set; }
    public List<string> OverrideMaterials { get; set; } = new();
    public UObject? RawActor { get; set; }
    public List<UObject> Components { get; set; } = new();
    public UObject? PrimaryComponent { get; set; }
}

public sealed class LevelLightingEnvironment
{
    public List<DirectionalLightData> DirectionalLights { get; } = new();
    public List<SkyLightData> SkyLights { get; } = new();
    public List<PointLightData> PointLights { get; } = new();
    public List<SpotLightData> SpotLights { get; } = new();
    public List<RectLightData> RectLights { get; } = new();
    public List<HeightFogData> HeightFogs { get; } = new();
    public List<SkyAtmosphereData> SkyAtmospheres { get; } = new();
    public List<PostProcessData> PostProcessVolumes { get; } = new();
    public List<DecalActorData> Decals { get; } = new();
    public List<AmbientSoundData> AmbientSounds { get; } = new();
    public List<CameraActorData> Cameras { get; } = new();

    public int TotalLightingCount =>
        DirectionalLights.Count + SkyLights.Count + PointLights.Count + SpotLights.Count +
        RectLights.Count + HeightFogs.Count + SkyAtmospheres.Count + PostProcessVolumes.Count;

    public int TotalEnvironmentActors =>
        TotalLightingCount + Decals.Count + AmbientSounds.Count + Cameras.Count;
}

public sealed class DirectionalLightData
{
    public string ActorName { get; set; } = "";
    public float[] Location { get; set; } = { 0, 0, 0 };
    public float[] Rotation { get; set; } = { 0, 0, 0 };
    public float Intensity { get; set; } = 10f;
    public byte[] LightColor { get; set; } = { 255, 255, 255, 255 };
    public float Temperature { get; set; } = 6500f;
    public bool UseTemperature { get; set; }
    public float LightSourceAngle { get; set; } = 0.5357f;
    public float LightSourceSoftAngle { get; set; }
    public int AtmosphereSunLightIndex { get; set; }
    public bool UsedAsAtmosphereSunLight { get; set; } = true;
    public bool CastShadows { get; set; } = true;
    public bool CastVolumetricShadow { get; set; } = true;
    public float VolumetricScatteringIntensity { get; set; } = 1f;
    public float IndirectLightingIntensity { get; set; } = 1f;
}

public sealed class SkyLightData
{
    public string ActorName { get; set; } = "";
    public float[] Location { get; set; } = { 0, 0, 0 };
    public float Intensity { get; set; } = 1f;
    public byte[] LightColor { get; set; } = { 255, 255, 255, 255 };
    public byte[] LowerHemisphereColor { get; set; } = { 0, 0, 0, 255 };
    public bool LowerHemisphereIsBlack { get; set; } = true;
    public string? Cubemap { get; set; }
    public string SourceType { get; set; } = "SLS_CapturedScene";
    public bool CastShadows { get; set; } = true;
}

public sealed class PointLightData
{
    public string ActorName { get; set; } = "";
    public float[] Location { get; set; } = { 0, 0, 0 };
    public float[] Rotation { get; set; } = { 0, 0, 0 };
    public float Intensity { get; set; } = 5000f;
    public byte[] LightColor { get; set; } = { 255, 255, 255, 255 };
    public float AttenuationRadius { get; set; } = 1000f;
    public float SourceRadius { get; set; }
    public float SoftSourceRadius { get; set; }
    public float SourceLength { get; set; }
    public bool CastShadows { get; set; } = true;
    public float Temperature { get; set; } = 6500f;
    public bool UseTemperature { get; set; }
}

public sealed class SpotLightData
{
    public string ActorName { get; set; } = "";
    public float[] Location { get; set; } = { 0, 0, 0 };
    public float[] Rotation { get; set; } = { 0, 0, 0 };
    public float Intensity { get; set; } = 5000f;
    public byte[] LightColor { get; set; } = { 255, 255, 255, 255 };
    public float AttenuationRadius { get; set; } = 1000f;
    public float InnerConeAngle { get; set; }
    public float OuterConeAngle { get; set; } = 44f;
    public bool CastShadows { get; set; } = true;
}

public sealed class RectLightData
{
    public string ActorName { get; set; } = "";
    public float[] Location { get; set; } = { 0, 0, 0 };
    public float[] Rotation { get; set; } = { 0, 0, 0 };
    public float Intensity { get; set; } = 5000f;
    public byte[] LightColor { get; set; } = { 255, 255, 255, 255 };
    public float AttenuationRadius { get; set; } = 1000f;
    public float SourceWidth { get; set; } = 64f;
    public float SourceHeight { get; set; } = 64f;
    public float BarnDoorAngle { get; set; } = 88f;
    public bool CastShadows { get; set; } = true;
}

public sealed class HeightFogData
{
    public string ActorName { get; set; } = "";
    public float[] Location { get; set; } = { 0, 0, 0 };
    public float FogDensity { get; set; } = 0.02f;
    public float FogHeightFalloff { get; set; } = 0.2f;
    public byte[] FogInscatteringColor { get; set; } = { 114, 150, 204, 255 };
    public float FogMaxOpacity { get; set; } = 1f;
    public float StartDistance { get; set; }
    public bool EnableVolumetricFog { get; set; } = true;
    public float VolumetricFogScatteringDistribution { get; set; } = 0.2f;
    public float VolumetricFogExtinctionScale { get; set; } = 1f;
    public float VolumetricFogDistance { get; set; } = 6000f;
}

public sealed class SkyAtmosphereData
{
    public string ActorName { get; set; } = "";
    public float[] Location { get; set; } = { 0, 0, 0 };
    public float BottomRadius { get; set; } = 6360f;
    public float AtmosphereHeight { get; set; } = 60f;
    public float MultiScatteringFactor { get; set; } = 1f;
    public float RayleighScatteringScale { get; set; } = 0.0331f;
}

public sealed class PostProcessData
{
    public string ActorName { get; set; } = "";
    public float[] Location { get; set; } = { 0, 0, 0 };
    public bool Unbound { get; set; } = true;
    public float Priority { get; set; } = 1f;
    public float BlendWeight { get; set; } = 1f;
}

public sealed class DecalActorData
{
    public string ActorName { get; set; } = "";
    public float[] Location { get; set; } = { 0, 0, 0 };
    public float[] Rotation { get; set; } = { 0, 0, 0 };
    public float[] Scale { get; set; } = { 1, 1, 1 };
    public string? DecalMaterial { get; set; }
    public float[] DecalSize { get; set; } = { 128, 256, 256 };
}

public sealed class AmbientSoundData
{
    public string ActorName { get; set; } = "";
    public float[] Location { get; set; } = { 0, 0, 0 };
    public string? SoundPath { get; set; }
    public float VolumeMultiplier { get; set; } = 1f;
    public float PitchMultiplier { get; set; } = 1f;
}

public sealed class CameraActorData
{
    public string ActorName { get; set; } = "";
    public float[] Location { get; set; } = { 0, 0, 0 };
    public float[] Rotation { get; set; } = { 0, 0, 0 };
    public float FieldOfView { get; set; } = 90f;
    public float AspectRatio { get; set; } = 1.777778f;
}

