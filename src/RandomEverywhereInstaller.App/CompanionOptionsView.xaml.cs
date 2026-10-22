using System.IO;
using System.Windows;
using System.Windows.Controls;
using RandomEverywhereInstaller.App.ViewModels;

namespace RandomEverywhereInstaller.App;

// Contextual details stay in the shell; no second window or unrelated tab strip.
public partial class CompanionOptionsView : UserControl
{
    private CompanionViewModel ViewModel => (CompanionViewModel)DataContext;
    private string _page = "General";
    public CompanionOptionsView() => InitializeComponent();

    public void SelectPage(string page)
    {
        _page = page;
        foreach (var panel in OptionsPages.Children.OfType<FrameworkElement>()) panel.Visibility = Visibility.Collapsed;
        FrameworkElement selected = page switch
        {
            "Audio" or "Voices" => ViewModel.IsRecordedMode ? RecordedOptions : SyntheticOptions,
            "Tools" => MaintenanceOptions,
            "Diagnostics" => DiagnosticsOptions,
            "MissionUpdate" => MissionUpdateOptions,
            "WorldData" => WorldDataOptions,
            _ => GeneralOptions
        };
        selected.Visibility = Visibility.Visible;
        DetailsTitle.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(
            page is "Audio" or "Voices" ? "L[C.Flow.AudioOptions]" :
            page == "Tools" ? "L[C.Nav.Tools]" :
            page == "Diagnostics" ? "L[C.Recorded.Diagnostics]" :
            page == "WorldData" ? "L[C.World.Title]" :
            page == "MissionUpdate" ? "L[C.MissionUpdate.Title]" : "L[C.Nav.Settings]"));
        SaveAudioButton.Visibility = page is "Audio" or "Voices" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void CloseOptions(object sender, RoutedEventArgs args)
    {
        if (_page == "Diagnostics") { SelectPage("General"); return; }
        if (_page == "WorldData") { if (!ViewModel.IsBusy) SelectPage("Tools"); return; }
        ViewModel.ReturnFromOptions();
    }
    private void ShowDiagnostics(object sender, RoutedEventArgs args) => SelectPage("Diagnostics");
    private void RecordedDragOver(object sender, DragEventArgs args)
    {
        var paths = args.Data.GetData(DataFormats.FileDrop) as string[];
        args.Effects = ViewModel.CanEditAudio && ViewModel.IsRecordedMode && paths is { Length: > 0 and <= 128 } &&
            paths.All(p => Path.GetExtension(p).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            ? DragDropEffects.Link : DragDropEffects.None;
        args.Handled = true;
    }
    private async void RecordedDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (args.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length is > 0 and <= 128 &&
            paths.All(p => Path.GetExtension(p).Equals(".xml", StringComparison.OrdinalIgnoreCase)))
            await ViewModel.RegisterRecordedPathsAsync(paths);
    }
}
