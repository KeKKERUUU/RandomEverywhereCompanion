using System.IO;
using Microsoft.Win32;
using RandomEverywhereInstaller.Core.Services;

namespace RandomEverywhereInstaller.App.Services;

public sealed class WindowsFolderPickerService : IFolderPickerService
{
    public string? PickFolder(string? initialDirectory, string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
