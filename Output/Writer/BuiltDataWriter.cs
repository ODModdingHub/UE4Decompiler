using CUE4Parse.UE4.Assets.Exports.BuildData;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using Serilog;

namespace UE4Decompiler.Output.Writer;

/// <summary>
/// Emits an editor-loadable <c>*_BuiltData.uasset</c> (UMapBuildDataRegistry + its lightmap textures) so the recovered
/// built lighting binds to the reconstructed map's components (keyed by FStaticMeshComponentLODInfo.MapBuildDataId).
///
/// CRITICAL: this package is written as UE5.x (NOT 4.21 like the rest of the dump). The registry's native body
/// (FLightMap2D etc.) is read at the ARCHIVE version, not upconverted — a 4.21 summary would make the editor take the
/// legacy lightmap branch and misread modern data. SynthPackageWriter(GAME_UE5_x) emits the matching summary, and
/// CustomVersionsOverride carries the source's custom versions so the version gates line up exactly.
///
/// The native serializer below MIRRORS CUE4Parse's UMapBuildDataRegistry.Deserialize as a WRITER. Byte sizes that bite:
///   - map / array = int32 count + entries; FGuid = 16 bytes.
///   - ReadBoolean() reads an INT32 (bShadowChannelValid[4] = 16 bytes, bHasGrid = 4 bytes).
///   - ScaleVectors/AddVectors/InvUniformPenumbraSize are Ar.Read&lt;FVector4&gt; => 4 FLOATs (16 bytes).
///   - CoordinateScale/Bias are new FVector2D(Ar) => 2 ReadFReal => 2 DOUBLEs under UE5 LWC (16 bytes).
/// </summary>
public static class BuiltDataWriter
{
    // FRenderingObjectVersion gate values (mirrors CUE4Parse enum).
    private static readonly FGuid RenderingObjVerGuid = new(0x12F88B9F, 0x88754AFC, 0xA67CD90C, 0x383ABD29);
    private static readonly FGuid ReflectionCaptureObjVerGuid = new(0x6B266CEC, 0x1EC74B8F, 0xA30BE4D9, 0x0942FC07);
    private static readonly FGuid FortniteMainObjVerGuid = new(0x601D1886, 0xAC644F84, 0xAA16D3DE, 0x0DEAC7D6);

    /// <summary>Serialize the registry's native body (everything AFTER the LevelLightingQuality tagged-prop None).
    /// <paramref name="remap"/> maps a source lightmap-texture FPackageIndex to the index it should carry in OUR
    /// package (identity for a round-trip test; texture-name remap for real emission). Gate values come from the
    /// source package's custom versions so the layout matches what the editor (same versions) will read.</summary>
    public static byte[] SerializeRegistryNative(UMapBuildDataRegistry reg, int renderingObjVer, bool reflectionCaptureInMapBuildData,
        bool fortniteGridDescSupport, System.Func<FPackageIndex?, int> remap)
    {
        // gate constants (CUE4Parse FRenderingObjectVersion.Type)
        int LightmapHasShadowmapData = (int)FRenderingObjectVersion.Type.LightmapHasShadowmapData;
        int VirtualTexturedLightmaps = (int)FRenderingObjectVersion.Type.VirtualTexturedLightmaps;
        int VirtualTexturedLightmapsV2 = (int)FRenderingObjectVersion.Type.VirtualTexturedLightmapsV2;
        int VirtualTexturedLightmapsV3 = (int)FRenderingObjectVersion.Type.VirtualTexturedLightmapsV3;
        int VolumetricLightmaps = (int)FRenderingObjectVersion.Type.VolumetricLightmaps;
        int SkyAtmosphereVer = (int)FRenderingObjectVersion.Type.SkyAtmosphereStaticLightingVersioning;

        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);

        // FStripDataFlags { Global=0, Class=0 } -> nothing stripped (editor data present, audio-visual present).
        w.Write((byte)0); w.Write((byte)0);

