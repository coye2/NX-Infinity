using System.Text.RegularExpressions;

namespace NxInfinity.Runtime;

internal static partial class SafePaths
{
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$")]
    private static partial Regex IdentifierPattern();

    internal static void ValidateIdentifier(string value, string name)
    {
        if (!IdentifierPattern().IsMatch(value) || value is "." or ".." || value.EndsWith('.'))
            throw new NxException("invalid_argument", $"{name} must be a safe 1..64 character identifier.");
    }

    internal static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new NxException("invalid_argument", "An absolute local filesystem path is required.");
        // Network shares/remote content are deliberately outside protocol v1.
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new NxException("invalid_argument", "Network share roots are not supported in this release.");
        return Path.GetFullPath(path);
    }

    internal static void RejectLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(full)!;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            // The filesystem root is an OS-managed boundary, not a child of
            // the caller's installation or mutable workspace. Wine/Proton can
            // report its virtual C:\\ drive as a reparse point even when the
            // files beneath it are normal directories. Rejecting that root
            // made verification of every installed core fail with invalid_argument.
            //
            // Only exempt the EXACT volume root. Any symlink/junction below it,
            // including a linked install folder, profile, manifest or artifact,
            // remains forbidden. No environment-variable security bypass.
            if (string.Equals(current, volumeRoot, comparison))
                break;

            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new NxException("invalid_argument",
                    "A symbolic link or junction inside the selected installation or NX data path is not supported.");
        }
    }
}

