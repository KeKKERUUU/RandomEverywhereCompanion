using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace RandomEverywhereInstaller.Infrastructure.Companion;

public sealed record DebugCleanupResult(int DeletedFiles, string? BackupPath);

/// <summary>Deletes only recognized debug snapshots after a verified backup.</summary>
public static class CompanionWorkFolderService
{
    public static IReadOnlyList<string> FindWorkFolders()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] roots =
        [
            Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalState", "WASM"),
            Path.Combine(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalState", "WASM"),
            Path.Combine(roaming, "Microsoft Flight Simulator", "WASM"),
            Path.Combine(roaming, "Microsoft Flight Simulator 2024", "WASM")
        ];
        var result = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var simulator in Directory.EnumerateDirectories(root))
                foreach (var package in Directory.EnumerateDirectories(simulator, "hpg-*"))
                {
                    var work = Path.Combine(package, "work");
                    if (Directory.Exists(work) && !HasReparsePoint(work)) result.Add(work);
                }
                foreach (var package in Directory.EnumerateDirectories(root, "hpg-*"))
                {
                    var work = Path.Combine(package, "work");
                    if (Directory.Exists(work) && !HasReparsePoint(work)) result.Add(work);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> DebugFiles(string workPath)
    {
        var work = Validate(workPath);
        return Directory.EnumerateFiles(work).Where(path => IsDebugFile(Path.GetFileName(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0).ToArray();
    }

    public static bool IsDebugFile(string name) =>
        name.StartsWith("table_R&E_debug_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("table_R&E_osm_", StringComparison.OrdinalIgnoreCase) ||
        (name.StartsWith("table_R&E_", StringComparison.OrdinalIgnoreCase) && name.Contains("_test", StringComparison.OrdinalIgnoreCase));

    public static DebugCleanupResult CleanupDebug(string workPath, string backupDirectory)
    {
        EnsureSimulatorClosed();
        var files = DebugFiles(workPath);
        if (files.Count == 0) return new(0, null);
        if (HasReparsePoint(Path.GetFullPath(backupDirectory))) throw new IOException("A backup path traverses a reparse point.");
        Directory.CreateDirectory(backupDirectory);
        var backup = Path.Combine(backupDirectory, "debug-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + ".zip");
        var hashes = new Dictionary<string, byte[]>();
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Create))
        foreach (var file in files)
        {
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            hashes[file] = SHA256.HashData(input);
            input.Position = 0;
            using var output = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Fastest).Open();
            input.CopyTo(output);
        }
        // Every backed-up entry and source is checked before any source is removed.
        using (var zip = ZipFile.OpenRead(backup))
        foreach (var file in files)
        {
            using var stored = zip.GetEntry(Path.GetFileName(file))!.Open();
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stored), hashes[file])) throw new IOException("Backup verification failed; no debug files were removed.");
            using var original = File.OpenRead(file);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(original), hashes[file])) throw new IOException("A debug file changed during backup; no files were removed.");
        }
        foreach (var file in files) File.Delete(file);
        return new(files.Count, backup);
    }

    private static string Validate(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(full) || !Path.GetFileName(full).Equals("work", StringComparison.OrdinalIgnoreCase) || HasReparsePoint(full))
            throw new IOException("Select an existing mission work folder without junctions or symbolic links.");
        return full;
    }

    private static bool HasReparsePoint(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static void EnsureSimulatorClosed()
    {
        foreach (var name in new[] { "FlightSimulator", "FlightSimulator2024" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length > 0) throw new IOException("Close Microsoft Flight Simulator before cleaning the mission work folder."); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
}
