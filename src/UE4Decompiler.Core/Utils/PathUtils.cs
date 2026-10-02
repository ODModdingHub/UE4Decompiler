namespace UE4Decompiler.Core.Utils;

/// <summary>
/// Cross-platform path helpers with strict path traversal security (Phase 28 &amp; 40).
/// </summary>
public static class PathUtils
{
    /// <summary>
    /// Normalize path to standard forward slashes, removing redundant segments.
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var p = path.Trim().Replace('\\', '/');
        while (p.Contains("//")) p = p.Replace("//", "/");
        return p;
    }

    /// <summary>
    /// Ensures that an untrusted relative path cannot escape the target root directory (Path Traversal Protection).
    /// Throws an <see cref="InvalidOperationException"/> if traversal is detected.
    /// </summary>
    public static string SafeCombine(string rootDirectory, string relativePath)
    {
        var fullRoot = Path.GetFullPath(rootDirectory);
        var cleanRel = Normalize(relativePath).TrimStart('/');

        // Reject directory traversal attempts explicitly
        if (cleanRel.Contains("../") || cleanRel.Contains(@"..\") || cleanRel.EndsWith(".."))
            throw new InvalidOperationException($"Security: Path traversal attempt detected in '{relativePath}'.");

        var combined = Path.GetFullPath(Path.Combine(fullRoot, cleanRel.Replace('/', Path.DirectorySeparatorChar)));

        if (!combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Security: Output path '{combined}' escapes root directory '{fullRoot}'.");

        return combined;
    }

    private static readonly HashSet<char> UniversalInvalidChars = new(
        Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }));

    /// <summary>
    /// Sanitize filename by replacing forbidden OS characters with underscores (cross-platform safe).
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        var chars = fileName.Select(c => UniversalInvalidChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrEmpty(sanitized) ? "unnamed" : sanitized;
    }

    /// <summary>
    /// Normalizes directory path, resolves full path, and ensures directory exists on disk.
    /// </summary>
    public static string NormalizeAndValidateDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        Directory.CreateDirectory(full);
        return full;
    }
}
