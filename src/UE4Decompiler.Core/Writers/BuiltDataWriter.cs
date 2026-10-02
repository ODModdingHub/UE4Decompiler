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
/// This package is written as UE5.x. The registry's native body (FLightMap2D etc.) is read at the ARCHIVE version,
/// not upconverted.
/// </summary>
public static class BuiltDataWriter
{
    // FRenderingObjectVersion gate values (mirrors CUE4Parse enum).
    private static readonly FGuid RenderingObjVerGuid = new(0x12F88B9F, 0x88754AFC, 0xA67CD90C, 0x383ABD29);
    private static readonly FGuid ReflectionCaptureObjVerGuid = new(0x6B266CEC, 0x1EC74B8F, 0xA30BE4D9, 0x0942FC07);
    private static readonly FGuid FortniteMainObjVerGuid = new(0x601D1886, 0xAC644F84, 0xAA16D3DE, 0x0DEAC7D6);

    /// <summary>Serialize the registry's native body (everything AFTER the LevelLightingQuality tagged-prop None).</summary>
    public static byte[] SerializeRegistryNative(UMapBuildDataRegistry reg, int renderingObjVer, bool reflectionCaptureInMapBuildData,
        bool fortniteGridDescSupport, Func<FPackageIndex?, int> remap)
    {
        int LightmapHasShadowmapData = (int)FRenderingObjectVersion.Type.LightmapHasShadowmapData;
        int VirtualTexturedLightmaps = (int)FRenderingObjectVersion.Type.VirtualTexturedLightmaps;
        int VirtualTexturedLightmapsV2 = (int)FRenderingObjectVersion.Type.VirtualTexturedLightmapsV2;
        int VirtualTexturedLightmapsV3 = (int)FRenderingObjectVersion.Type.VirtualTexturedLightmapsV3;
        int VolumetricLightmaps = (int)FRenderingObjectVersion.Type.VolumetricLightmaps;
        int SkyAtmosphereVer = (int)FRenderingObjectVersion.Type.SkyAtmosphereStaticLightingVersioning;

        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);

        // FStripDataFlags { Global=0, Class=0 } -> nothing stripped
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

        // LevelPrecomputedLightVolumeBuildData : TMap (empty)
        w.Write(0);
        // LevelPrecomputedVolumetricLightmapBuildData : TMap (gate VolumetricLightmaps; empty)
        if (renderingObjVer >= VolumetricLightmaps) w.Write(0);
        // LightBuildData : TMap (empty)
        w.Write(0);
        // ReflectionCaptureBuildData : TMap (empty)
        if (reflectionCaptureInMapBuildData) w.Write(0);
        // SkyAtmosphereBuildData : TMap (empty)
        if (renderingObjVer >= SkyAtmosphereVer) w.Write(0);
        // Fortnite VolumetricLightMapGridDescSupport -> bool bHasGrid (int32)
        if (fortniteGridDescSupport) w.Write(0);

        w.Flush();
        return ms.ToArray();
    }

    private static void WriteMeshMapBuildData(FArchiveWriter w, FMeshMapBuildData d, int rov,
        int lightmapHasShadowmapData, int vt, int vt2, int vt3, Func<FPackageIndex?, int> remap)
    {
        if (d.LightMap is FLightMap2D lm)
        {
            w.Write((uint)2);                                   // LMT_2D
            WriteGuidArray(w, lm.LightGuids);
            w.Write(remap(lm.Textures != null && lm.Textures.Length > 0 ? lm.Textures[0] : null));
            w.Write(remap(lm.Textures != null && lm.Textures.Length > 1 ? lm.Textures[1] : null));
            w.Write(remap(lm.SkyOcclusionTexture));
            w.Write(remap(lm.AOMaterialMaskTexture));
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

        if (d.ShadowMap is FShadowMap2D sm)
        {
            w.Write((uint)2);                                   // SMT_2D
            WriteGuidArray(w, sm.LightGuids);
            w.Write(remap(sm.Texture));
            WriteVec2d(w, sm.CoordinateScale);
            WriteVec2d(w, sm.CoordinateBias);
            for (int i = 0; i < 4; i++) w.Write(sm.bChannelValid != null && i < sm.bChannelValid.Length && sm.bChannelValid[i] ? 1 : 0);
            WriteVec4f(w, sm.InvUniformPenumbraSize);
        }
        else
        {
            w.Write((uint)0);                                   // SMT_None
        }

        WriteGuidArray(w, d.IrrelevantLights);
        w.Write(16);                                            // elementSize
        w.Write(0);                                             // count
    }

    private static FPackageIndex? GetVT(FLightMap2D lm, int i)
        => lm.VirtualTextures != null && i < lm.VirtualTextures.Length ? lm.VirtualTextures[i] : null;

    private static void WriteGuidArray(FArchiveWriter w, FGuid[]? a)
    {
        a ??= Array.Empty<FGuid>();
        w.Write(a.Length);
        foreach (var g in a) w.WriteGuid(g);
    }

    private static void WriteVec4f(FArchiveWriter w, FVector4 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W); }
    private static void WriteVec2d(FArchiveWriter w, FVector2D? v) { var vv = v ?? new FVector2D(0, 0); w.Write((double)vv.X); w.Write((double)vv.Y); }

    public static int CustomVer(FPackageFileSummary summary, FGuid guid)
    {
        var vers = summary.CustomVersionContainer?.Versions;
        if (vers == null) return -1;
        foreach (var v in vers) if (v.Key == guid) return v.Version;
        return -1;
    }

    public static (int rov, bool refl, bool fortnite) GatesForGame(EGame game)
    {
        int rov = (int)FRenderingObjectVersion.Type.VirtualTexturedLightmapsV3;
        bool refl = game >= EGame.GAME_UE4_25;
        bool fortnite = game >= EGame.GAME_UE5_3;
        return (rov, refl, fortnite);
    }

    public static int RenderingObjVerOf(FPackageFileSummary s) => CustomVer(s, RenderingObjVerGuid);
    public static bool ReflectionCaptureInMapBuildData(FPackageFileSummary s)
        => CustomVer(s, ReflectionCaptureObjVerGuid) >= (int)FReflectionCaptureObjectVersion.Type.MoveReflectionCaptureDataToMapBuildData;
    public static bool FortniteGridDescSupport(FPackageFileSummary s)
        => CustomVer(s, FortniteMainObjVerGuid) >= (int)FFortniteMainBranchObjectVersion.Type.VolumetricLightMapGridDescSupport;
}
