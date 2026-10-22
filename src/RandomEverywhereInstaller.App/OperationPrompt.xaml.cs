using System.Windows;
using System.Windows.Controls;

namespace RandomEverywhereInstaller.App;

public partial class OperationPrompt : UserControl
{
    public event Action<bool>? Decision;
    public OperationPrompt(string title, string message, string accept, string cancel, bool confirmation)
    {
        InitializeComponent();
        Heading.Text = title; Message.Text = message;
        AcceptAction.Content = accept; CancelAction.Content = cancel;
        AcceptAction.Visibility = confirmation ? Visibility.Visible : Visibility.Collapsed;
    }
    public void FocusSafeAction() => CancelAction.Focus();
    private void CancelClick(object sender, RoutedEventArgs args) => Decision?.Invoke(false);
    private void AcceptClick(object sender, RoutedEventArgs args) => Decision?.Invoke(true);
}
