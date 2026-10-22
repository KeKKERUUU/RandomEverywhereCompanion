using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RandomEverywhereInstaller.App;

namespace RandomEverywhereInstaller.App.Tests;

internal static partial class Program
{
    private static void WorldDataEntry()
    {
        var dialogs = new RejectingDialogs();
        using var f = new Fixture(savedCommunity: true, savedWorkFolder: true, dialogs: dialogs);
        string? selected = null;
        f.Vm.OptionsRequested += page => selected = page;
        RunCommand(f.Vm.OpenWorldDataCommand);
        Check(selected == "WorldData" && f.Vm.WorldFields.Count == 10, "Tools did not reach the waypoint editor.");
        f.Vm.WorldFields.Single(x => x.Key == "name").Value = "Fixture hangar";
        f.Vm.WorldFields.Single(x => x.Key == "position").Value = "46.9, 7.5";
        RunCommand(f.Vm.SaveWorldCommand);
        Check(f.Vm.WorldEntries.Count == 1, "Saving the form did not create a local entry: " + f.Vm.Detail);
        Check(Directory.GetFiles(f.WorkFolder).Length == 0, "Saving a form modified the mission work folder.");
        RunCommand(f.Vm.WriteWorldCommand);
        Check(dialogs.Confirmations == 1 && Directory.GetFiles(f.WorkFolder).Length == 0, "Rejected table write still changed the work folder.");
        RunCommand(f.Vm.SyncWorldCommand);
        Check(dialogs.Confirmations == 2 && Directory.GetFiles(f.WorkFolder).Length == 0, "Rejected download performed work.");
        f.Vm.WorldSelected = f.Vm.WorldEntries[0]; RunCommand(f.Vm.EditWorldCommand);
        f.Vm.WorldFields.Single(x => x.Key == "name").Value = "Edited fixture";
        f.Localization.SetCulture("de-DE");
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (f.Vm.IsBusy && wait.Elapsed < TimeSpan.FromSeconds(5)) { Pump(); Thread.Sleep(1); }
        Check(f.Vm.WorldFields.Single(x => x.Key == "name").Value == "Edited fixture", "Localization discarded pending world edits.");
        RunCommand(f.Vm.SaveWorldCommand);
        Check(f.Vm.WorldEntries.Count == 1 && f.Vm.WorldEntries[0].Label.Contains("Edited fixture"), "Edit duplicated or lost the entry.");
    }

    private static IEnumerable<DependencyObject> WorldVisuals(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in WorldVisuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void WorldRevisionConfiguration()
    {
        using var f = new Fixture(savedCommunity: true, savedWorkFolder: true);
        var service = new RandomEverywhereInstaller.Infrastructure.WorldData.WorldDataService(Path.Combine(f.Vm.StoragePath, "WorldData"));
        service.SyncAsync(f.WorkFolder, false, CancellationToken.None).GetAwaiter().GetResult();
        var method = f.Vm.GetType().GetMethod("Configuration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (var mode in new[] { "recorded", "synthetic" })
        {
            using var config = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(method.Invoke(f.Vm, [mode])));
            Check(config.RootElement.GetProperty("WorldDataRevision").GetInt32() == 1, mode + " lost the shared waypoint revision");
            Check(config.RootElement.GetProperty("Mode").GetString() == mode, "World data changed the selected audio mode.");
        }
        File.WriteAllText(Path.Combine(f.Vm.StoragePath, "WorldData", "counter.json"), "corrupt");
        using var fallback = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(method.Invoke(f.Vm, ["recorded"])));
        Check(fallback.RootElement.GetProperty("WorldDataRevision").GetInt32() == 0 && !string.IsNullOrEmpty(f.Vm.WorldReport), "Damaged optional world state blocked audio or hid its warning.");
    }

    private static void WorldDataLayout()
    {
        using var f = new Fixture(savedCommunity: true, savedWorkFolder: true);
        RunCommand(f.Vm.OpenWorldDataCommand);
        var view = new WorldDataView { DataContext = f.Vm };
        foreach (var culture in new[] { "en-US", "de-DE", "fr-FR", "es-ES", "it-IT" })
        foreach (var kind in f.Vm.WorldKinds.Select(k => k.Id).ToArray())
        {
            f.Localization.SetCulture(culture); f.Vm.WorldKindId = kind;
            view.Measure(new Size(1000, 470)); view.Arrange(new Rect(0, 0, 1000, 470)); view.UpdateLayout(); Pump();
            var controls = WorldVisuals(view).OfType<Control>().Where(c => c.ActualHeight > 0 && c.Visibility == Visibility.Visible && c is TextBox or ComboBox or Button or CheckBox).ToArray();
            foreach (var c in controls)
            {
                var p = c.TranslatePoint(new Point(), view);
                Check(p.X >= -1 && p.Y >= -1 && p.X + c.ActualWidth <= 1001 && p.Y + c.ActualHeight <= 471,
                    culture + " " + kind + " clips " + c.GetType().Name + " at " + p + " / " + c.RenderSize);
            }
            var fields = controls.OfType<TextBox>().ToArray();
            Check(fields.All(c => c.ActualHeight >= 29), culture + " input text is vertically clipped");
            var save = controls.OfType<Button>().Single(b => b.Command == f.Vm.SaveWorldCommand);
            var saveTop = save.TranslatePoint(new Point(), view).Y;
            Check(fields.All(c => c.TranslatePoint(new Point(), view).Y + c.ActualHeight <= saveTop), culture + " fields overlap Save");
            if (culture == "de-DE" && kind == "hangar_wps" && Environment.GetEnvironmentVariable("RE_WORLD_SCREENSHOT") is { Length: > 0 } screenshot)
            {
                var bitmap = new RenderTargetBitmap(1000, 470, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
                using var output = File.Create(screenshot); encoder.Save(output);
            }
        }
    }
}
