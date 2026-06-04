namespace UE4Decompiler.Output.Writer;

/// <summary>
/// Port of UE4.21 <c>FCrc</c> hashing, required to emit correct serialized name-pool hashes
/// (<c>FNameEntrySerialized</c> stores NonCasePreserving + CasePreserving 16-bit hashes, and the
/// 4.21 linker uses them to bucket names on load — wrong hashes break cross-references).
///
/// Source: Engine/Source/Runtime/Core/{Public/Misc/Crc.h, Private/Misc/Crc.cpp, Private/UObject/UnrealNames.cpp}
///   NonCasePreservingHash = FCrc::Strihash_DEPRECATED(name) &amp; 0xFFFF   (table poly 0x04C11DB7, non-reflected)
///   CasePreservingHash    = FCrc::StrCrc32(name)            &amp; 0xFFFF   (table poly 0xEDB88320, reflected)
/// </summary>
public static class FCrc
{
    private const uint Crc32Poly = 0x04C11DB7;   // forward poly used by the deprecated table
    private const uint RCrc32Poly = 0xEDB88320;  // ReverseBits(0x04C11DB7), used by StrCrc32's SB8 table

    private static readonly uint[] CRCTableDeprecated = BuildDeprecatedTable();
    private static readonly uint[] CRCTableSB8 = BuildSB8Table();

    private static uint[] BuildDeprecatedTable()
    {
        var table = new uint[256];
        for (uint iCRC = 0; iCRC < 256; iCRC++)
        {
            uint c = iCRC << 24;
            for (var j = 8; j != 0; j--)
                c = (c & 0x80000000) != 0 ? (c << 1) ^ Crc32Poly : c << 1;
            table[iCRC] = c;
        }
        return table;
    }

    private static uint[] BuildSB8Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ RCrc32Poly : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// NonCasePreservingHash source. CRITICAL: the engine hashes the name using the char width it is
    /// STORED as — FNameEntrySerialized saves GetRawNonCasePreservingHash(AnsiName) for ANSI names
    /// (1 byte/char) and the WIDECHAR overload (2 bytes/char: low then high) for wide names. Hashing an
    /// ANSI-stored name with the wide path yields a different value, so on load FName buckets it wrong and
    /// it never matches the canonical hardcoded names (NAME_Package, NAME_Class, …) — every import then
    /// fails VerifyImportInner's "inappropriate outermost" check. So pick the path to match storage.
    /// </summary>
    public static uint Strihash_DEPRECATED(string data, bool wide)
    {
        uint hash = 0;
        foreach (var rawCh in data)
        {
            var ch = (ushort)char.ToUpperInvariant(rawCh);
            var b = (byte)(ch & 0xFF);
            hash = ((hash >> 8) & 0x00FFFFFF) ^ CRCTableDeprecated[(hash ^ b) & 0xFF];
            if (wide)
            {
                b = (byte)(ch >> 8);
                hash = ((hash >> 8) & 0x00FFFFFF) ^ CRCTableDeprecated[(hash ^ b) & 0xFF];
            }
        }
        return hash;
    }

    /// <summary>CasePreservingHash source: reflected CRC32, 4 byte-iterations per char (matches the wide overload).</summary>
    public static uint StrCrc32(string data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var rawCh in data)
        {
            uint ch = rawCh;
            for (var k = 0; k < 4; k++)
            {
                crc = (crc >> 8) ^ CRCTableSB8[(crc ^ ch) & 0xFF];
                ch >>= 8;
            }
        }
        return ~crc;
    }

    /// <summary>A name is stored as ANSI iff every char is 7-bit (matches FArchiveWriter.WriteFString).</summary>
    public static bool IsStoredWide(string name) => !name.All(c => c < 128);

    public static ushort NonCasePreservingHash(string name) =>
        (ushort)(Strihash_DEPRECATED(name, IsStoredWide(name)) & 0xFFFF);
    public static ushort CasePreservingHash(string name) => (ushort)(StrCrc32(name) & 0xFFFF);
}
