using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RandomEverywhereInstaller.App.ViewModels;
using RandomEverywhereInstaller.App.Services;

namespace RandomEverywhereInstaller.App;

public partial class InstallerView : UserControl
{
    public InstallerView() => InitializeComponent();

    private void FilesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DataContext is MainWindowViewModel { IsBusy: false } && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FilesDrop(object sender, DragEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            vm.AcceptZipDrop(paths);
        e.Handled = true;
    }

    private void BrowseFiles(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { IsBusy: false } vm) return;
        var picker = new OpenFileDialog { Title = vm.ZipFolderLabel, Multiselect = true, CheckFileExists = true,
            InitialDirectory = FilePickerLocations.Shared.InitialDirectory("installation", vm.ZipArchives.FirstOrDefault()?.FullPath) };
        if (picker.ShowDialog(Window.GetWindow(this)) == true)
        {
            FilePickerLocations.Shared.RememberFile("installation", picker.FileName);
            vm.AcceptZipDrop(picker.FileNames);
        }
    }
}
