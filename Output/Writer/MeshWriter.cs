using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes.PSK;
using Serilog;

namespace UE4Decompiler.Output.Writer;

/// <summary>
/// [Tier2 mesh module] Real-geometry editor StaticMesh: build an FRawMesh blob from a CUE4Parse-converted
/// cooked mesh LOD, then clone the engine Cube editor StaticMesh and APPEND the real FRawMesh as a new bulk
/// blob, re-pointing the StaticMesh's FRawMeshBulkData header at it + changing the source FGuid so the editor
/// rebuilds RenderData from the new geometry (instead of the cached cube). Appending (vs in-place swap) means
/// the Cube's other bulk offsets don't shift. First iteration — expect to tune FRawMesh version/format by test.
/// </summary>
public static class MeshWriter
{
    /// <summary>Serialize an FRawMesh (4.21) from a converted static-mesh LOD. FByteBulkData payload layout.</summary>
    public static byte[] BuildFRawMesh(CStaticMeshLod lod)
    {
        using var ms = new MemoryStream(); using var w = new FArchiveWriter(ms);
        var verts = lod.Verts!;
        var indices = lod.Indices!.Value;
        int numWedges = indices.Length;
        int numTris = numWedges / 3;

        w.Write(1);                 // RawMeshVersion (guess: 1 = RAW_MESH_VER post-initial; tune by test)
        w.Write(0);                 // RawMeshLicenseeVersion

        // FaceMaterialIndices (int32 per triangle) — single section => 0
        w.Write(numTris); for (int i = 0; i < numTris; i++) w.Write(0);
        // FaceSmoothingMasks (uint32 per triangle) — 0
        w.Write(numTris); for (int i = 0; i < numTris; i++) w.Write(0u);
        // VertexPositions (FVector per vertex)
        w.Write(verts.Length); foreach (var v in verts) { w.Write(v.Position.X); w.Write(v.Position.Y); w.Write(v.Position.Z); }
        // WedgeIndices (int32 per wedge)
        w.Write(numWedges); foreach (var idx in indices) w.Write((int)idx);
        // WedgeTangentX (FVector per wedge) = tangent
        w.Write(numWedges); foreach (var idx in indices) { var t = verts[idx].Tangent; w.Write(t.X); w.Write(t.Y); w.Write(t.Z); }
        // WedgeTangentY (FVector per wedge) = 0 (editor recomputes from X/Z)
        w.Write(numWedges); for (int i = 0; i < numWedges; i++) { w.Write(0f); w.Write(0f); w.Write(0f); }
        // WedgeTangentZ (FVector per wedge) = normal
        w.Write(numWedges); foreach (var idx in indices) { var n = verts[idx].Normal; w.Write(n.X); w.Write(n.Y); w.Write(n.Z); }
        // Order matches 4.21 FRawMesh ground truth (engine Cube): tangents -> WedgeTexCoords[8] ->
        // WedgeColors -> trailing int32. UVs come BEFORE colors; colors are empty.
        // WedgeTexCoords[8] (FVector2D per wedge); [0]=UV0, rest empty.
        for (int ch = 0; ch < 8; ch++)
        {
            if (ch == 0) { w.Write(numWedges); foreach (var idx in indices) { w.Write(verts[idx].UV.U); w.Write(verts[idx].UV.V); } }
            else w.Write(0);
        }
        w.Write(0);                 // WedgeColors (FColor[]) — empty
        w.Write(0);                 // trailing array (empty) observed in ground truth
        w.Flush();
        Log.Information("Built FRawMesh: {V} verts, {W} wedges, {T} tris, {B} bytes", verts.Length, numWedges, numTris, ms.Length);
        return ms.ToArray();
    }

    /// <summary>Find the FRawMesh FByteBulkData header in a cloned-Cube StaticMesh payload (flags=1, two equal
    /// counts, int64 offset=0) and re-point it at the appended real blob + new size + new source Guid.</summary>
    public static bool PatchFRawMeshHeader(byte[] payload, int newCount, long newRelOffset)
    {
        for (int i = 0; i + 36 <= payload.Length; i++)
        {
            if (BitConverter.ToInt32(payload, i) != 1) continue;                 // flags == BULKDATA_PayloadAtEndOfFile
            int c = BitConverter.ToInt32(payload, i + 4);
            int s = BitConverter.ToInt32(payload, i + 8);
            long off = BitConverter.ToInt64(payload, i + 12);
            if (c <= 0 || c != s || off != 0) continue;                          // cube FRawMesh: count==size, offset 0
            // sanity: a 16-byte Guid follows at i+20; just require the candidate is the FRawMesh (first such)
            BitConverter.GetBytes(newCount).CopyTo(payload, i + 4);              // ElementCount
            BitConverter.GetBytes((uint)newCount).CopyTo(payload, i + 8);        // SizeOnDisk
            BitConverter.GetBytes(newRelOffset).CopyTo(payload, i + 12);         // OffsetInFile (relative)
            var g = Guid.NewGuid().ToByteArray(); Array.Copy(g, 0, payload, i + 20, 16);   // new source Guid -> rebuild
            Log.Information("Patched FRawMesh header @ {Off}: count {C}->{N}, relOffset 0->{R}", i, c, newCount, newRelOffset);
            return true;
        }
        Log.Warning("FRawMesh FByteBulkData header not found in StaticMesh payload");
        return false;
    }
}
