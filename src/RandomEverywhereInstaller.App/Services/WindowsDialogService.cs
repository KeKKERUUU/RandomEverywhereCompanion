using System.Windows;
using RandomEverywhereInstaller.Core.Localization;
using RandomEverywhereInstaller.Core.Services;

namespace RandomEverywhereInstaller.App.Services;

public sealed class WindowsDialogService(ILocalizationService? localization = null) : IUserDialogService
{
    public bool Confirm(string title, string message) => ShowPrompt(title, message, true);

    public void ShowError(string title, string message) => ShowPrompt(title, message, false);

    public void ShowInformation(string title, string message) => ShowPrompt(title, message, false);

    private bool ShowPrompt(string title, string message, bool confirmation)
    {
        if (localization is null || Application.Current is null)
            return MessageBox.Show(message, title, confirmation ? MessageBoxButton.YesNo : MessageBoxButton.OK,
                MessageBoxImage.Information, confirmation ? MessageBoxResult.No : MessageBoxResult.OK) == MessageBoxResult.Yes;
        var prompt = new OperationPrompt(title, message, localization.GetText("Action.Continue"),
            localization.GetText(confirmation ? "Chrome.Cancel" : "Action.Close"), confirmation);
        var workArea = SystemParameters.WorkArea;
        var window = new Window
        {
            Title = title, Content = prompt, Width = Math.Min(680, workArea.Width), Height = Math.Min(460, workArea.Height),
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResizeWithGrip,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate => candidate.IsActive)
                ?? Application.Current.MainWindow
        };
        prompt.Decision += accepted => window.DialogResult = accepted;
        window.ContentRendered += (_, _) => prompt.FocusSafeAction();
        return window.ShowDialog() == true;
    }
}
