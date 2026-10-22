namespace RandomEverywhereInstaller.Infrastructure.Updates;

/// <summary>Removes only managed update files, without traversing linked directories.</summary>
public static class UpdateCacheCleanup
{
    public static void Clean(string root, string executableAssetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (Path.GetFileName(executableAssetName) != executableAssetName ||
            !executableAssetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A plain executable filename is required.", nameof(executableAssetName));
        var directory = new DirectoryInfo(Path.GetFullPath(root));
        if (directory.Parent is null) throw new ArgumentException("A volume root is not an update cache.", nameof(root));
        CleanDirectory(directory, executableAssetName, isRoot: true);
    }

    private static void CleanDirectory(DirectoryInfo directory, string asset, bool isRoot)
    {
        try
        {
            if (!IsUnlinked(directory)) return;
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (!IsUnlinked(directory)) return;
                entry.Refresh();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo child) CleanDirectory(child, asset, isRoot: false);
                else if (entry.Name.Equals(asset, StringComparison.OrdinalIgnoreCase) ||
                         entry.Name.Equals(asset + ".partial", StringComparison.OrdinalIgnoreCase))
                {
                    try { entry.Delete(); }
                    catch (IOException) { /* An active helper remains locked until exit. */ }
                    catch (UnauthorizedAccessException) { /* Preserve entries we cannot safely remove. */ }
                }
            }
            // Non-recursive removal preserves folders containing logs or unrecognized user files.
            if (!isRoot && IsUnlinked(directory)) directory.Delete(recursive: false);
        }
        catch (IOException) { /* A concurrent update or non-empty directory is retained. */ }
        catch (UnauthorizedAccessException) { /* Cleanup must not block application startup. */ }
    }

    private static bool IsUnlinked(DirectoryInfo directory)
    {
        for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
        {
            current.Refresh();
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }
}
