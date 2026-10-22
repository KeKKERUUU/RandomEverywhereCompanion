using System.IO;

namespace RandomEverywhereInstaller.App.ViewModels;

public sealed class ZipArchivePathViewModel
{
    public ZipArchivePathViewModel(string fullPath)
    {
        FullPath = Path.GetFullPath(fullPath);
        FileName = Path.GetFileName(FullPath);
        Directory = Path.GetDirectoryName(FullPath) ?? string.Empty;
    }

    public string FullPath { get; }
    public string FileName { get; }
    public string Directory { get; }
}
