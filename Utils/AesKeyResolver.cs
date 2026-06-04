using CUE4Parse.Encryption.Aes;
using Serilog;

namespace UE4Decompiler.Utils;

/// <summary>
/// Resolves the AES-256 key used to encrypt PAK/IoStore index + data blocks.
/// Primary path: a hex string passed on the CLI. Secondary (best-effort): scan the
/// game executable for high-entropy 32-byte regions that look like an embedded key.
/// </summary>
public static class AesKeyResolver
{
    /// <summary>
    /// Normalise + validate a user supplied key. Accepts "0x..."/bare hex, 64 hex chars (32 bytes).
    /// Returns null for null/empty input (i.e. unencrypted game).
    /// </summary>
    public static FAesKey? FromHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;

        var clean = hex.Trim();
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
        clean = clean.Replace("-", "").Replace(" ", "");

        if (clean.Length != 64 || !IsHex(clean))
            throw new ArgumentException($"AES key must be 64 hex characters (32 bytes); got {clean.Length} chars.");

        return new FAesKey("0x" + clean);
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    /// <summary>
    /// Heuristic scan of a shipped executable for candidate AES keys. UE games sometimes embed
    /// the key as a contiguous 32-byte blob; we surface high Shannon-entropy windows as candidates.
    /// This is a hook for manual triage — it is NOT guaranteed to find the key and may return noise.
    /// Each candidate should be validated by attempting to mount the PAK with it.
    /// </summary>
    public static IEnumerable<FAesKey> ScanExecutableForKeys(string exePath, double minEntropy = 3.6)
    {
        if (!File.Exists(exePath))
        {
            Log.Warning("AES scan: executable not found at {Path}", exePath);
            yield break;
        }

        var data = File.ReadAllBytes(exePath);
        const int window = 32;
        var seen = new HashSet<string>();
        Log.Information("AES scan: sweeping {Bytes:N0} bytes of {File} for 32-byte high-entropy windows", data.Length, Path.GetFileName(exePath));

        for (var i = 0; i + window <= data.Length; i += window) // stride by 32: keys are usually 32-aligned blobs
        {
            var span = data.AsSpan(i, window);
            if (ShannonEntropy(span) < minEntropy) continue;
            if (IsLowVariety(span)) continue;

            var hex = Convert.ToHexString(span);
            if (seen.Add(hex))
                yield return new FAesKey("0x" + hex);
        }
    }

    private static double ShannonEntropy(ReadOnlySpan<byte> data)
    {
        Span<int> counts = stackalloc int[256];
        foreach (var b in data) counts[b]++;
        double entropy = 0;
        foreach (var c in counts)
        {
            if (c == 0) continue;
            var p = (double)c / data.Length;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    // Reject runs that are mostly one byte (padding, zero fill) which can score deceptively.
    private static bool IsLowVariety(ReadOnlySpan<byte> data)
    {
        Span<int> counts = stackalloc int[256];
        foreach (var b in data) counts[b]++;
        var distinct = 0;
        foreach (var c in counts) if (c > 0) distinct++;
        return distinct < 12;
    }
}
