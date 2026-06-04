using System.Globalization;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Versions;
using Serilog;

namespace UE4Decompiler.Utils;

/// <summary>
/// Resolves an <see cref="EGame"/> / engine-version string either from a user-supplied
/// hint (e.g. "4.27", "5.1") or by sniffing the package file version of the first asset.
/// </summary>
public static class VersionDetector
{
    /// <summary>Parse a "4.27" / "5.1" style hint into an <see cref="EGame"/>. Returns null if unparseable.</summary>
    public static EGame? FromHint(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;

        var cleaned = hint.Trim().TrimStart('v', 'V', 'u', 'U', 'e', 'E', ' ');
        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major)) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor)) return null;

        var name = major switch
        {
            4 => $"GAME_UE4_{minor}",
            5 => $"GAME_UE5_{minor}",
            _ => null
        };
        if (name is null) return null;

        return Enum.TryParse<EGame>(name, out var game) ? game : null;
    }

    /// <summary>Map an <see cref="EGame"/> back to the "4.27" string used by .uproject EngineAssociation.</summary>
    public static string ToEngineAssociation(EGame game)
    {
        // Enum names look like GAME_UE4_27 / GAME_UE5_1. Derive "major.minor" from the name.
        var name = game.ToString();
        var marker = name.IndexOf("_UE", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var tail = name[(marker + 3)..]; // "4_27"
            var bits = tail.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length >= 2) return $"{bits[0]}.{bits[1]}";
        }
        return "4.27";
    }

    /// <summary>
    /// Best-effort detection from a loaded package's <c>FPackageFileSummary.FileVersionUE</c>.
    /// Falls back to <paramref name="fallback"/> when the version is ambiguous (custom/stripped headers).
    /// </summary>
    public static EGame DetectFromPackage(IPackage package, EGame fallback)
    {
        var summary = package.Summary;
        var ue5 = summary.FileVersionUE.FileVersionUE5;
        var ue4 = summary.FileVersionUE.FileVersionUE4;

        // UE5 packages carry a non-zero UE5 object version.
        if (ue5 > 0)
        {
            EGame guess;
            if (ue5 >= (int)EUnrealEngineObjectUE5Version.DATA_RESOURCES) guess = EGame.GAME_UE5_3;
            else if (ue5 >= (int)EUnrealEngineObjectUE5Version.LARGE_WORLD_COORDINATES) guess = EGame.GAME_UE5_0;
            else guess = EGame.GAME_UE5_0;
            Log.Information("Detected UE5 package (UE5 obj version {Ue5}) -> {Game}", ue5, guess);
            return guess;
        }

        if (ue4 <= 0)
        {
            Log.Warning("Package has no readable file version (custom/encrypted header). Falling back to {Fallback}", fallback);
            return fallback;
        }

        // Object-version -> release mapping is approximate (versions aren't 1:1 with releases);
        // thresholds are kept in strict ascending value order so the choice stays well-defined.
        EGame detected;
        if (ue4 >= (int)EUnrealEngineObjectUE4Version.CORRECT_LICENSEE_FLAG) detected = EGame.GAME_UE4_27;
        else if (ue4 >= (int)EUnrealEngineObjectUE4Version.FIX_WIDE_STRING_CRC) detected = EGame.GAME_UE4_26;
        else if (ue4 >= (int)EUnrealEngineObjectUE4Version.ADDED_SOFT_OBJECT_PATH) detected = EGame.GAME_UE4_24;
        else detected = fallback;
        Log.Information("Detected package (UE4 obj version {Ue4}) -> {Game} ({Assoc})",
            ue4, detected, ToEngineAssociation(detected));
        return detected;
    }
}
