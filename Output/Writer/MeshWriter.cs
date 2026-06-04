using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes.PSK;
using Serilog;

namespace UE4Decompiler.Output.Writer;

public static class MeshWriter
{
    public static byte[] BuildFRawMesh(CStaticMeshLod lod)
    {
        using var ms = new MemoryStream();
        using var w = new FArchiveWriter(ms);

        var verts = lod.Verts!;
        var src = lod.Indices!.Value;

        // Fix inside-out / angle-only visibility by flipping triangle winding:
        // 0,1,2 -> 0,2,1
        var indices = new int[src.Length];

        for (int i = 0; i < src.Length; i += 3)
        {
            indices[i + 0] = (int)src[i + 0];
            indices[i + 1] = (int)src[i + 1];
            indices[i + 2] = (int)src[i + 1];
        }

        int numWedges = indices.Length;
        int numTris = numWedges / 3;

        w.Write(1);
        w.Write(0);

        w.Write(numTris);
        for (int i = 0; i < numTris; i++) w.Write(0);

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