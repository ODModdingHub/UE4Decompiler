using System.Text;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;

namespace UE4Decompiler.Output.Writer;

/// <summary>
/// Little-endian binary writer with the UE primitives the package writer needs (FString, FName,
/// FGuid, FEngineVersion). Mirrors CUE4Parse's reader so output round-trips through it.
/// </summary>
public sealed class FArchiveWriter : IDisposable
{
    private readonly BinaryWriter _w;
    public FArchiveWriter(Stream s) => _w = new BinaryWriter(s, Encoding.ASCII, leaveOpen: true);

    public long Position => _w.BaseStream.Position;
    public void Seek(long pos) => _w.BaseStream.Seek(pos, SeekOrigin.Begin);

    public void Write(int v) => _w.Write(v);
    public void Write(float v) => _w.Write(v);
    public void Write(double v) => _w.Write(v);
    public void Write(uint v) => _w.Write(v);
    public void Write(long v) => _w.Write(v);
    public void Write(ushort v) => _w.Write(v);
    public void Write(byte v) => _w.Write(v);
    public void WriteBool(bool v) => _w.Write(v ? 1 : 0); // UE serializes bool as int32 in archives... but export flags are bytes
    public void WriteByteBool(bool v) => _w.Write((byte)(v ? 1 : 0));
    public void WriteBytes(byte[] b) => _w.Write(b);

    /// <summary>FString: int32 length (incl. null term; positive=ANSI, negative=UTF16), chars, null term.</summary>
    public void WriteFString(string? s)
    {
        if (string.IsNullOrEmpty(s)) { _w.Write(0); return; }

        var isAscii = s.All(c => c < 128);
        if (isAscii)
        {
            _w.Write(s.Length + 1);
            _w.Write(Encoding.ASCII.GetBytes(s));
            _w.Write((byte)0);
        }
        else
        {
            _w.Write(-(s.Length + 1));
            _w.Write(Encoding.Unicode.GetBytes(s)); // UTF-16LE
            _w.Write((ushort)0);
        }
    }

    /// <summary>FName reference on disk: int32 name-table index + int32 number (raw, 0 = no instance).</summary>
    public void WriteFName(FName name)
    {
        _w.Write(name.Index);
        _w.Write(name.Number);
    }

    public void WriteGuid(FGuid g)
    {
        _w.Write(g.A); _w.Write(g.B); _w.Write(g.C); _w.Write(g.D);
    }

    /// <summary>FEngineVersion: Major/Minor/Patch (uint16), Changelist (uint32), Branch (FString).</summary>
    public void WriteEngineVersion(ushort major, ushort minor, ushort patch, uint changelist, string branch)
    {
        _w.Write(major); _w.Write(minor); _w.Write(patch); _w.Write(changelist);
        WriteFString(branch);
    }

    public void Flush() => _w.Flush();
    public void Dispose() { _w.Flush(); _w.Dispose(); }
}
