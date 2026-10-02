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
        public byte Direction;
        public byte[] PinId = new byte[16];
        public int LinkedToCountOffset;   // byte offset (within payload) of the LinkedTo int32 count
        public int LinkedToCount;
        /// <summary>Owning-node FPackageIndex values of the LinkedTo peers (order-matched).</summary>
        public List<int> LinkedNodes = new();
        public int CategoryIdx = -1;
        public string DefaultValue = "";
    }

    private static int SkipFString(byte[] p, int o)
    {
        int len = BitConverter.ToInt32(p, o); o += 4;
        if (len > 0) o += len;                 // ANSI incl null
        else if (len < 0) o += (-len) * 2;     // UTF-16 incl null
        return o;
    }

    /// <summary>Read an FString without advancing semantics for diagnostics (value + end offset).</summary>
    public static (string value, int end) PeekFString(byte[] p, int o)
    {
        int len = BitConverter.ToInt32(p, o); o += 4;
        if (len <= 0) return ("", o);
        int take = Math.Min(len - 1, Math.Max(0, p.Length - o));
        var sb = new System.Text.StringBuilder(take);
        for (int i = 0; i < take; i++)
        {
            char c = (char)p[o + i];
            if (c == 0) break;
            sb.Append(char.IsControl(c) ? '?' : c);
        }
        return (sb.ToString(), o + len);
    }

    private static int SkipFText(byte[] p, int o)
    {
        o += 4;                                 // Flags (int32)
        sbyte hist = unchecked((sbyte)p[o]); o += 1;   // HistoryType
        if (hist == -1)
        {
            // UE5 None-history texts carry bHasCultureInvariantString (int32, false here).
            if (IsUe5) o += 4;
            return o;                           // empty/INVALID — done
        }
        if (hist == 0)                          // Base: Namespace, Key, SourceString (FStrings)
        { o = SkipFString(p, o); o = SkipFString(p, o); o = SkipFString(p, o); return o; }
        throw new NotSupportedException($"FText HistoryType {hist} not handled at {o}");
    }

    /// <summary>Walk the tagged-property stream; return offset of the byte just past the terminating None.</summary>
    public static int SkipTaggedProperties(byte[] p, int o, int noneIdx)
    {
        if (IsUe5)
        {
            int nc = NameCount > 0 ? NameCount : int.MaxValue / 2;
            while (true)
            {
                int nameIdx = BitConverter.ToInt32(p, o);
                if (nameIdx == noneIdx) return o + 8;   // None terminator (bare FName)
                if (!ParseTag5(p, o, nc, out _, out _, out _, out _, out int end, out _))
                    throw new InvalidOperationException($"Bad UE5 property tag at {o}");
                o = end;
            }
        }
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

    /// <summary>
    /// Parse one UE5 property tag (Name + type-name tree + Size + flags + optional ArrayIndex/Guid).
    /// Returns false when the header doesn't validate (caller stops). Outputs the value offset and
    /// the offset just past the value (tag end).
    /// </summary>
    public static bool ParseTag5(byte[] p, int o, int nameCount,
        out int nameIdx, out int size, out byte flags, out int valueOffset, out int tagEnd)
        => ParseTag5(p, o, nameCount, out nameIdx, out size, out flags, out valueOffset, out tagEnd, out _);

    /// <summary>
    /// Parse one UE5 property tag (Name + type-name tree + Size + flags + optional ArrayIndex/Guid).
    /// Returns false when the header doesn't validate (caller stops). Outputs the value offset and
    /// the offset just past the value (tag end).</summary>
    public static bool ParseTag5(byte[] p, int o, int nameCount,
        out int nameIdx, out int size, out byte flags, out int valueOffset, out int tagEnd, out int sizePos)
    {
        nameIdx = -1; size = 0; flags = 0; valueOffset = -1; tagEnd = -1; sizePos = -1;
        try
        {
            if (o + 8 > p.Length) return false;
            nameIdx = BitConverter.ToInt32(p, o);
            if (nameIdx < 0 || nameIdx >= nameCount) return false;
            o += 8;
            // Type-name tree: (FName, InnerCount)* — validated node by node.
            int remaining = 1;
            do
            {
                if (o + 12 > p.Length) return false;
                int t = BitConverter.ToInt32(p, o);
                if (t < 0 || t >= nameCount) return false;
                int inner = BitConverter.ToInt32(p, o + 8);
                if (inner < 0 || inner > 64) return false;
                o += 12;
                remaining += inner - 1;
            } while (remaining > 0);
            if (o + 5 > p.Length) return false;
            sizePos = o;
            size = BitConverter.ToInt32(p, o); o += 4;
            if (size < 0 || size > p.Length) return false;
            flags = p[o]; o += 1;
            if ((flags & 0x01) != 0) o += 4;    // HasArrayIndex
            if ((flags & 0x02) != 0) o += 16;   // HasPropertyGuid
            if ((flags & 0x04) != 0) o += 1;    // HasPropertyExtensions (extension id; payload rarely follows in our shapes)
            valueOffset = o;
            tagEnd = o + size;
            return tagEnd >= o && tagEnd <= p.Length;
        }
        catch { return false; }
    }

    /// <summary>End offset (past value) of the UE5 tag starting at <paramref name="o"/>.</summary>
    public static int TagEnd5(byte[] p, int o)
    {
        // NameCount unknown here — use a permissive bound; callers that know it should use ParseTag5.
        return TagEnd5(p, o, int.MaxValue / 2);
    }

    public static int TagEnd5(byte[] p, int o, int nameCount)
    {
        if (!ParseTag5(p, o, nameCount, out _, out _, out _, out _, out int end))
            throw new InvalidOperationException($"Bad UE5 property tag at {o}");
        return end;
    }

    /// <summary>
    /// Scan a UE5 tag stream for the first tag named <paramref name="nameIdx"/> whose type tree opens
    /// with <paramref name="type0"/> (pass -1 to match any type). Stops at the None terminator.
    /// Returns tag start, Size field offset, value offset and tag end.
    /// NameCount comes from the ThreadStatic (set alongside type indices).
    /// </summary>
    public static bool FindTag5(byte[] p, int noneIdx, int nameIdx, int type0,
        out int tagStart, out int sizePos, out int valueOff, out int tagEnd)
    {
        tagStart = -1; sizePos = -1; valueOff = -1; tagEnd = -1;
        int nc = NameCount > 0 ? NameCount : int.MaxValue / 2;
        int o = 0;
        while (o + 8 <= p.Length)
        {
            int n = BitConverter.ToInt32(p, o);
            if (n == noneIdx) return false;
            if (n < 0 || n >= nc) return false;
            tagStart = o;
            // Peek the tree's first type node without full validation.
            int t0 = -1;
            if (o + 20 <= p.Length) t0 = BitConverter.ToInt32(p, o + 8);
            if (!ParseTag5(p, o, nc, out n, out _, out _, out int vOff, out int end, out int sPos))
                return false;
            if (n == nameIdx && (type0 < 0 || t0 == type0))
            {
                sizePos = sPos; valueOff = vOff; tagEnd = end;
                return true;
            }
            o = end;
            if (end <= tagStart) return false; // no progress guard
        }
        return false;
    }
    /// <summary>Walk the tagged-property stream and return the byte span [start,end) of the first property whose
    /// name index == targetNameIdx (the full tag header + value). Returns (-1,-1) if not found. Used to splice a
    /// rebuilt StaticMaterials array into a cloned StaticMesh export without disturbing its other properties.</summary>
    public static (int start, int end) FindPropertySpan(byte[] p, int o, int noneIdx, int targetNameIdx)
    {
        if (IsUe5)
        {
            // None terminator is a bare FName (no type tree) — peek before parsing.
            int nc = NameCount > 0 ? NameCount : int.MaxValue / 2;
            while (o + 8 <= p.Length)
            {
                int n = BitConverter.ToInt32(p, o);
                if (n == noneIdx) return (-1, -1);
                if (!ParseTag5(p, o, nc, out n, out _, out _, out _, out int end)) return (-1, -1);
                if (n == targetNameIdx) return (o, end);
                o = end;
            }
            return (-1, -1);
        }
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
    /// <summary>5.x pin bodies carry SourceIndex (after FriendlyName) + bIsUObjectWrapper (end of pin type).</summary>
    [ThreadStatic] public static bool IsUe5;
    /// <summary>Name-table count for tag validation (set alongside the type indices).</summary>
    [ThreadStatic] public static int NameCount;

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
            o += 4;                              // OwningNode (2nd copy, present in 4.x and 5.x)
            Array.Copy(p, o, pin.PinId, 0, 16); o += 16;   // PinId (2nd copy) — the canonical one
            pin.PinNameIdx = BitConverter.ToInt32(p, o); o += 8;   // PinName FName
            o = SkipFText(p, o);                 // PinFriendlyName
            if (IsUe5) o += 4;                   // SourceIndex (UE5+)
            o = SkipFString(p, o);               // PinToolTip
            pin.Direction = p[o]; o += 1;        // Direction
            // FEdGraphPinType
            int catStart = o;
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
            if (IsUe5) o += 4;                   // bIsUObjectWrapper (UE5+)
            if (IsUe5) o += 4;                   // bSerializeAsSinglePrecisionFloat (UE5+)
            pin.CategoryIdx = BitConverter.ToInt32(p, catStart);
            var (dv, dvEnd) = PeekFString(p, o);
            pin.DefaultValue = dv;
            o = SkipFString(p, o);               // DefaultValue
            o = SkipFString(p, o);               // AutogeneratedDefaultValue
            o += 4;                              // DefaultObject objref
            o = SkipFText(p, o);                 // DefaultTextValue
            // LinkedTo
            pin.LinkedToCountOffset = o;
            int linked = BitConverter.ToInt32(p, o); o += 4;
            pin.LinkedToCount = linked;
            for (int k = 0; k < linked; k++) { pin.LinkedNodes.Add(BitConverter.ToInt32(p, o + 4)); o = SkipSerializePinRef(p, o); }
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

    /// <summary>
    /// Locate the tagged-property stream inside an export payload. UE5 editor payloads carry a short
    /// preamble (observed: 1 zero byte) before the first tag; 4.21 payloads start with the tag directly.
    /// Auto-detected per payload by validating the tag header (name index in range, sane number), so
    /// neither version needs hardcoding: offset 0 wins when valid (4.21 + empty 5.x streams), else the
    /// first valid header wins. Returns 0 when nothing validates (caller falls back to verbatim copy).
    /// </summary>
    public static int FindPayloadStart(byte[] p, int nameCount)
    {
        if (p.Length >= 24 && ValidTagHeader(p, 0, nameCount)) return 0;
        // Preamble is short; scan the first few bytes for the first valid header.
        for (int o = 1; o <= 8 && o + 24 <= p.Length; o++)
            if (ValidTagHeader(p, o, nameCount)) return o;
        return 0;
    }

    private static bool ValidTagHeader(byte[] p, int o, int nameCount)
    {
        int idx = BitConverter.ToInt32(p, o);
        int num = BitConverter.ToInt32(p, o + 4);
        int type = BitConverter.ToInt32(p, o + 8);
        int typenum = BitConverter.ToInt32(p, o + 12);
        return idx >= 0 && idx < nameCount && num >= 0 && num < 100000
            && type >= 0 && type < nameCount && typenum >= 0 && typenum < 100000;
    }
}
