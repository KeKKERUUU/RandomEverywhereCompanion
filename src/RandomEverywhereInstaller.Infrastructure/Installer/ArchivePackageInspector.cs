using RandomEverywhereInstaller.Core.Installer;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace RandomEverywhereInstaller.Infrastructure.Installer;

/// <summary>
/// Normalizes supported archive formats and detects safe MSFS package roots.
/// </summary>
internal static class ArchivePackageInspector
{
    private const int MaxEntries = 100_000;
    private const long MaxEntryBytes = 8L * 1024 * 1024 * 1024;
    private const long MaxTotalBytes = 50L * 1024 * 1024 * 1024;
    // High-compression textures (DDS/PNG/JPEG) are normal in MSFS packages.
    // Ratio alone is therefore only blocking for an extreme, very large entry;
    // total expanded size and entry-count limits remain the primary bomb guards.
    private const double ExtremeCompressionRatio = 10_000;
    private const long ExtremeRatioMinimumBytes = 512L * 1024 * 1024;
    private static readonly string[] SupportedExtensions =
    [
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz", ".tbz2",
        ".xz", ".txz", ".lz", ".lzip", ".zst", ".zstd", ".tzst", ".arj", ".arc"
    ];
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
    private static readonly HashSet<string> MsfsContentFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "SimObjects", "hpgmission", "html_ui", "ContentInfo", "effects", "scenery", "sound", "soundai",
        "materialLibs", "ModelBehaviorDefs", "missions", "MarketplaceData", "Services", "Weather"
    };

    public static bool IsSupportedFile(string path) =>
        SupportedExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    public static IArchive Open(string archivePath)
    {
        try
        {
            return ArchiveFactory.OpenArchive(archivePath, new ReaderOptions { LeaveStreamOpen = false });
        }
        catch (Exception exception) when (exception is InvalidFormatException or InvalidDataException or IOException or NotSupportedException)
        {
            throw new InstallerException("ARCHIVE-001", $"Unsupported, damaged, or unreadable archive: {Path.GetFileName(archivePath)}", exception);
        }
    }

    // Inspection reads metadata only; extraction occurs after the complete plan is valid.
    public static IReadOnlyList<DetectedPackageRoot> Inspect(string archivePath)
    {
        using var archive = Open(archivePath);
        var entries = archive.Entries.ToArray();
        if (entries.Length == 0)
            throw new InstallerException("ARCHIVE-002", $"Archive is empty: {Path.GetFileName(archivePath)}");
        if (entries.Length > MaxEntries)
            throw new InstallerException("ARCHIVE-012", $"Archive contains too many entries: {entries.Length}");

        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var entry in entries)
        {
            var normalized = ValidateEntry(entry);
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            if (entry.IsDirectory)
            {
                // Repeated directory records are produced by several legitimate RAR/ZIP tools.
                // They are harmless because the installer creates directories idempotently.
                if (fileNames.Contains(normalized))
                    throw new InstallerException("ARCHIVE-013", $"Archive path is both a file and a directory: {normalized}");
                directoryNames.Add(normalized);
            }
            else
            {
                if (directoryNames.Contains(normalized) || !fileNames.Add(normalized))
                    throw new InstallerException("ARCHIVE-013", $"Archive contains a duplicate or case-colliding file: {normalized}");
            }
            allPaths.Add(normalized);
            totalBytes = checked(totalBytes + entry.Size);
            if (totalBytes > MaxTotalBytes)
                throw new InstallerException("ARCHIVE-012", "Archive uncompressed size exceeds the safety limit.");
        }

        var roots = new List<DetectedPackageRoot>();
        foreach (var manifest in allPaths.Where(path => path.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase) || path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
        {
            var separator = manifest.LastIndexOf('/');
            var root = separator < 0 ? string.Empty : manifest[..separator];
            var layout = string.IsNullOrEmpty(root) ? "layout.json" : $"{root}/layout.json";
            if (!allPaths.Contains(layout)) continue;
            var folderName = string.IsNullOrEmpty(root)
                ? SanitizeFolderName(GetArchiveStem(archivePath))
                : root.Split('/')[^1];
            roots.Add(new DetectedPackageRoot(root, folderName));
        }

        if (roots.Count == 0) roots.AddRange(DetectOverlayRoots(archivePath, allPaths));
        if (roots.Count == 0)
            throw new InstallerException("ARCHIVE-007", $"No unambiguous MSFS package or overlay root was found in {Path.GetFileName(archivePath)}.");

        return roots.DistinctBy(root => root.RelativePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(root => root.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Every entry is normalized before it can influence a staging destination.
    public static string ValidateEntry(IArchiveEntry entry)
    {
        var original = entry.Key ?? string.Empty;
        var name = original.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(name)) return name;
        if (entry.IsEncrypted)
            throw new InstallerException("ARCHIVE-003", $"Encrypted or password-protected archive entry: {original}");
        if (Path.IsPathRooted(original) || original.StartsWith("//", StringComparison.Ordinal) || original.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InstallerException("ARCHIVE-005", $"Absolute archive path detected: {original}");

        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new InstallerException("ARCHIVE-004", $"Path traversal detected: {original}");
        foreach (var segment in segments)
        {
            // The destination is Windows even when archive inspection runs on another OS.
            // Win32 trims trailing dots/spaces, which would turn distinct entries into aliases.
            if (segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(character => character < 32 || character is '<' or '>' or '"' or '|' or '?' or '*'))
                throw new InstallerException("ARCHIVE-005", $"Invalid Windows path segment detected: {original}");
            if (segment.Contains(':', StringComparison.Ordinal))
                throw new InstallerException("ARCHIVE-005", $"Alternate data stream or drive path detected: {original}");
            if (ReservedNames.Contains(segment.Split('.')[0]))
                throw new InstallerException("ARCHIVE-005", $"Reserved Windows path detected: {original}");
        }

        if (!string.IsNullOrWhiteSpace(entry.LinkTarget))
            throw new InstallerException("ARCHIVE-006", $"Symbolic or hard link detected: {original}");
        if (entry.Size > MaxEntryBytes)
            throw new InstallerException("ARCHIVE-012", $"Archive entry exceeds the size limit: {original}");
        if (entry.CompressedSize > 0 && entry.Size >= ExtremeRatioMinimumBytes && (double)entry.Size / entry.CompressedSize > ExtremeCompressionRatio)
            throw new InstallerException("ARCHIVE-011", $"Suspicious compression ratio detected: {original}");
        // Match the path the filesystem will resolve before duplicate/root checks.
        return string.Join('/', segments);
    }

    private static DetectedPackageRoot[] DetectOverlayRoots(string archivePath, IReadOnlySet<string> allPaths)
    {
        var files = allPaths.Where(path => !string.IsNullOrWhiteSpace(path) && Path.HasExtension(path))
            .Where(path => !IsDocumentationFile(path)).ToArray();
        if (files.Length == 0) return [];

        // Community may itself be behind one or more download/wrapper folders.
        var communityRoots = files.Select(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Select(parts => (Parts: parts, Index: Array.FindIndex(parts, part => part.Equals("Community", StringComparison.OrdinalIgnoreCase))))
            .Where(value => value.Index >= 0 && value.Parts.Length > value.Index + 2)
            .Select(value => new DetectedPackageRoot(
                string.Join('/', value.Parts[..(value.Index + 2)]),
                value.Parts[value.Index + 1]))
            .DistinctBy(root => root.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (communityRoots.Length > 0) return communityRoots;

        // Overlay archives frequently omit manifest/layout. The directory immediately
        // above an MSFS content folder is the package root, regardless of wrapper depth.
        var structuralRoots = files
            .Select(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Select(parts => (Parts: parts, Index: Array.FindIndex(parts, part => MsfsContentFolders.Contains(part))))
            .Where(value => value.Index > 0)
            .Select(value => new DetectedPackageRoot(
                string.Join('/', value.Parts[..value.Index]),
                value.Parts[value.Index - 1]))
            .DistinctBy(root => root.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (structuralRoots.Length > 0) return structuralRoots;

        var directories = files.Select(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries)[..^1])
            .Where(parts => parts.Length > 0).ToArray();
        if (directories.Length == 0) return [];
        var commonLength = directories.Min(parts => parts.Length);
        var common = 0;
        while (common < commonLength && directories.All(parts => parts[common].Equals(directories[0][common], StringComparison.OrdinalIgnoreCase))) common++;
        if (common > 0)
        {
            var relative = string.Join('/', directories[0][..common]);
            return [new DetectedPackageRoot(relative, directories[0][common - 1])];
        }

        var topFolders = directories.Select(parts => parts[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return topFolders.Length == 1 ? [new DetectedPackageRoot(topFolders[0], topFolders[0])] : [];
    }

    private static bool IsDocumentationFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("readme", StringComparison.OrdinalIgnoreCase) || name.StartsWith("license", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("changelog", StringComparison.OrdinalIgnoreCase) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetArchiveStem(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var extension in SupportedExtensions.OrderByDescending(value => value.Length))
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return name[..^extension.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    private static string SanitizeFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "msfs-package" : cleaned;
    }
}
