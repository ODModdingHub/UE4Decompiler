using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes.PSK;
using Serilog;

namespace UE4Decompiler.Output.Writer;

public static class MeshWriter
{
    public readonly record struct MeshBounds(float OriginX, float OriginY, float OriginZ,
        float ExtentX, float ExtentY, float ExtentZ, float SphereRadius);

    public static MeshBounds CalculateBounds(CStaticMeshLod lod)
    {
        var verts = lod.Verts ?? throw new InvalidOperationException("Static mesh LOD has no vertices");
        if (verts.Length == 0) return default;

        var minX = verts[0].Position.X; var maxX = minX;
        var minY = verts[0].Position.Y; var maxY = minY;
        var minZ = verts[0].Position.Z; var maxZ = minZ;
        foreach (var v in verts)
        {
            var p = v.Position;
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
        }

        var ox = (minX + maxX) * 0.5f;
        var oy = (minY + maxY) * 0.5f;
        var oz = (minZ + maxZ) * 0.5f;
        var maxDistSq = 0f;
        foreach (var v in verts)
        {
            var dx = v.Position.X - ox;
            var dy = v.Position.Y - oy;
            var dz = v.Position.Z - oz;
            var d = dx * dx + dy * dy + dz * dz;
            if (d > maxDistSq) maxDistSq = d;
        }

        return new MeshBounds(ox, oy, oz,
            (maxX - minX) * 0.5f, (maxY - minY) * 0.5f, (maxZ - minZ) * 0.5f,
            MathF.Sqrt(maxDistSq));
    }

    /// <param name="materialSlotCount">Number of StaticMaterials slots the written mesh will have. Per-face material
    /// indices are clamped to [0, slotCount-1]: a section whose MaterialIndex exceeds the slot count would make the
    /// editor's scene proxy read StaticMaterials out of bounds when the mesh is placed in a level -> access violation
    /// reading a garbage address (crashes only the maps that place such a mesh). 0 = no clamp (single-slot fallback).</param>
    public static byte[] BuildFRawMesh(CStaticMeshLod lod, int materialSlotCount = 0)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);

        var verts = lod.Verts!;
        var src = lod.Indices!.Value;
        int maxSlot = materialSlotCount > 0 ? materialSlotCount - 1 : 0;

        // Fix inside-out / angle-only visibility by flipping triangle winding:
        // 0,1,2 -> 0,2,1
        var indices = new int[src.Length];

        for (int i = 0; i < src.Length; i += 3)
        {
            indices[i + 0] = (int)src[i + 0];
            indices[i + 1] = (int)src[i + 1];
            indices[i + 2] = (int)src[i + 2];
        }

        int numWedges = indices.Length;
        int numTris = numWedges / 3;

        // Per-triangle material slot index from the mesh sections, so multi-material meshes show the right
        // texture per section (instead of slot 0 on everything). Triangle order matches src triangle order.
        var faceMat = new int[numTris];
        var sections = lod.Sections?.Value;
        if (sections != null)
            foreach (var s in sections)
            {
                int firstTri = s.FirstIndex / 3, slot = s.MaterialIndex < 0 ? 0 : s.MaterialIndex;
                if (slot > maxSlot) slot = maxSlot;     // never reference a slot the StaticMaterials array lacks
                for (int t = firstTri; t < firstTri + s.NumFaces && t < numTris; t++) faceMat[t] = slot;
            }
        Log.Information("FaceMat: {Secs} section(s), distinct slots [{Slots}] over {T} tris",
            sections?.Length ?? 0, string.Join(",", faceMat.Distinct().OrderBy(x => x)), numTris);

        w.Write(1);
        w.Write(0);

        w.Write(numTris);
        for (int i = 0; i < numTris; i++) w.Write(faceMat[i]);

        w.Write(numTris);
        for (int i = 0; i < numTris; i++) w.Write(0u);

        w.Write(verts.Length);
        foreach (var v in verts)
        {
            w.Write(v.Position.X);
            w.Write(v.Position.Y);
            w.Write(v.Position.Z);
        }

        w.Write(numWedges);
        foreach (var idx in indices)
            w.Write(idx);

        w.Write(numWedges);
        foreach (var idx in indices)
        {
            var t = verts[idx].Tangent;
            w.Write(t.X);
            w.Write(t.Y);
            w.Write(t.Z);
        }

        w.Write(numWedges);
        for (int i = 0; i < numWedges; i++)
        {
            w.Write(0f);
            w.Write(0f);
            w.Write(0f);
        }

        w.Write(numWedges);
        foreach (var idx in indices)
        {
            var n = verts[idx].Normal;

            // Flip normals to match flipped winding.
            w.Write(n.X);
            w.Write(n.Y);
            w.Write(n.Z);
        }

        for (int ch = 0; ch < 8; ch++)
        {
            if (ch == 0)
            {
                w.Write(numWedges);
                foreach (var idx in indices)
                {
                    w.Write(verts[idx].UV.U);
                    w.Write(verts[idx].UV.V);
                }
            }
            else
            {
                w.Write(0);
            }
        }

        w.Write(0);
        w.Write(0);

        w.Flush();

        Log.Information(
            "Built FRawMesh fixed winding: {V} verts, {W} wedges, {T} tris, {B} bytes",
            verts.Length,
            numWedges,
            numTris,
            ms.Length
        );

        return ms.ToArray();
    }

    public static bool PatchFRawMeshHeader(byte[] payload, int newCount, long newRelOffset)
    {
        for (int i = 0; i + 36 <= payload.Length; i++)
        {
            if (BitConverter.ToInt32(payload, i) != 1) continue;

            int c = BitConverter.ToInt32(payload, i + 4);
            int s = BitConverter.ToInt32(payload, i + 8);
            long off = BitConverter.ToInt64(payload, i + 12);

            if (c <= 0 || c != s || off != 0) continue;

            BitConverter.GetBytes(newCount).CopyTo(payload, i + 4);
            BitConverter.GetBytes((uint)newCount).CopyTo(payload, i + 8);
            BitConverter.GetBytes(newRelOffset).CopyTo(payload, i + 12);

            var g = Guid.NewGuid().ToByteArray();
            Array.Copy(g, 0, payload, i + 20, 16);

            Log.Information(
                "Patched FRawMesh header @ {Off}: count {C}->{N}, relOffset 0->{R}",
                i,
                c,
                newCount,
                newRelOffset
            );

            return true;
        }

        Log.Warning("FRawMesh FByteBulkData header not found in StaticMesh payload");
        return false;
    }
}