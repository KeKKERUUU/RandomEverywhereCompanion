using System.ComponentModel;
using System.Windows;
using RandomEverywhereInstaller.App.Services;
using RandomEverywhereInstaller.App.ViewModels;

namespace RandomEverywhereInstaller.App;

public partial class CompanionWindow : Window
{
    private readonly CompanionViewModel _viewModel;
    private readonly WindowCloseCoordinator _closeCoordinator;
    private MainWindowViewModel? _installer;
    private TaskCompletionSource? _installerCompletion;

    public async Task ShowInstallerAsync(MainWindowViewModel installer)
    {
        if (_installer is not null) throw new InvalidOperationException("An installer session is already open.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _installerCompletion = completion;
        void Finish(object? sender, EventArgs args) => completion.TrySetResult();
        _installer = installer;
        installer.ExitRequested += Finish;
        InstallerHost.Content = new InstallerView { DataContext = installer };
        CompanionBody.Visibility = Visibility.Collapsed;
        SimulatorConnectionBar.Visibility = Visibility.Collapsed;
        // The embedded workflow owns progress; do not display a stale second status bar.
        ShellStatusBar.Visibility = Visibility.Collapsed;
        ShellStatusRow.Height = new GridLength(0);
        SettingsButton.IsEnabled = false;
        InstallerHost.Visibility = Visibility.Visible;
        try { await completion.Task; }
        finally
        {
            installer.ExitRequested -= Finish;
            InstallerHost.Content = null;
            InstallerHost.Visibility = Visibility.Collapsed;
            CompanionBody.Visibility = Visibility.Visible;
            SimulatorConnectionBar.ClearValue(VisibilityProperty);
            ShellStatusBar.Visibility = Visibility.Visible;
            ShellStatusRow.Height = new GridLength(42);
            SettingsButton.ClearValue(IsEnabledProperty);
            _installer = null;
            _installerCompletion = null;
        }
    }
    public CompanionWindow(CompanionViewModel viewModel)
    {
        _viewModel = viewModel;
        _closeCoordinator = new WindowCloseCoordinator(Dispatcher,
            () => _viewModel.DisposeAsync().AsTask(), Close,
            error => System.Diagnostics.Trace.TraceError("Companion shutdown: {0}", error));
        InitializeComponent(); DataContext = viewModel;
        SourceInitialized += (_, _) => WindowWorkArea.Attach(this);
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1180, area.Width); Height = Math.Min(760, area.Height);
        MinWidth = Math.Min(1060, area.Width); MinHeight = Math.Min(700, area.Height);
        viewModel.OptionsRequested += page => InlineOptions.SelectPage(page);
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        Closing += CloseSafely;
        Closed += (_, _) => { _installer?.Dispose(); _installerCompletion?.TrySetResult(); };
    }
    private void Minimize(object sender, RoutedEventArgs args) => WindowState = WindowState.Minimized;
    private void Maximize(object sender, RoutedEventArgs args) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs args) => Close();
    public void CloseAfterUpdate()
    {
        IsEnabled = false;
        _closeCoordinator.Request();
    }
    private void CloseSafely(object? sender, CancelEventArgs args)
    {
        if (_closeCoordinator.CanClose) return;
        args.Cancel = true;
        if (_installer is { IsBusy: true }) { _installer.RequestCancellation(); return; }
        if (_viewModel.IsBusy) return;
        if (!_viewModel.TryLeaveAudioEdits()) return;
        IsEnabled = false;
        _closeCoordinator.Request();
    }
}
