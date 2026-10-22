namespace RandomEverywhereInstaller.Core.Services;

public interface IFolderPickerService
{
    string? PickFolder(string? initialDirectory, string title);
}
