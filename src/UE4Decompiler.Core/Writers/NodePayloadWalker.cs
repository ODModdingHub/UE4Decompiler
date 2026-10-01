namespace UE4Decompiler.Output.Writer;

/// <summary>
/// Walks a serialized K2Node payload (tagged-property stream + UEdGraphNode pin section) to locate a
/// specific pin and its LinkedTo array, so we can patch a wire into a REAL editor node (e.g. connect an
/// existing BeginPlay's "then" pin to a reconstructed CallFunction). Field layout matches
/// <see cref="PinSerializer"/> and the reversed 4.21 ground truth.
/// </summary>
public static class NodePayloadWalker
{
    public sealed class PinInfo
    {
        public int PinNameIdx;
        public byte[] PinId = new byte[16];
        public int LinkedToCountOffset;   // byte offset (within payload) of the LinkedTo int32 count
        public int LinkedToCount;
    }

    private static int SkipFString(byte[] p, int o)
    {
        int len = BitConverter.ToInt32(p, o); o += 4;
        if (len > 0) o += len;                 // ANSI incl null
        else if (len < 0) o += (-len) * 2;     // UTF-16 incl null
        return o;
    }

    private static int SkipFText(byte[] p, int o)
    {
        o += 4;                                 // Flags (int32)
        sbyte hist = unchecked((sbyte)p[o]); o += 1;   // HistoryType
        if (hist == -1) return o;               // empty/INVALID — done
        if (hist == 0)                          // Base: Namespace, Key, SourceString (FStrings)
        { o = SkipFString(p, o); o = SkipFString(p, o); o = SkipFString(p, o); return o; }
        throw new NotSupportedException($"FText HistoryType {hist} not handled at {o}");
    }

    /// <summary>Walk the tagged-property stream; return offset of the byte just past the terminating None.</summary>
    public static int SkipTaggedProperties(byte[] p, int o, int noneIdx)
    {
        while (true)
        {
            int nameIdx = BitConverter.ToInt32(p, o);
            o += 8;                              // Name FName (idx+number)
            if (nameIdx == noneIdx) return o;    // None terminator consumed
            int typeIdx = BitConverter.ToInt32(p, o); o += 8;   // Type FName
            int size = BitConverter.ToInt32(p, o); o += 4;      // Size
            o += 4;                                              // ArrayIndex
            // type-specific tag data (resolve by the type name via caller-supplied map would be cleaner,
            // but we only need names; use the known property-type indices passed in TypeNames).
            o = SkipTypeTagData(p, o, typeIdx);
            o += 1;                              // HasPropertyGuid
            o += size;                           // value bytes
        }
    }

    /// <summary>Walk the tagged-property stream and return the byte span [start,end) of the first property whose
    /// name index == targetNameIdx (the full tag header + value). Returns (-1,-1) if not found. Used to splice a
    /// rebuilt StaticMaterials array into a cloned StaticMesh export without disturbing its other properties.</summary>
    public static (int start, int end) FindPropertySpan(byte[] p, int o, int noneIdx, int targetNameIdx)
    {
        while (true)
        {
            int propStart = o;
            int nameIdx = BitConverter.ToInt32(p, o);
            o += 8;
            if (nameIdx == noneIdx) return (-1, -1);
            int typeIdx = BitConverter.ToInt32(p, o); o += 8;
            int size = BitConverter.ToInt32(p, o); o += 4;
            o += 4;                              // ArrayIndex
            o = SkipTypeTagData(p, o, typeIdx);
            o += 1;                              // HasPropertyGuid
            o += size;                           // value
            if (nameIdx == targetNameIdx) return (propStart, o);
        }
    }

