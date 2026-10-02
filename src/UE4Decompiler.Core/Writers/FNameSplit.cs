namespace UE4Decompiler.Output.Writer;

/// <summary>
/// UE FName trailing-number split. An FName ending in "_&lt;digits&gt;" is stored as a BASE name + an
/// integer Number (the name table holds "Foo", references carry the number). The SERIALIZED number is the
/// INTERNAL number = external value + 1 (0 = NAME_NO_NUMBER = no suffix).
///
/// WHY THIS MATTERS: the editor builds a package's FName from its FILE PATH for loose/.pak packages, which
/// applies this split — so "/Game/.../MI_Wall_Panel_10" registers as FName("/Game/.../MI_Wall_Panel", 11).
/// If our writer instead stores the literal "MI_Wall_Panel_10" with number 0, the two FNames DISPLAY
/// identically and hash to the same FPackageId but are different FName entries -> "FPackageId collision:
/// … for both …MI_Wall_Panel_10 and …MI_Wall_Panel_10". Splitting here makes our FNames match UE's.
///
/// UE rule (UnrealNames): trailing run of digits, immediately preceded by '_', with a non-empty base, and
/// NOT leading-zero (a single '0' is fine, "_01"/"_007" are not), and small enough to fit int32.
/// </summary>
public static class FNameSplit
{
    /// <summary>Returns (baseName, internalNumber). internalNumber 0 means "no numeric suffix".</summary>
    public static (string baseName, int number) Split(string name)
    {
        if (string.IsNullOrEmpty(name)) return (name, 0);
        int len = name.Length;
        int i = len;
        while (i > 0 && name[i - 1] >= '0' && name[i - 1] <= '9') i--;
        int digitCount = len - i;
        if (digitCount == 0) return (name, 0);              // no trailing digits
        if (i < 2 || name[i - 1] != '_') return (name, 0);  // must be "_<digits>" with a non-empty base
        if (digitCount > 1 && name[i] == '0') return (name, 0);  // leading zero (except single '0')
        if (digitCount > 10) return (name, 0);              // would overflow int32
        if (!long.TryParse(name.AsSpan(i), out var val) || val > int.MaxValue - 1) return (name, 0);
        return (name.Substring(0, i - 1), (int)val + 1);    // internal = external + 1
    }
}
