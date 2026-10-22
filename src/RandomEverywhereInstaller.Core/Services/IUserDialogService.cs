namespace RandomEverywhereInstaller.Core.Services;

public interface IUserDialogService
{
    bool Confirm(string title, string message);
    void ShowError(string title, string message);
    void ShowInformation(string title, string message);
}