    // Type-specific tag data sizes keyed by the type-name index. Caller sets these from the package name table.
    // [ThreadStatic] so concurrent pipeline workers don't clobber each other's per-package indices.
    [ThreadStatic] public static int StructPropertyIdx;
    [ThreadStatic] public static int BoolPropertyIdx;
    [ThreadStatic] public static int BytePropertyIdx;
    [ThreadStatic] public static int EnumPropertyIdx;
    [ThreadStatic] public static int ArrayPropertyIdx;
    [ThreadStatic] public static int SetPropertyIdx;
    [ThreadStatic] public static int MapPropertyIdx;

    private static int SkipTypeTagData(byte[] p, int o, int typeIdx)
    {
        if (typeIdx == StructPropertyIdx) return o + 8 + 16;          // StructName FName + StructGuid
        if (typeIdx == BoolPropertyIdx) return o + 1;                 // BoolVal
        if (typeIdx == BytePropertyIdx || typeIdx == EnumPropertyIdx) return o + 8;  // EnumName FName
        if (typeIdx == ArrayPropertyIdx || typeIdx == SetPropertyIdx) return o + 8;  // InnerType FName
        if (typeIdx == MapPropertyIdx) return o + 16;                 // Key+Value types
        return o;
    }

    /// <summary>Walk the pin section (after tagged props): leading int32 + count + pins. Returns each pin's
    /// name index, PinId, and the offset of its LinkedTo count. Pins are owning-pins (full body).</summary>
    public static List<PinInfo> WalkPins(byte[] p, int pinSectionStart)
    {
        var result = new List<PinInfo>();
        int o = pinSectionStart;
        o += 4;                                  // leading int32 (0)
        int count = BitConverter.ToInt32(p, o); o += 4;
        for (int i = 0; i < count; i++)
        {
            var pin = new PinInfo();
            o += 4;                              // bNullPtr (owning => 0)
            o += 4;                              // OwningNode
            o += 16;                             // PinId (first copy)
            o += 4;                              // OwningNode (2nd copy)
            Array.Copy(p, o, pin.PinId, 0, 16); o += 16;   // PinId (2nd copy) — the canonical one
            pin.PinNameIdx = BitConverter.ToInt32(p, o); o += 8;   // PinName FName
            o = SkipFText(p, o);                 // PinFriendlyName
            o = SkipFString(p, o);               // PinToolTip
            o += 1;                              // Direction
            // FEdGraphPinType
            o += 8;                              // PinCategory FName
            o += 8;                              // PinSubCategory FName
            o += 4;                              // PinSubCategoryObject objref
            o += 1;                              // ContainerType
            o += 4;                              // bIsReference
            o += 4;                              // bIsWeakPointer
            o += 4;                              // FSimpleMemberReference.MemberParent objref
            o += 8;                              // MemberName FName
            o += 16;                             // MemberGuid
            o += 4;                              // bIsConst
            o = SkipFString(p, o);               // DefaultValue
            o = SkipFString(p, o);               // AutogeneratedDefaultValue
            o += 4;                              // DefaultObject objref
            o = SkipFText(p, o);                 // DefaultTextValue
            // LinkedTo
            pin.LinkedToCountOffset = o;
            int linked = BitConverter.ToInt32(p, o); o += 4;
            pin.LinkedToCount = linked;
            for (int k = 0; k < linked; k++) o = SkipSerializePinRef(p, o);
            // SubPins
            int sub = BitConverter.ToInt32(p, o); o += 4;
            for (int k = 0; k < sub; k++) o = SkipSerializePinRef(p, o);
            // ParentPin, ReferencePassThroughConnection (SerializePin refs)
            o = SkipSerializePinRef(p, o);
            o = SkipSerializePinRef(p, o);
            o += 16;                             // PersistentGuid
            o += 4;                              // BitField
            result.Add(pin);
        }
        return result;
    }

    // A deferred pin ref: bNullPtr int32; if 0 (non-null) then OwningNode(4)+PinId(16).
    private static int SkipSerializePinRef(byte[] p, int o)
    {
        int nullPtr = BitConverter.ToInt32(p, o); o += 4;
        if (nullPtr == 0) o += 4 + 16;
        return o;
    }
}
