namespace UE4Decompiler.Output.Writer;

/// <summary>
/// [Tier2 Phase 4] Writes a UE 4.21 tagged-property stream from scratch — the keystone for synthesizing
/// editor exports (UBlueprint, EdGraph, K2Node, SCS_Node…) that don't exist in cooked paks.
///
/// Format (CoreUObject/Private/UObject/PropertyTag.cpp, operator&lt;&lt;(FSlot,FPropertyTag), ver 517):
///   FName Name; if None -> stream ends
///   FName Type; int32 Size (byte length of the value, backpatched); int32 ArrayIndex
///   [StructProperty] FName StructName + FGuid StructGuid
///   [Bool] uint8 BoolVal (this IS the value; value section is empty)
///   [Byte/Enum] FName EnumName
///   [Array] FName InnerType
///   uint8 HasPropertyGuid (0)
///   &lt;value bytes&gt; (UProperty::SerializeItem)
///
/// FNames are written as (nameIndex, number) where nameIndex resolves via the supplied name-table
/// adder, and object references as int32 FPackageIndex via the supplied resolver.
/// </summary>
public sealed class TaggedPropertyWriter
{
    private readonly FArchiveWriter _w;
    private readonly Func<string, int> _name;   // string -> name-table index (adds if missing)

    public TaggedPropertyWriter(FArchiveWriter w, Func<string, int> nameAdder) { _w = w; _name = nameAdder; }

    private void FName(string s) { _w.Write(_name(s)); _w.Write(0); }   // (index, number=0)

    /// <summary>Write tag header up to (not including) the value; returns the file offset of Size for backpatch.</summary>
    private long BeginTag(string name, string type, Action? extra = null)
    {
        FName(name);
        FName(type);
        var sizeOff = _w.Position;
        _w.Write(0);            // Size placeholder (backpatched in EndTag)
        _w.Write(0);            // ArrayIndex
        extra?.Invoke();        // struct/bool/enum/array tag-data
        _w.WriteByteBool(false); // HasPropertyGuid = 0
        return sizeOff;
    }

    private void EndTag(long sizeOff, long valueStart)
    {
        var end = _w.Position;
        var size = (int)(end - valueStart);
        _w.Seek(sizeOff); _w.Write(size); _w.Seek(end);
    }

    /// <summary>Terminating None tag — ends a tagged-property stream.</summary>
    public void WriteNone() => FName("None");

    public void Object(string name, int packageIndex)
    {
        var so = BeginTag(name, "ObjectProperty"); var vs = _w.Position;
        _w.Write(packageIndex);                    // FPackageIndex (export+1 / -(import+1) / 0)
        EndTag(so, vs);
    }

    public void Int(string name, int v)
    {
        var so = BeginTag(name, "IntProperty"); var vs = _w.Position;
        _w.Write(v);
        EndTag(so, vs);
    }

    public void Bool(string name, bool v)
    {
        // Bool value lives in the tag (BoolVal); value section is empty (Size 0).
        var so = BeginTag(name, "BoolProperty", () => _w.WriteByteBool(v)); var vs = _w.Position;
        EndTag(so, vs);
    }

    public void Name(string name, string value)
    {
        var so = BeginTag(name, "NameProperty"); var vs = _w.Position;
        FName(value);
        EndTag(so, vs);
    }

    public void Str(string name, string value)
    {
        var so = BeginTag(name, "StrProperty"); var vs = _w.Position;
        _w.WriteFString(value);
        EndTag(so, vs);
    }

    /// <summary>Enum/byte property — value is the enum entry's FName (e.g. "ENodeEnabledState::Disabled").</summary>
    public void Enum(string name, string enumTypeName, string entryName)
    {
        var so = BeginTag(name, "EnumProperty", () => FName(enumTypeName)); var vs = _w.Position;
        FName(entryName);
        EndTag(so, vs);
    }

    /// <summary>FGuid struct property (16 bytes) — used for NodeGuid/GraphGuid/BlueprintGuid/PinId.</summary>
    public void GuidStruct(string name, FGuid16 g)
    {
        var so = BeginTag(name, "StructProperty", () => { FName("Guid"); _w.WriteBytes(new byte[16]); }); // StructName=Guid, StructGuid=0
        var vs = _w.Position;
        _w.Write(g.A); _w.Write(g.B); _w.Write(g.C); _w.Write(g.D);
        EndTag(so, vs);
    }

    /// <summary>Array of object references (e.g. EventGraph.Nodes[], Blueprint.UbergraphPages[]).</summary>
    public void ObjectArray(string name, IReadOnlyList<int> packageIndices)
    {
        var so = BeginTag(name, "ArrayProperty", () => FName("ObjectProperty")); var vs = _w.Position;
        _w.Write(packageIndices.Count);
        foreach (var pi in packageIndices) _w.Write(pi);
        EndTag(so, vs);
    }

    /// <summary>Generic struct property with caller-written value bytes (for non-Guid structs).</summary>
    public void Struct(string name, string structName, Action writeValue)
    {
        var so = BeginTag(name, "StructProperty", () => { FName(structName); _w.WriteBytes(new byte[16]); });
        var vs = _w.Position;
        writeValue();
        EndTag(so, vs);
    }
}

/// <summary>Plain 4-uint32 GUID (matches CUE4Parse FGuid A/B/C/D ordering used by the writer).</summary>
public readonly record struct FGuid16(uint A, uint B, uint C, uint D)
{
    public static FGuid16 NewGuid()
    {
        var g = Guid.NewGuid().ToByteArray();
        return new FGuid16(BitConverter.ToUInt32(g, 0), BitConverter.ToUInt32(g, 4),
                           BitConverter.ToUInt32(g, 8), BitConverter.ToUInt32(g, 12));
    }
}