        // MeshBuildData : TMap<FGuid, FMeshMapBuildData>
        var mesh = reg.MeshBuildData ?? new();
        w.Write(mesh.Count);
        foreach (var kv in mesh)
        {
            w.WriteGuid(kv.Key);
            WriteMeshMapBuildData(w, kv.Value, renderingObjVer, LightmapHasShadowmapData,
                VirtualTexturedLightmaps, VirtualTexturedLightmapsV2, VirtualTexturedLightmapsV3, remap);
        }

        // LevelPrecomputedLightVolumeBuildData : TMap (v1: empty)
        w.Write(0);
        // LevelPrecomputedVolumetricLightmapBuildData : TMap (gate VolumetricLightmaps; v1: empty)
        if (renderingObjVer >= VolumetricLightmaps) w.Write(0);
        // LightBuildData : TMap (v1: empty)
        w.Write(0);
        // ReflectionCaptureBuildData : TMap (gate MoveReflectionCaptureDataToMapBuildData; v1: empty)
        if (reflectionCaptureInMapBuildData) w.Write(0);
        // SkyAtmosphereBuildData : TMap (gate SkyAtmosphereStaticLightingVersioning; v1: empty)
        if (renderingObjVer >= SkyAtmosphereVer) w.Write(0);
        // Fortnite VolumetricLightMapGridDescSupport -> bool bHasGrid (ReadBoolean = int32)
        if (fortniteGridDescSupport) w.Write(0);

