namespace RandomEverywhereInstaller.Infrastructure.Updates;

/// <summary>One owned download beside Community, never within a package or on C: by default.</summary>
public sealed class MissionDownloadCache : IDisposable
{
    public string FilePath { get; }
    private readonly string _directory;
    private MissionDownloadCache(string directory) { _directory = directory; FilePath = Path.Combine(directory, "everywhere_all.json"); }

    public static MissionDownloadCache Create(string community, long bytes)
    {
        if (bytes <= 0 || bytes > MissionUpdateService.MaximumMissionBytes) throw new IOException("Invalid mission download size.");
        var selected = new DirectoryInfo(Path.GetFullPath(community));
        if (!selected.Exists || selected.Parent is null) throw new IOException("Select an existing Community directory.");
        MissionUpdateService.RequireUnlinkedParent(Path.Combine(selected.FullName, ".validation"));
        if (new DriveInfo(Path.GetPathRoot(selected.FullName)!).AvailableFreeSpace < bytes + 16L * 1024 * 1024)
            throw new IOException("Insufficient free space before the mission download.");
        var root = Path.Combine(selected.Parent.FullName, ".RandomEverywhereCompanion", "MissionDownloads");
        // Validate every existing ancestor before creating any directory.
        for (var p = new DirectoryInfo(root); p is not null; p = p.Parent)
            if (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The mission cache cannot traverse links.");
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new MissionDownloadCache(directory);
    }

    public void Dispose()
    {
        // Never recurse: unexpected files remain untouched, including during recovery.
        try
        {
            MissionUpdateService.RequireUnlinkedParent(FilePath);
            File.Delete(FilePath);
            Directory.Delete(_directory, recursive: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Core.Updates.ApplicationUpdateException)
        { System.Diagnostics.Trace.TraceWarning("Mission cache retained for recovery: {0}", error.Message); }
    }
}
