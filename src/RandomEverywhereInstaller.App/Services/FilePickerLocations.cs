using System.IO;

namespace RandomEverywhereInstaller.App.Services;

// Session-only history; canceled pickers never change paths or create directories.
public sealed class FilePickerLocations
{
    public static FilePickerLocations Shared { get; } = new();
    private readonly Dictionary<string, string> _directories = new(StringComparer.Ordinal);
    public string InitialDirectory(string purpose, string? preferred = null)
    {
        if (_directories.TryGetValue(purpose, out var remembered) && Directory.Exists(remembered)) return remembered;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            if (Directory.Exists(preferred)) return preferred;
            if (File.Exists(preferred)) return Path.GetDirectoryName(Path.GetFullPath(preferred))!;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
    public void RememberFile(string purpose, string selectedFile)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(selectedFile));
        if (directory is not null && Directory.Exists(directory)) _directories[purpose] = directory;
    }
}