        w.Flush();
        return ms.ToArray();
    }

    private static void WriteMeshMapBuildData(FArchiveWriter w, FMeshMapBuildData d, int rov,
        int lightmapHasShadowmapData, int vt, int vt2, int vt3, System.Func<FPackageIndex?, int> remap)
    {
        // LightMap: ELightMapType (uint32) + body
        if (d.LightMap is FLightMap2D lm)
        {
            w.Write((uint)2);                                   // LMT_2D
            // base FLightMap.LightGuids : TArray<FGuid>
            WriteGuidArray(w, lm.LightGuids);
            // modern (Ver > COMBINED_LIGHTMAP_TEXTURES) body
            w.Write(remap(lm.Textures != null && lm.Textures.Length > 0 ? lm.Textures[0] : null));
            w.Write(remap(lm.Textures != null && lm.Textures.Length > 1 ? lm.Textures[1] : null));
            w.Write(remap(lm.SkyOcclusionTexture));             // Ver >= SKY_LIGHT_COMPONENT (UE5: yes)
            w.Write(remap(lm.AOMaterialMaskTexture));           // Ver >= AO_MATERIAL_MASK (UE5: yes)
            for (int i = 0; i < 4; i++)
            {
                WriteVec4f(w, lm.ScaleVectors != null && i < lm.ScaleVectors.Length ? lm.ScaleVectors[i] : default);
                WriteVec4f(w, lm.AddVectors != null && i < lm.AddVectors.Length ? lm.AddVectors[i] : default);
            }
            WriteVec2d(w, lm.CoordinateScale);
            WriteVec2d(w, lm.CoordinateBias);
            if (rov >= lightmapHasShadowmapData)
            {
                for (int i = 0; i < 4; i++) w.Write(lm.bShadowChannelValid != null && i < lm.bShadowChannelValid.Length && lm.bShadowChannelValid[i] ? 1 : 0);
                WriteVec4f(w, lm.InvUniformPenumbraSize ?? default);
            }
            if (rov >= vt)
            {
                if (rov >= vt3) { w.Write(remap(GetVT(lm, 0))); w.Write(remap(GetVT(lm, 1))); }
                else if (rov >= vt2) { w.Write(remap(GetVT(lm, 0))); }
                else { w.Write(remap(GetVT(lm, 0))); }
            }
        }
        else
        {
            w.Write((uint)0);                                   // LMT_None
        }

        // ShadowMap: EShadowMapType (uint32) + body
        if (d.ShadowMap is FShadowMap2D sm)
        {
            w.Write((uint)2);                                   // SMT_2D
            WriteGuidArray(w, sm.LightGuids);
            w.Write(remap(sm.Texture));
            WriteVec2d(w, sm.CoordinateScale);
            WriteVec2d(w, sm.CoordinateBias);
            for (int i = 0; i < 4; i++) w.Write(sm.bChannelValid != null && i < sm.bChannelValid.Length && sm.bChannelValid[i] ? 1 : 0);
            WriteVec4f(w, sm.InvUniformPenumbraSize);           // Ver >= STATIC_SHADOWMAP_PENUMBRA_SIZE (UE5: yes)
        }
        else
        {
            w.Write((uint)0);                                   // SMT_None
        }

        // IrrelevantLights : TArray<FGuid>
        WriteGuidArray(w, d.IrrelevantLights);
        // PerInstanceLightmapData : bulk array (elementSize int32 + count int32 + data). v1: empty (only ISM/foliage use it).
        w.Write(16);                                            // elementSize (unused when count==0)
        w.Write(0);                                             // count
    }

    private static FPackageIndex? GetVT(FLightMap2D lm, int i)
        => lm.VirtualTextures != null && i < lm.VirtualTextures.Length ? lm.VirtualTextures[i] : null;

    private static void WriteGuidArray(FArchiveWriter w, FGuid[]? a)
    {
        a ??= System.Array.Empty<FGuid>();
        w.Write(a.Length);
        foreach (var g in a) w.WriteGuid(g);
    }

    private static void WriteVec4f(FArchiveWriter w, FVector4 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W); }     // 4 floats
    private static void WriteVec2d(FArchiveWriter w, FVector2D? v) { var vv = v ?? new FVector2D(0, 0); w.Write((double)vv.X); w.Write((double)vv.Y); }   // 2 doubles (UE5 LWC)

    /// <summary>Look up a custom version value from a parsed package summary; -1 if absent.</summary>
    public static int CustomVer(CUE4Parse.UE4.Objects.UObject.FPackageFileSummary summary, FGuid guid)
    {
        var vers = summary.CustomVersionContainer?.Versions;
        if (vers == null) return -1;
        foreach (var v in vers) if (v.Key == guid) return v.Version;
        return -1;
    }

    /// <summary>Registry version gates derived from the target game (matches CUE4Parse's version defaults).
    /// Tuned/verified for UE5.0–5.2 (the user's target is 5.1): FRenderingObjectVersion = VirtualTexturedLightmapsV3,
    /// reflection-capture-in-mapbuilddata = true, Fortnite VolumetricLightMapGridDescSupport = false (not reached until
    /// after 5.1, so no trailing bHasGrid bool).</summary>
    public static (int rov, bool refl, bool fortnite) GatesForGame(EGame game)
    {
        int rov = game < EGame.GAME_UE5_3
            ? (int)FRenderingObjectVersion.Type.VirtualTexturedLightmapsV3
            : (int)FRenderingObjectVersion.Type.VirtualTexturedLightmapsV3;   // (only 5.0–5.2 validated)
        bool refl = game >= EGame.GAME_UE4_25;
        bool fortnite = game >= EGame.GAME_UE5_3;   // VolumetricLightMapGridDescSupport lands after 5.1
        return (rov, refl, fortnite);
    }

    public static int RenderingObjVerOf(CUE4Parse.UE4.Objects.UObject.FPackageFileSummary s) => CustomVer(s, RenderingObjVerGuid);
    public static bool ReflectionCaptureInMapBuildData(CUE4Parse.UE4.Objects.UObject.FPackageFileSummary s)
        => CustomVer(s, ReflectionCaptureObjVerGuid) >= (int)FReflectionCaptureObjectVersion.Type.MoveReflectionCaptureDataToMapBuildData;
    public static bool FortniteGridDescSupport(CUE4Parse.UE4.Objects.UObject.FPackageFileSummary s)
        => CustomVer(s, FortniteMainObjVerGuid) >= (int)FFortniteMainBranchObjectVersion.Type.VolumetricLightMapGridDescSupport;
}
