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
    private readonly bool _ue5;                 // UE5 tag format (type-name tree + flags byte)

    public TaggedPropertyWriter(FArchiveWriter w, Func<string, int> nameAdder, bool ue5 = false) { _w = w; _name = nameAdder; _ue5 = ue5; }

    // Split a trailing "_<number>" into the FName number field (UE convention) so names like "…_10" encode the same
    // way the editor builds file-derived FNames — otherwise they collide (FPackageId assert). See FNameSplit.
    private void FName(string s)
    {
        var (baseName, number) = FNameSplit.Split(PackagePathCanon.Normalize(s));
        _w.Write(_name(baseName)); _w.Write(number);
    }

    /// <summary>Write tag header up to (not including) the value; returns the file offset of Size for backpatch.</summary>
    private long BeginTag(string name, string type, Action? extra = null)
    {
        FName(name);
        if (_ue5)
        {
            // UE5: single-node type tree + Size + zero flags. The 4.x type tag-data (extra) has no
            // 5.x equivalent for plain types; struct/enum/bool/array shapes use BeginTag5 directly.
            WriteTypeNode(type, 0);
            var sizeOff = _w.Position;
            _w.Write(0);            // Size placeholder (backpatched in EndTag)
            _w.Write((byte)0);      // flags = None
            return sizeOff;
        }
        FName(type);
        var sizeOff4 = _w.Position;
        _w.Write(0);            // Size placeholder (backpatched in EndTag)
        _w.Write(0);            // ArrayIndex
        extra?.Invoke();        // struct/bool/enum/array tag-data
        _w.WriteByteBool(false); // HasPropertyGuid = 0
        return sizeOff4;
    }

    /// <summary>UE5 tag header with an explicit type tree and flags. Returns Size offset for backpatch.</summary>
    private long BeginTag5(string name, IReadOnlyList<(string type, int inner)> tree, byte flags)
    {
        FName(name);
        WriteTypeTree(tree);
        var sizeOff = _w.Position;
        _w.Write(0);            // Size placeholder (backpatched in EndTag)
        _w.Write(flags);
        return sizeOff;
    }

    private void WriteTypeNode(string type, int inner)
    {
        FName(type);
        _w.Write(inner);
    }

    private void WriteTypeTree(IReadOnlyList<(string type, int inner)> tree)
    {
        foreach (var (type, inner) in tree) WriteTypeNode(type, inner);
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

    public void Int64(string name, long v)
    {
        var so = BeginTag(name, "Int64Property"); var vs = _w.Position;
        _w.Write(v);
        EndTag(so, vs);
    }

    /// <summary>UInt64 property (e.g. FBPVariableDescription.PropertyFlags) — 8 bytes LE like Int64.</summary>
    public void UInt64(string name, ulong v)
    {
        var so = BeginTag(name, "UInt64Property"); var vs = _w.Position;
        _w.Write((long)v);
        EndTag(so, vs);
    }

    public void Float(string name, float v)
    {
        var so = BeginTag(name, "FloatProperty"); var vs = _w.Position;
        _w.Write(v);
        EndTag(so, vs);
    }

    public void Double(string name, double v)
    {
        var so = BeginTag(name, "DoubleProperty"); var vs = _w.Position;
        _w.Write(v);
        EndTag(so, vs);
    }

    public void Bool(string name, bool v)
    {
        if (_ue5)
        {
            // UE5 bool value lives in the flags (BoolTrue); no value bytes, Size 0.
            var so = BeginTag5(name, [( "BoolProperty", 0 )], v ? (byte)0x10 : (byte)0x00);
            var vs = _w.Position;
            EndTag(so, vs);
            return;
        }
        // Bool value lives in the tag (BoolVal); value section is empty (Size 0).
        var so4 = BeginTag(name, "BoolProperty", () => _w.WriteByteBool(v)); var vs4 = _w.Position;
        EndTag(so4, vs4);
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

    public void Text(string name, string ns, string key, string source)
    {
        var so = BeginTag(name, "TextProperty"); var vs = _w.Position;
        _w.Write(0u);              // ETextFlag::None
        _w.Write((byte)0);         // ETextHistoryType::Base
        _w.WriteFString(ns);
        _w.WriteFString(key);
        _w.WriteFString(source);
        EndTag(so, vs);
    }

    /// <summary>SoftObjectProperty (e.g. ULevelStreaming.WorldAsset). 4.21 FSoftObjectPath = FName AssetPathName +
    /// FString SubPathString. assetPath is the full "/Game/Path/Map.Map" object path; SubPath is empty for a map.</summary>
    public void SoftObject(string name, string assetPath)
    {
        // UE5 FSoftObjectPath = FTopLevelAssetPath (PackageName FName + AssetName FName) + SubPathString.
        if (_ue5)
        {
            var so = BeginTag5(name, [( "SoftObjectProperty", 0 )], 0x00);
            var vs = _w.Position;
            var dot = assetPath.LastIndexOf('.');
            if (dot > 0) { FName(assetPath.Substring(0, dot)); FName(assetPath.Substring(dot + 1)); }
            else { FName(assetPath); FName("None"); }
            _w.WriteFString("");
            EndTag(so, vs);
            return;
        }
        var so4 = BeginTag(name, "SoftObjectProperty"); var vs4 = _w.Position;
        FName(assetPath);          // AssetPathName
        _w.WriteFString("");       // SubPathString
        EndTag(so4, vs4);
    }

    /// <summary>UE5 double-precision vector/rotator struct (FVector/FRotator are 3×double in UE5; 24 bytes).</summary>
    public void VectorD(string name, float x, float y, float z)
        => Struct(name, "Vector", "/Script/CoreUObject",
            () => { _w.Write((double)x); _w.Write((double)y); _w.Write((double)z); }, 0x08);

    /// <summary>UE5 double-precision rotator struct (24 bytes).</summary>
    public void RotatorD(string name, float pitch, float yaw, float roll)
        => Struct(name, "Rotator", "/Script/CoreUObject",
            () => { _w.Write((double)pitch); _w.Write((double)yaw); _w.Write((double)roll); }, 0x08);

    /// <summary>Enum/byte property — value is the enum entry's FName (e.g. "ENodeEnabledState::Disabled").</summary>
    public void Enum(string name, string enumTypeName, string entryName)
    {
        // UE5 ground truth (5.5 DirectionalLight actor): 4-node tree
        // [(EnumProperty,2),(EnumType,1),(EnumPackage,0),(ByteProperty,0)].
        if (_ue5)
        {
            var so = BeginTag5(name, [( "EnumProperty", 2 ), ( enumTypeName, 1 ), ( "/Script/Engine", 0 ), ( "ByteProperty", 0 )], 0x00);
            var vs = _w.Position;
            FName(entryName);
            EndTag(so, vs);
            return;
        }
        var so4 = BeginTag(name, "EnumProperty", () => FName(enumTypeName)); var vs4 = _w.Position;
        FName(entryName);
        EndTag(so4, vs4);
    }

    /// <summary>ByteProperty backed by a UEnum (TEnumAsByte) — tag carries EnumName, value is the entry FName.
    /// Used for USceneComponent.Mobility (EComponentMobility::Type) so placed components can be marked Movable.</summary>
    public void ByteEnum(string name, string enumTypeName, string entryName)
    {
        // UE5 ground truth (5.5-saved maps): 3-node tree [(ByteProperty,1),(EnumType,1),(EnumPackage,0)].
        // The enum package is /Script/Engine for all component enums used here.
        if (_ue5)
        {
            var so = BeginTag5(name, [( "ByteProperty", 1 ), ( enumTypeName, 1 ), ( "/Script/Engine", 0 )], 0x00);
            var vs = _w.Position;
            FName(entryName);
            EndTag(so, vs);
            return;
        }
        var so4 = BeginTag(name, "ByteProperty", () => FName(enumTypeName)); var vs4 = _w.Position;
        FName(entryName);
        EndTag(so4, vs4);
    }

    public void Byte(string name, byte v)
    {
        var so = BeginTag(name, "ByteProperty", () => FName("None")); var vs = _w.Position;
        _w.Write(v);
        EndTag(so, vs);
    }

    /// <summary>FGuid struct property (16 bytes) — used for NodeGuid/GraphGuid/BlueprintGuid/PinId.</summary>
    public void GuidStruct(string name, FGuid16 g)
    {
        if (_ue5)
        {
            // UE5: struct tree names the Guid type in CoreUObject; native FGuid payload (16 raw bytes).
            var so = BeginTag5(name, [( "StructProperty", 1 ), ( "Guid", 1 ), ( "/Script/CoreUObject", 0 )], 0x08);
            var vs = _w.Position;
            _w.Write(g.A); _w.Write(g.B); _w.Write(g.C); _w.Write(g.D);
            EndTag(so, vs);
            return;
        }
        var so4 = BeginTag(name, "StructProperty", () => { FName("Guid"); _w.WriteBytes(new byte[16]); }); // StructName=Guid, StructGuid=0
        var vs4 = _w.Position;
        _w.Write(g.A); _w.Write(g.B); _w.Write(g.C); _w.Write(g.D);
        EndTag(so4, vs4);
    }

    public void ColorStruct(string name, CUE4Parse.UE4.Objects.Core.Math.FColor c)
    {
        // UE5: FColor struct in CoreUObject, native-struct flags (ground truth: light components).
        if (_ue5)
        {
            var so = BeginTag5(name, [( "StructProperty", 1 ), ( "Color", 1 ), ( "/Script/CoreUObject", 0 )], 0x08);
            var vs = _w.Position;
            _w.Write(c.B); _w.Write(c.G); _w.Write(c.R); _w.Write(c.A);
            EndTag(so, vs);
            return;
        }
        var so4 = BeginTag(name, "StructProperty", () => { FName("Color"); _w.WriteBytes(new byte[16]); });
        var vs4 = _w.Position;
        _w.Write(c.B); _w.Write(c.G); _w.Write(c.R); _w.Write(c.A);
        EndTag(so4, vs4);
    }

    public void BoxSphereBounds(string name, MeshWriter.MeshBounds b)
    {
        var so = BeginTag(name, "StructProperty", () => { FName("BoxSphereBounds"); _w.WriteBytes(new byte[16]); });
        var vs = _w.Position;
        // UE5-saved StaticMesh assets serialize BoxSphereBounds here as a fallback tagged struct, not as the
        // native 56-byte FBoxSphereBounds3d blob: Origin(Vector), BoxExtent(Vector), SphereRadius(Double), None.
        Struct("Origin", "Vector", () =>
        {
            _w.Write((double)b.OriginX); _w.Write((double)b.OriginY); _w.Write((double)b.OriginZ);
        });
        Struct("BoxExtent", "Vector", () =>
        {
            _w.Write((double)b.ExtentX); _w.Write((double)b.ExtentY); _w.Write((double)b.ExtentZ);
        });
        Double("SphereRadius", b.SphereRadius);
        WriteNone();
        EndTag(so, vs);
    }

    /// <summary>Array of object references (e.g. EventGraph.Nodes[], Blueprint.UbergraphPages[]).</summary>
    public void ObjectArray(string name, IReadOnlyList<int> packageIndices)
    {
        // UE5 ground truth (5.5 maps): tree [(ArrayProperty,1),(ObjectProperty,0)]; empty arrays still
        // carry a trailing int32 0 after the count (8 bytes total).
        if (_ue5)
        {
            var so = BeginTag5(name, [( "ArrayProperty", 1 ), ( "ObjectProperty", 0 )], 0x00);
            var vs = _w.Position;
            _w.Write(packageIndices.Count);
            if (packageIndices.Count == 0) _w.Write(0);
            foreach (var pi in packageIndices) _w.Write(pi);
            EndTag(so, vs);
            return;
        }
        var so4 = BeginTag(name, "ArrayProperty", () => FName("ObjectProperty")); var vs4 = _w.Position;
        _w.Write(packageIndices.Count);
        foreach (var pi in packageIndices) _w.Write(pi);
        EndTag(so4, vs4);
    }

    public void Int64Array(string name, IReadOnlyList<long> values)
    {
        var so = BeginTag(name, "ArrayProperty", () => FName("Int64Property")); var vs = _w.Position;
        _w.Write(values.Count);
        foreach (var v in values) _w.Write(v);
        EndTag(so, vs);
    }

    public void ByteArray(string name, IReadOnlyList<byte> values)
    {
        var so = BeginTag(name, "ArrayProperty", () => FName("ByteProperty")); var vs = _w.Position;
        _w.Write(values.Count);
        foreach (var v in values) _w.Write(v);
        EndTag(so, vs);
    }

    /// <summary>Array of TEnumAsByte&lt;Enum&gt; values — inner type is ByteProperty but each element is the enum
    /// ENTRY FName (8 bytes), not a raw byte (verified against an editor-saved FTextureSource.LayerFormat).</summary>
    public void ByteEnumArray(string name, IReadOnlyList<string> entryNames)
    {
        var so = BeginTag(name, "ArrayProperty", () => FName("ByteProperty")); var vs = _w.Position;
        _w.Write(entryNames.Count);
        foreach (var e in entryNames) FName(e);
        EndTag(so, vs);
    }

    /// <summary>UStaticMesh.StaticMaterials — an ArrayProperty of FStaticMaterial structs (4.21 array-of-struct
    /// layout: count, one inner StructProperty tag, then each element = MaterialInterface(Object) + MaterialSlotName
    /// (Name) + None). Editor recomputes UVChannelData. Slot i's MaterialInterface = matImports[i] (FPackageIndex).</summary>
    public void StaticMaterialsArray(IReadOnlyList<(int matImport, string slot)> mats)
    {
        var so = BeginTag("StaticMaterials", "ArrayProperty", () => FName("StructProperty")); var vs = _w.Position;
        _w.Write(mats.Count);
        // inner element-type tag (FPropertyTag header for the struct element type)
        FName("StaticMaterials"); FName("StructProperty");
        var innerSizeOff = _w.Position; _w.Write(0); _w.Write(0);     // Size (backpatched) + ArrayIndex
        FName("StaticMaterial"); _w.WriteBytes(new byte[16]);         // StructName + StructGuid
        _w.WriteByteBool(false);                                      // HasPropertyGuid
        var elemStart = _w.Position;
        foreach (var (matImport, slot) in mats)
        {
            Object("MaterialInterface", matImport);
            Name("MaterialSlotName", slot);
            WriteNone();
        }
        var elemEnd = _w.Position;
        _w.Seek(innerSizeOff); _w.Write((int)(elemEnd - elemStart)); _w.Seek(elemEnd);   // inner tag Size = element bytes
        EndTag(so, vs);
    }

    /// <summary>UStaticMesh.SectionInfoMap / OriginalSectionInfoMap — a StructProperty "MeshSectionInfoMap" whose
    /// "Map" (TMap&lt;uint32 section, FMeshSectionInfo&gt;) maps each render section to its material slot. Required so
    /// the editor's mesh build keeps N material slots instead of collapsing to slot 0. sectionToMaterial[i] = the
    /// material index for section i (identity for one-section-per-material meshes).</summary>
    public void SectionInfoMap(string propName, IReadOnlyList<int> sectionToMaterial)
    {
        var so = BeginTag(propName, "StructProperty", () => { FName("MeshSectionInfoMap"); _w.WriteBytes(new byte[16]); });
        var vs = _w.Position;
        // "Map" MapProperty (key=UInt32Property, value=StructProperty FMeshSectionInfo)
        FName("Map"); FName("MapProperty");
        var mapSizeOff = _w.Position; _w.Write(0); _w.Write(0);   // Size (backpatched) + ArrayIndex
        FName("UInt32Property"); FName("StructProperty");          // MapProperty tag-data: KeyType + ValueType
        _w.WriteByteBool(false);                                   // HasPropertyGuid
        var mapValStart = _w.Position;
        _w.Write(0);                                               // NumKeysToRemove
        _w.Write(sectionToMaterial.Count);                         // NumEntries
        for (int i = 0; i < sectionToMaterial.Count; i++)
        {
            _w.Write(i);                                           // key: section index (uint32)
            Int("MaterialIndex", sectionToMaterial[i]);            // value struct: tagged props + None
            Bool("bEnableCollision", true);
            Bool("bCastShadow", true);
            WriteNone();
        }
        var mapValEnd = _w.Position;
        _w.Seek(mapSizeOff); _w.Write((int)(mapValEnd - mapValStart)); _w.Seek(mapValEnd);
        WriteNone();                                               // end of MeshSectionInfoMap struct value
        EndTag(so, vs);
    }

    /// <summary>UBlueprint.NewVariables — the editor's variable list (My Blueprint panel). ArrayProperty of
    /// FBPVariableDescription structs (4.21): per element VarName(Name) + VarGuid(Guid) + VarType(FEdGraphPinType) +
    /// FriendlyName(Str) + None. Only simple value-type categories are emitted here (bool/int/float/string/text/
    /// name/byte) — those have an all-default FEdGraphPinType so no class/struct sub-reference is needed.</summary>
    public void NewVariables(IReadOnlyList<(string name, string category)> vars)
    {
        var so = BeginTag("NewVariables", "ArrayProperty", () => FName("StructProperty")); var vs = _w.Position;
        _w.Write(vars.Count);
        FName("NewVariables"); FName("StructProperty");                 // inner element-type tag
        var innerSizeOff = _w.Position; _w.Write(0); _w.Write(0);       // Size (backpatched) + ArrayIndex
        FName("BPVariableDescription"); _w.WriteBytes(new byte[16]);    // StructName + StructGuid
        _w.WriteByteBool(false);                                        // HasPropertyGuid
        var elemStart = _w.Position;
        foreach (var (name, category) in vars)
        {
            Name("VarName", name);
            GuidStruct("VarGuid", FGuid16.NewGuid());
            Struct("VarType", "EdGraphPinType", () => WritePinType(category));
            Str("FriendlyName", name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name);
            WriteNone();
        }
        var elemEnd = _w.Position;
        _w.Seek(innerSizeOff); _w.Write((int)(elemEnd - elemStart)); _w.Seek(elemEnd);
        EndTag(so, vs);
    }

    /// <summary>FEdGraphPinType value for a simple value-type pin: PinCategory FName,
    /// PinSubCategory None, PinSubCategoryObject(0), ContainerType byte(None), bIsReference/bIsWeakPointer (int32),
    /// FSimpleMemberReference (MemberParent i32 + MemberName None + MemberGuid 16B), bIsConst (int32),
    /// plus bIsUObjectWrapper/bSerializeAsSinglePrecisionFloat on UE5 (69B total vs 61B on 4.x).
    /// Bools are int32-width in this native struct; verified byte-exact against an editor-saved blueprint.</summary>
    private void WritePinType(string category)
    {
        FName(category);                 // PinCategory
        FName("None");                   // PinSubCategory
        _w.Write(0);                     // PinSubCategoryObject (FPackageIndex)
        _w.WriteByteBool(false);         // ContainerType (uint8 EPinContainerType = None)
        _w.Write(0);                     // bIsReference (int32)
        _w.Write(0);                     // bIsWeakPointer (int32)
        _w.Write(0);                     // FSimpleMemberReference.MemberParent (FPackageIndex)
        FName("None");                   // FSimpleMemberReference.MemberName
        _w.WriteBytes(new byte[16]);     // FSimpleMemberReference.MemberGuid
        _w.Write(0);                     // bIsConst (int32)
        if (_ue5) { _w.Write(0); _w.Write(0); }   // bIsUObjectWrapper + bSerializeAsSinglePrecisionFloat (UE5+;
                                          // without these the value is 61B vs the 69B the 5.x linker reads,
                                          // overrunning into FriendlyName (BPVariableDescription:VarType Read 101B/69B))
    }

    /// <summary>Generic struct property with caller-written value bytes (for non-Guid structs).</summary>
    public void Struct(string name, string structName, Action writeValue)
    {
        if (_ue5) throw new InvalidOperationException(
            $"Struct '{name}' needs an explicit struct package on the UE5 path; use Struct(name, structName, structPackage, writeValue).");
        var so = BeginTag(name, "StructProperty", () => { FName(structName); _w.WriteBytes(new byte[16]); });
        var vs = _w.Position;
        writeValue();
        EndTag(so, vs);
    }

    /// <summary>UE5 struct property: type tree names the struct + its package (e.g. MemberReference in
    /// /Script/Engine). Children are caller-written tagged properties, same as 4.x values. Native-struct
    /// values (FGuid, FEdGraphPinType) carry flags 0x08; USTRUCT-tagged values use 0x00.</summary>
    public void Struct(string name, string structName, string structPackage, Action writeValue, byte flags = 0x00)
    {
        if (!_ue5) { Struct(name, structName, writeValue); return; }
        var so = BeginTag5(name, [( "StructProperty", 1 ), ( structName, 1 ), ( structPackage, 0 )], flags);
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
