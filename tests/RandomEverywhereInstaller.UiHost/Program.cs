using System.IO;
using System.Windows;
using System.Windows.Controls;
using RandomEverywhereInstaller.App;
using RandomEverywhereInstaller.App.Services;
using RandomEverywhereInstaller.App.ViewModels;
using RandomEverywhereInstaller.Core.Models;
using RandomEverywhereInstaller.Core.Services;
using RandomEverywhereInstaller.Core.Updates;
using RandomEverywhereInstaller.Core.Wizard;
using RandomEverywhereInstaller.Infrastructure.Localization;

namespace RandomEverywhereInstaller.UiHost;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Generic Application only: never instantiate the production App bootstrapper.
        var application = new Application();
        foreach (var theme in new[] { "Colors", "Theme" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/RandomAndEverywhereCompanion;component/Themes/{theme}.xaml", UriKind.Relative) });
        var localization = new JsonLocalizationService(culture => typeof(InstallerView).Assembly.GetManifestResourceStream(
            $"RandomEverywhereInstaller.App.Localization.{culture}.json")!);
        var community = Path.Combine(AppContext.BaseDirectory, ".fixture-community");
        Directory.CreateDirectory(community);
        var engine = new FixtureEngine(community);
        var view = new InstallerView();
        var root = new DockPanel { Margin = new Thickness(12) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var scenario = new ComboBox { ItemsSource = new[] { "Full", "Cumulative", "Stable" }, SelectedIndex = 0, Width = 140 };
        var outcome = new ComboBox { ItemsSource = new[] { "Success", "Error", "Cancelled" }, SelectedIndex = 0, Width = 140, Margin = new Thickness(8, 0, 0, 0) };
        var language = new ComboBox { ItemsSource = localization.SupportedCultures, SelectedItem = "en-US", Width = 120, Margin = new Thickness(8, 0, 0, 0) };
        var reset = new Button { Content = "Reset fixture", Style = (Style)application.FindResource("SecondaryButton"), Margin = new Thickness(8, 0, 0, 0) };
        var notice = new TextBlock { Text = "TEST HOST — in-memory operations only", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var control in new UIElement[] { scenario, outcome, language, reset, notice }) toolbar.Children.Add(control);
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar); root.Children.Add(view);
        var window = new Window { Title = "Companion UI Fixture Host — NO REAL OPERATIONS", Width = 1180, Height = 760, MinWidth = 1060, MinHeight = 700,
            Content = root, Background = (System.Windows.Media.Brush)application.FindResource("Brush.Window") };
        MainWindowViewModel? model = null;
        async Task ResetAsync()
        {
            if (model?.IsBusy == true) return;
            model?.Dispose();
            engine.Outcome = (string)outcome.SelectedItem;
            model = new MainWindowViewModel(localization, new WizardState(), new FixtureFolderPicker(), new WindowsDialogService(localization), engine, new NoUpdates(), ownsEngine: false);
            await model.PrepareCompanionWorkflowAsync(community, SimulatorType.Msfs2024,
                scenario.SelectedIndex == 0 ? InstallerMode.FullInstallation : InstallerMode.AndrewUpdateOnly);
            model.AcceptZipDrop([Path.Combine(AppContext.BaseDirectory, scenario.SelectedIndex == 1 ? "everywhere_all.json" : "ui-fixture.zip")]);
            model.ExitRequested += (_, _) => window.Close();
            view.DataContext = model;
        }
        reset.Click += async (_, _) => await ResetAsync();
        language.SelectionChanged += (_, _) => localization.SetCulture((string)language.SelectedItem);
        window.Loaded += async (_, _) => await ResetAsync();
        window.Closed += (_, _) => model?.Dispose();
        application.Run(window);
    }
    private sealed class FixtureFolderPicker : IFolderPickerService
    {
        public string? PickFolder(string? initialDirectory, string title) => null;
    }
    private sealed class NoUpdates : IApplicationUpdateService
    {
        public Task<ApplicationUpdateInfo?> CheckForUpdateAsync(Version currentVersion, string assetName, CancellationToken cancellationToken) => Task.FromResult<ApplicationUpdateInfo?>(null);
        public Task<string> DownloadAsync(ApplicationUpdateInfo update, string destinationDirectory, IProgress<ApplicationUpdateProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException("UI fixture has no network");
    }
}
