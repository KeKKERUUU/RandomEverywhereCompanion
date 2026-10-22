using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using RandomEverywhereInstaller.App;
using RandomEverywhereInstaller.App.Services;
using RandomEverywhereInstaller.App.ViewModels;
using RandomEverywhereInstaller.Core.Companion;
using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Core.Models;
using RandomEverywhereInstaller.Core.Services;
using RandomEverywhereInstaller.Core.Wizard;
using RandomEverywhereInstaller.Core.Updates;
using RandomEverywhereInstaller.Infrastructure.Companion;
using RandomEverywhereInstaller.Infrastructure.Installer;
using RandomEverywhereInstaller.Infrastructure.Localization;
using RandomEverywhereInstaller.Infrastructure.Updates;

namespace RandomEverywhereInstaller.App.Tests;

internal static partial class Program
{
    // STA controls and dispatcher only: never Application.Run, Window.Show, InitializeAsync,
    // StartCommand, or any audio/runtime request. All preferences are temporary fixtures.
    private static readonly List<Exception> Unhandled = [];

    private static void MaximizedBounds()
    {
        Check(WindowWorkArea.RelativeBounds(new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040)) == new Rect(0, 0, 1920, 1040), "Bottom taskbar is covered");
        Check(WindowWorkArea.RelativeBounds(new Rect(-1920, 0, 1920, 1080), new Rect(-1880, 0, 1880, 1080)) == new Rect(40, 0, 1880, 1080), "Secondary monitor left taskbar offset was lost");
        Check(WindowWorkArea.RelativeBounds(new Rect(1920, -1440, 2560, 1440), new Rect(1920, -1380, 2560, 1380)) == new Rect(0, 60, 2560, 1380), "Scaled monitor top taskbar offset was lost");
    }

    private static void MissionUpdateEntry()
    {
        var service = new FakeMissionUpdates();
        using var f = new Fixture(savedCommunity: true, missionUpdates: service);
        string? page = null;
        f.Vm.OptionsRequested += value => page = value;
        RunCommand(f.Vm.UpdateMissionCommand);
        Check(page == "MissionUpdate" && service.Checks == 0 && service.Downloads == 0, "Opening updates caused network work or opened the manual picker");
        RunCommand(f.Vm.CheckMissionUpdateCommand);
        Check(service.Checks == 0 && !f.Vm.HasMissionUpdate, "Missing primary was ignored");
        Check(f.Vm.Status == f.Vm.L["C.MissionUpdate.Missing"], "Missing-primary guidance was not shown");
    }

    private static void MissionUpdateConfirmation()
    {
        var service = new FakeMissionUpdates();
        var dialogs = new RejectingDialogs();
        using var f = new Fixture(savedCommunity: true, missionUpdates: service, dialogs: dialogs);
        var package = Path.Combine(f.WorkFolder, "hpg-airbus-h145-z-andrew-mission-pack");
        var mission = Path.Combine(package, "hpgmission", "RANDOM AND EVERYWHERE");
        Directory.CreateDirectory(mission);
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(package, "layout.json"), "{}");
        var path = Path.Combine(mission, "everywhere_all.json");
        var original = "{\"id\":\"HEMSRandE\",\"title\":\"HEMS RANDOM AND EVERYWHERE MISSIONS 0.997 169.9\"}";
        File.WriteAllText(path, original);
        RunCommand(f.Vm.CheckMissionUpdateCommand);
        Check(service.Checks == 1 && f.Vm.HasMissionUpdate && f.Vm.MissionUpdateNotes == "Fixture release notes", "Release or notes were lost");
        RunCommand(f.Vm.DownloadMissionUpdateCommand);
        Check(dialogs.Confirmations == 1 && service.Downloads == 0, "Rejected confirmation still downloaded a mission");
        Check(File.ReadAllText(path) == original, "Checking a release changed the installed mission");
    }

    private sealed class FakeMissionUpdates : IMissionUpdateService
    {
        public int Checks, Downloads;
        public Task<MissionUpdateInfo> CheckAsync(CancellationToken token)
        {
            Checks++;
            return Task.FromResult(new MissionUpdateInfo(new("0.997", "169.45", null), "v0.997-169.45", "Fixture release notes",
                new Uri("https://github.com/davierosoft/HEMS-random-everywhere-mission/releases/download/v0.997-169.45/everywhere_all.json"), 100, null, "Fixture"));
        }
        public Task<string> DownloadAsync(MissionUpdateInfo info, string path, IProgress<ApplicationUpdateProgress>? progress, CancellationToken token)
        { Downloads++; throw new InvalidOperationException("This test must not download."); }
    }

    [STAThread]
    private static int Main()
    {
        // Do not instantiate the production App: Application queues OnStartup on its
        // dispatcher even without Run(), which would enter the real bootstrapper.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var resource in new[] { "Colors", "Theme" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/RandomAndEverywhereCompanion;component/Themes/{resource}.xaml", UriKind.Relative)
            });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Dispatcher.CurrentDispatcher.UnhandledException += (_, args) =>
        {
            Unhandled.Add(args.Exception);
            args.Handled = true;
        };
        (string Name, Action Test)[] tests =
        [
            ("Tools discovers a work folder through the real options event", ToolsDiscovery),
            ("Tools preserves an existing work folder", ToolsPreservesFolder),
            ("Persistent synthetic sliders synchronize both ways", VolumeBindings),
            ("Nonlive synthetic editors disable while running and busy", SyntheticEditGates),
            ("Welcome persistence failure is recoverable and retryable", WelcomeFailure),
            ("Welcome acceptance persists and skips the next notice", WelcomeSuccess),
            ("Synchronous shutdown is deferred and runs once", SynchronousShutdown),
            ("Asynchronous shutdown waits and contains cleanup failure", AsynchronousShutdown),
            ("Community cancellation preserves the selected simulator and path", CommunityCancellation),
            ("Embedded installer keeps file drop visible beside a full inventory", EmbeddedInstallerLayout),
            ("Audio output and radio controls fit all translated pages", AudioEditorsLayout),
            ("Audio level labels receive bounded property updates", AudioLevelNotifications),
            ("Rejected maintenance does not enter the operation engine", RejectedMaintenance),
            ("Embedded navigation exposes only actions applicable to each step", EmbeddedNavigation),
            ("Recording search filters display without changing clips or mute state", RecordingSearch),
            ("Diagnostics explains empty state and retains complete discovery warnings", DiagnosticPresentation),
            ("Synthetic language mismatch is explicit and never affects recorded mode", VoiceLanguageWarning),
            ("Installer completion after shell disposal does not restart inventory", DisposedInstallerSession),
            ("Runtime labels relocalize without replacing pending audio edits", RelocalizeAudioPresentation),
            ("Catalogue filters combine locally and clear hidden download targets", CatalogueFilters),
            ("Pending audio edits require an explicit decision and preserve live levels", PendingAudioEdits),
            ("Themed prompts default to cancellation and expose their full message", SafeOperationPrompt),
            ("File picker locations are purpose-specific and never create folders", PickerLocationHistory),
            ("Diagnostic severity filtering preserves continuation details", DiagnosticSeverityFiltering),
            ("Maximized bounds respect taskbars and secondary monitor origins", MaximizedBounds)
            ,("Mission update opening and missing primary never trigger a download", MissionUpdateEntry)
            ,("Mission release confirmation gates download and preserves Community", MissionUpdateConfirmation)
            ,("World data opens without runtime and keeps edits local until confirmed", WorldDataEntry)
            ,("World editor fits translated forms without overlapping controls", WorldDataLayout)
            ,("World revision reaches both audio modes without starting runtime", WorldRevisionConfiguration)
            ,("Synthetic pack fallback respects saved choice language and English", TextPackFallbackSelection)
            ,("Synthetic pack selection follows the worker and excludes unusable packs", TextPackRuntimeSelection)
            ,("Missing archive scan errors are recoverable and retryable", CommandErrorsAreRecoverable)
        ];
        var failures = 0;
        foreach (var (name, test) in tests)
        {
            Unhandled.Clear();
            try { test(); Check(Unhandled.Count == 0, "Unhandled dispatcher exception: " + string.Join(Environment.NewLine, Unhandled)); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed; {failures} failed. No windows, audio or runtime launched.");
        return failures == 0 ? 0 : 1;
    }

    private static void DiagnosticSeverityFiltering()
    {
        const string log = "10:00:00 Ready\n10:00:01 [WARN] missing clip\nclip path\n10:00:02 [ERROR] failure\nstack detail\n10:00:03 recovered";
        Check(DiagnosticLogFilter.Apply(log, "all") == log, "All filter changed the raw log");
        var warnings = DiagnosticLogFilter.Apply(log, "warnings");
        Check(warnings.Contains("clip path") && warnings.Contains("stack detail") && !warnings.Contains("recovered"), "Warnings filter lost context or included unrelated entries");
        var errors = DiagnosticLogFilter.Apply(log, "errors");
        Check(errors.Contains("failure") && errors.Contains("stack detail") && !errors.Contains("clip path"), "Error filter mixed entry severities");
        using var f = new Fixture();
        typeof(CompanionViewModel).GetProperty("RuntimeLog")!.SetValue(f.Vm, "10:00:00 Ready");
        f.Vm.DiagnosticLevel = "errors";
        Check(f.Vm.DiagnosticText == f.Vm.L["C.Diagnostics.NoMatches"] && !f.Vm.CopyRuntimeLogCommand.CanExecute(null), "Empty filtered result has misleading content");
    }

    private static void PickerLocationHistory()
    {
        using var f = new Fixture();
        var history = new FilePickerLocations();
        var expected = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Check(history.InitialDirectory("recorded") == expected, "Picker defaults to the launcher folder");
        Directory.CreateDirectory(f.WorkFolder);
        history.RememberFile("recorded", Path.Combine(f.WorkFolder, "selected.xml"));
        Check(history.InitialDirectory("recorded") == f.WorkFolder, "Accepted parent folder was not remembered");
        Check(history.InitialDirectory("profiles") == expected, "Unrelated picker purposes share history");
        var missing = Path.Combine(f.WorkFolder, "missing");
        Check(history.InitialDirectory("profiles", missing) == expected && !Directory.Exists(missing), "Lookup creates or selects nonexistent directories");
        Directory.Delete(f.WorkFolder);
        Check(history.InitialDirectory("recorded") == expected, "Removed remembered folder was reused");
        Check(f.Vm.WorkFolderDisplay == f.Vm.L["C.Tools.WorkFolderEmpty"], "Missing work folder has no guidance");
    }

    private static void SafeOperationPrompt()
    {
        var prompt = new OperationPrompt("Review operation", "Target: fixture only\nNo operation is executed by this test.", "Continue", "Cancel", true);
        var cancel = (Button)prompt.FindName("CancelAction");
        var accept = (Button)prompt.FindName("AcceptAction");
        Check(cancel.IsDefault && cancel.IsCancel && !accept.IsDefault, "Enter/Escape can accidentally approve a prompt");
        bool? decision = null;
        prompt.Decision += value => decision = value;
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(decision == false, "Cancel did not reject the action");
        accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(decision == true, "Explicit acceptance did not return true");
        prompt.Measure(new Size(640, 440)); prompt.Arrange(new Rect(0, 0, 640, 440)); prompt.UpdateLayout();
        Check(((TextBox)prompt.FindName("Message")).IsReadOnly && cancel.ActualHeight >= 36, "Prompt readability/control sizing regressed");
        var information = new OperationPrompt("Information", "Details", "", "Close", false);
        Check(((Button)information.FindName("AcceptAction")).Visibility == Visibility.Collapsed, "Information has an unnecessary apply action");
    }

    private static void PendingAudioEdits()
    {
        var dialogs = new RejectingDialogs();
        using var f = new Fixture(dialogs: dialogs);
        Check(!f.Vm.HasPendingAudioEdits, "Fresh editor starts dirty");
        f.Vm.SyntheticVolume = 42;
        Check(f.Vm.HasPendingAudioEdits && !f.Vm.TestCommand.CanExecute(null) && !f.Vm.StartCommand.CanExecute(null),
            "Pending settings can be applied implicitly through playback/connect");
        Check(!f.Vm.TryLeaveAudioEdits() && f.Vm.SyntheticVolume == 42, "Rejecting discard lost edits");
        f.Vm.RecordedVolume = 27;
        dialogs.Result = true;
        Check(f.Vm.TryLeaveAudioEdits() && f.Vm.SyntheticVolume == 100 && f.Vm.RecordedVolume == 27 && !f.Vm.HasPendingAudioEdits,
            "Discard failed or reverted live recording volume");
        var sound = new RecordedSoundViewModel(1, "fixture", 2, true, "detail");
        var actor = new ActorVoiceViewModel(8, "actor") { Speed = 1.2, Pool = ["a"] };
        f.Vm.RecordedSounds.Add(sound); f.Vm.Actors.Add(actor);
        // Accepted runtime-state boundary, without launching a worker or saving files.
        typeof(CompanionViewModel).GetMethod("CaptureAudioDraft", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Vm, null);
        sound.Enabled = false; actor.Speed = 2; actor.Pool = ["b"];
        Check(f.Vm.HasPendingAudioEdits, "Row edits were not tracked");
        Check(f.Vm.TryLeaveAudioEdits() && sound.Enabled && actor.Speed == 1.2 && actor.Pool.SequenceEqual(["a"]),
            "Discard did not restore row values");
        f.Vm.CallSign = "draft";
        f.Vm.CallSign = string.Empty;
        Check(!f.Vm.HasPendingAudioEdits, "Undoing the last edit leaves a false pending warning");
    }

    private static void CatalogueFilters()
    {
        using var f = new Fixture();
        var english = new CompanionChoice("a", "en_US · Alice", "25 MB · MIT", "en_US", "high", true);
        var german = new CompanionChoice("b", "de_DE · Eva", "10 MB · CC0", "de_DE", "low", false);
        var other = new CompanionChoice("c", "en_US · Bob", "15 MB · MIT", "en_US", "low", false);
        f.Vm.CatalogVoices.Add(english); f.Vm.CatalogVoices.Add(german); f.Vm.CatalogVoices.Add(other);
        f.Vm.CatalogVoice = german;
        f.Vm.CatalogLanguage = "en_US";
        Check(f.Vm.CatalogVoice is null && !f.Vm.InstallVoiceCommand.CanExecute(null), "Hidden voice remains a download target");
        f.Vm.CatalogQuality = "high";
        f.Vm.CatalogInstalledOnly = true;
        f.Vm.CatalogSearch = "mit";
        Check(f.Vm.FilteredCatalogVoices.Cast<CompanionChoice>().SequenceEqual([english]), "Combined filters did not use catalogue metadata");
        f.Vm.CatalogSearch = "CC0";
        Check(f.Vm.FilteredCatalogVoices.IsEmpty, "Search ignored active filters");
        f.Vm.CatalogLanguage = string.Empty; f.Vm.CatalogQuality = string.Empty;
        f.Vm.CatalogInstalledOnly = false; f.Vm.CatalogSearch = string.Empty;
        Check(f.Vm.FilteredCatalogVoices.Cast<CompanionChoice>().SequenceEqual([german, english, other]), "Reset did not restore a sorted catalogue");
        Check(f.Vm.CatalogVoices.Count == 3 && !german.Installed, "Filtering mutated installation metadata");
    }

    private static void RelocalizeAudioPresentation()
    {
        using var f = new Fixture();
        var device = new CompanionChoice("", f.Vm.L["C.Audio.DefaultDevice"]);
        var voice = new CompanionChoice("", f.Vm.L["C.Voice.Automatic"]);
        f.Vm.Devices.Add(device);
        f.Vm.Voices.Add(voice);
        f.Vm.RecordedDevice = device;
        f.Vm.HeadsetDevice = device;
        var actor = new ActorVoiceViewModel(8, "Dispatcher") { Voice = voice, Speed = 1.8, Random = true, Pool = ["keep"] };
        f.Vm.Actors.Add(actor);
        var sound = new RecordedSoundViewModel(7, "L:FIXTURE", 2, false, "old text", "Crew", "fixture.wav", "raw warning");
        f.Vm.RecordedSounds.Add(sound);
        f.Vm.RecordedSound = sound;
        f.Vm.RecordedSearch = "fixture";
        foreach (var culture in new[] { "de-DE", "it-IT", "en-US" })
        {
            f.Localization.SetCulture(culture);
            Pump();
            Check(device.Name == f.Vm.L["C.Audio.DefaultDevice"] && voice.Name == f.Vm.L["C.Voice.Automatic"],
                "Cached choice label retained the old language");
            Check(sound.Detail.Contains(f.Vm.L["C.Recorded.Channel.Crew"]) && sound.Detail.Contains("fixture.wav") && sound.Detail.Contains("raw warning"),
                "Recorded presentation was stale or lost raw metadata");
            Check(ReferenceEquals(f.Vm.RecordedDevice, device) && ReferenceEquals(f.Vm.HeadsetDevice, device) && ReferenceEquals(actor.Voice, voice),
                "Relocalization replaced selected choice objects");
            Check(actor.Speed == 1.8 && actor.Random && actor.Pool.SequenceEqual(["keep"]) && !sound.Enabled &&
                ReferenceEquals(f.Vm.RecordedSound, sound) && f.Vm.RecordedSearch == "fixture", "Relocalization discarded pending edits");
        }
    }

    private static void DisposedInstallerSession()
    {
        using var f = new Fixture(savedCommunity: true);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = false;
        f.Vm.InstallerRequested += _ => { entered = true; return completion.Task; };
        f.Vm.InstallCommand.Execute(null);
        Pump();
        Check(entered, "Installer session did not begin");
        var section = f.Vm.Section;
        f.Vm.DisposeAsync().AsTask().GetAwaiter().GetResult();
        f.Vm.DisposeAsync().AsTask().GetAwaiter().GetResult();
        completion.SetResult();
        for (var attempt = 0; attempt < 10; attempt++) { Pump(); Thread.Sleep(1); }
        Check(f.Vm.IsDisposed && f.Vm.Section == section && !f.Vm.IsBusy,
            "A disposed shell resumed inventory/navigation");
        f.Installer.Dispose();
        f.Installer.Dispose();
    }

    private static void VoiceLanguageWarning()
    {
        using var f = new Fixture(dialogs: new RejectingDialogs { Result = true });
        f.Vm.Voices.Add(new CompanionChoice("", "Automatic"));
        f.Vm.Voices.Add(new CompanionChoice("win:it", "Italian voice", Family: "it"));
        Check(!f.Vm.HasMatchingVoiceLanguage && f.Vm.AudioAvailabilityHint.Contains("EN"),
            "Missing English voice is not explained");
        f.Vm.VoiceLanguage = "IT";
        Check(f.Vm.HasMatchingVoiceLanguage && f.Vm.AudioAvailabilityHint.Length == 0,
            "Matching installed voice still shows a warning");
        f.Vm.VoiceLanguage = "DE";
        Check(f.Vm.AudioAvailabilityHint.Contains("DE"), "Language change retained an obsolete warning");
        var settings = (CompanionSettings)typeof(CompanionViewModel)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Vm)!;
        settings.AudioMode = CompanionAudioMode.Recorded;
        f.Vm.RecordedPacks.Add(new CompanionChoice("recorded.xml", "Recorded pack"));
        Check(f.Vm.AudioAvailabilityHint.Length == 0, "Synthetic voice availability leaked into recorded mode");
        Check(f.Vm.Voices.Count == 2, "Warning changed installed voices");
    }

    private static void DiagnosticPresentation()
    {
        using var f = new Fixture();
        Check(f.Vm.DiagnosticText == f.Vm.L["C.Diagnostics.Empty"] && !f.Vm.CopyRuntimeLogCommand.CanExecute(null),
            "Empty diagnostics is unexplained or allows copying placeholder text");
        var warning = "A long discovery warning" + Environment.NewLine + "with its complete file path and detail";
        typeof(CompanionViewModel).GetProperty("RecordedDiagnostics")!.SetValue(f.Vm, warning);
        typeof(CompanionViewModel).GetProperty("RuntimeLog")!.SetValue(f.Vm, "Runtime message");
        Check(f.Vm.DiagnosticText.Contains(warning) && f.Vm.DiagnosticText.Contains("Runtime message"),
            "Diagnostics truncated discovery or runtime details");
        Check(f.Vm.CopyRuntimeLogCommand.CanExecute(null), "Populated diagnostics cannot be copied");
        var sound = new RecordedSoundViewModel(1, "fixture", 1, true, warning);
        Check(!sound.Summary.Contains('\n') && sound.Detail == warning, "Compact summary changed the full detail");
    }

    private static void RecordingSearch()
    {
        using var f = new Fixture();
        var radio = new RecordedSoundViewModel(1, "RADIO_DISPATCH", 3, true, "dispatcher.wav");
        var cabin = new RecordedSoundViewModel(2, "CABIN_READY", 2, false, "doctor-ready.wav");
        f.Vm.RecordedSounds.Add(radio);
        f.Vm.RecordedSounds.Add(cabin);
        f.Vm.RecordedSearch = "  DOCTOR  ";
        Check(f.Vm.FilteredRecordedSounds.Cast<RecordedSoundViewModel>().SequenceEqual([cabin]), "Filename search failed");
        f.Vm.RecordedSearch = "radio";
        Check(f.Vm.FilteredRecordedSounds.Cast<RecordedSoundViewModel>().SequenceEqual([radio]), "Variable search failed");
        f.Vm.RecordedSearch = "absent";
        Check(f.Vm.FilteredRecordedSounds.IsEmpty, "Unknown query did not produce an empty view");
        f.Vm.RecordedSearch = string.Empty;
        Check(f.Vm.FilteredRecordedSounds.Cast<RecordedSoundViewModel>().SequenceEqual([radio, cabin]), "Clearing search lost clips");
        Check(f.Vm.RecordedSounds.Count == 2 && radio.Enabled && !cabin.Enabled && radio.Variants == 3,
            "Search mutated the underlying sound configuration");
    }

    private static void RejectedMaintenance()
    {
        var dialogs = new RejectingDialogs();
        using var f = new Fixture(dialogs: dialogs, savedCommunity: true);
        var before = Directory.GetFileSystemEntries(f.WorkFolder, "*", SearchOption.AllDirectories);
        RunCommand(f.Vm.MaintainCommand);
        Check(dialogs.Confirmations == 1 && dialogs.Message.Contains(f.WorkFolder), "Maintenance omitted its target confirmation");
        Check(f.Vm.Status == f.Vm.L["Status.Cancelled"] && !f.Vm.IsBusy, "Rejected maintenance did not return idle");
        Check(Directory.GetFileSystemEntries(f.WorkFolder, "*", SearchOption.AllDirectories).SequenceEqual(before),
            "Rejected maintenance changed the fixture Community");
    }

    private static void EmbeddedNavigation()
    {
        using var f = new Fixture();
        f.Wizard.BeginCompanionWorkflow();
        var view = new InstallerView { DataContext = f.Installer };
        var back = (Button)view.FindName("PreviousButton");
        var next = (Button)view.FindName("ContinueButton");
        var close = (Button)view.FindName("ReturnButton");
        Pump();
        Check(back.Visibility == Visibility.Collapsed && close.IsEnabled, "Files has no clean exit");
        f.Wizard.MoveNext();
        Pump();
        Check(back.Visibility == Visibility.Visible, "Review has no return to files");
        f.Wizard.MoveNext();
        Pump();
        Check(next.Visibility == Visibility.Collapsed, "Installation exposes a redundant Next");
        f.Wizard.MoveNext();
        Pump();
        Check(next.Visibility == Visibility.Collapsed && back.Visibility == Visibility.Collapsed && close.IsEnabled,
            "Completed exposes obsolete navigation");
    }

    private sealed class RejectingDialogs : IUserDialogService
    {
        public int Confirmations { get; private set; }
        public string Message { get; private set; } = string.Empty;
        public bool Result { get; set; }
        public bool Confirm(string title, string message) { Confirmations++; Message = message; return Result; }
        public void ShowError(string title, string message) => throw new InvalidOperationException(message);
        public void ShowInformation(string title, string message) => throw new InvalidOperationException(message);
    }

    private static void AudioLevelNotifications()
    {
        using var f = new Fixture();
        var changed = new HashSet<string>();
        f.Vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);
        foreach (var name in new[] { "HeadsetLevel", "ExternalLevel", "NotifyLevel", "RadioNoise", "BeepLevel" })
        {
            var property = typeof(CompanionViewModel).GetProperty(name)!;
            property.SetValue(f.Vm, 37);
            Check(changed.Contains(name), name + " did not notify its numeric label");
            property.SetValue(f.Vm, 120);
            Check((int)property.GetValue(f.Vm)! == 100, name + " exceeded 100%");
            property.SetValue(f.Vm, -4);
            Check((int)property.GetValue(f.Vm)! == 0, name + " became negative");
        }
    }

    private static void AudioEditorsLayout()
    {
        using var f = new Fixture();
        var view = f.Options();
        view.SelectPage("Voices");
        var tabs = (TabControl)view.FindName("SyntheticTabs");
        foreach (var scale in new[] { 1d, 1.25, 1.5 })
        foreach (var culture in new[] { "en-US", "de-DE", "fr-FR", "es-ES", "it-IT" })
        foreach (var pair in new[] { ("OutputTab", "CallSignEditor"), ("RadioTab", "RadioTailEditor") })
        {
            f.Localization.SetCulture(culture);
            VisualTreeHelper.SetRootDpi(view, new DpiScale(scale, scale));
            Check(VisualTreeHelper.GetDpi(view).DpiScaleX == scale, "WPF did not accept the test DPI");
            tabs.SelectedItem = view.FindName(pair.Item1);
            view.InvalidateMeasure();
            view.Measure(new Size(1000, 460));
            view.Arrange(new Rect(0, 0, 1000, 460));
            view.UpdateLayout();
            Pump();
            var editor = (FrameworkElement)view.FindName(pair.Item2);
            var origin = editor.TranslatePoint(new Point(), view);
            var save = (FrameworkElement)view.FindName("SaveAudioButton");
            var saveOrigin = save.TranslatePoint(new Point(), view);
            Check(editor.ActualHeight > 0 && origin.Y >= 0 && origin.Y + editor.ActualHeight <= saveOrigin.Y,
                culture + " " + pair.Item2 + " is clipped or overlaps Save: " + origin + " / " + editor.RenderSize);
            Check(origin.X >= 0 && origin.X + editor.ActualWidth <= 1000,
                culture + " " + pair.Item2 + " exceeds the page width");
        }
    }

    private static void EmbeddedInstallerLayout()
    {
        using var f = new Fixture();
        f.Wizard.BeginCompanionWorkflow();
        for (var index = 0; index < 13; index++)
        {
            var definition = new PackageDefinition("fixture-" + index, "Installed package with a long descriptive name " + index,
                true, index, ".*", new HashSet<SimulatorType> { SimulatorType.Msfs2024 }, string.Empty);
            f.Installer.CommunityPackages.Add(new PackageStatusViewModel(
                new PackageScanResult(definition, PackageState.Installed, null, [], null), "Installed", "Required"));
        }
        var view = new InstallerView { DataContext = f.Installer };
        foreach (var scale in new[] { 1d, 1.25, 1.5 })
        foreach (var culture in new[] { "en-US", "de-DE", "fr-FR", "es-ES", "it-IT" })
        {
            f.Localization.SetCulture(culture);
            VisualTreeHelper.SetRootDpi(view, new DpiScale(scale, scale));
            Check(VisualTreeHelper.GetDpi(view).DpiScaleX == scale, "WPF did not accept the test DPI");
            view.InvalidateMeasure();
            view.Measure(new Size(1000, 520));
            view.Arrange(new Rect(0, 0, 1000, 520));
            view.UpdateLayout();
            Pump();
            var drop = (FrameworkElement)view.FindName("FileDropArea");
            var origin = drop.TranslatePoint(new Point(), view);
            Check(drop.ActualHeight >= 180 && drop.ActualWidth >= 400,
                culture + ": file drop is too small: " + drop.RenderSize);
            Check(origin.Y >= 0 && origin.Y + drop.ActualHeight <= 520,
                culture + ": file drop was pushed outside the viewport");
            Check(f.Installer.CurrentStep == WizardStep.ZipFolder && !f.Installer.BackCommand.CanExecute(null),
                "Embedded UI exposed previous onboarding");
        }
    }

    private static void CommunityCancellation()
    {
        using var f = new Fixture();
        RunCommand(f.Vm.ContinueCommand);
        Check(!f.Vm.CanCancelCommunity, "First-run folder selection can be bypassed");
        f.Vm.NavigateCommand.Execute("Mission");
        Pump();
        var before = File.ReadAllText(f.SettingsPath);
        var simulator2020 = f.Vm.IsMsfs2020;
        f.Vm.NavigateCommand.Execute("Community");
        Pump();
        Check(f.Vm.Section == "Community" && f.Vm.CanCancelCommunity, "No return route");
        if (simulator2020) f.Vm.IsMsfs2024 = true; else f.Vm.IsMsfs2020 = true;
        Check(File.ReadAllText(f.SettingsPath) == before, "Provisional selection mutated saved settings");
        Check(f.Vm.CancelCommunityCommand.CanExecute(null), "Back command is disabled");
        f.Vm.CancelCommunityCommand.Execute(null);
        Pump();
        Check(f.Vm.Section == "Mission" && f.Vm.IsMsfs2020 == simulator2020,
            "Back did not restore the original context");
        Check(File.ReadAllText(f.SettingsPath) == before && !f.Vm.CanCancelCommunity,
            "Cancellation changed settings or retained a stale return route");
    }

    private static void SynchronousShutdown()
    {
        var disposed = 0;
        var closed = 0;
        WindowCloseCoordinator? coordinator = null;
        coordinator = new(Dispatcher.CurrentDispatcher,
            () => { disposed++; return Task.CompletedTask; },
            () => { Check(coordinator!.CanClose, "Final close is not permitted"); closed++; },
            error => throw new InvalidOperationException("Unexpected cleanup error", error));
        coordinator.Request();
        coordinator.Request();
        Check(disposed == 0 && closed == 0 && !coordinator.CanClose,
            "Cleanup reentered the original Closing event");
        Pump();
        coordinator.Request();
        Pump();
        Check(disposed == 1 && closed == 1, "Repeated close disposed or closed twice");
    }

    private static void AsynchronousShutdown()
    {
        var completion = new TaskCompletionSource();
        var disposed = 0;
        var closed = 0;
        Exception? reported = null;
        var coordinator = new WindowCloseCoordinator(Dispatcher.CurrentDispatcher,
            () => { disposed++; return completion.Task; }, () => closed++, error => reported = error);
        coordinator.Request();
        Pump();
        coordinator.Request();
        Pump();
        Check(disposed == 1 && closed == 0 && !coordinator.CanClose, "Close did not wait for cleanup");
        var fault = new IOException("Fixture cleanup failure");
        completion.SetException(fault);
        Pump();
        Check(ReferenceEquals(reported, fault) && closed == 1 && coordinator.CanClose,
            "Cleanup failure prevented final close or was not reported");
    }

    private static void ToolsDiscovery()
    {
        using var f = new Fixture();
        RunCommand(f.Vm.ContinueCommand);
        var view = f.Options();
        f.Vm.OptionsRequested += view.SelectPage;
        RunCommand(f.Vm.OpenMaintenanceCommand);
        Check(f.DiscoveryCalls == 1, $"Expected discovery once; got {f.DiscoveryCalls}");
        Check(f.Vm.WorkPath == f.WorkFolder, "Discovered path not selected");
        Check(f.Store.Load().WorkFolderPath == f.WorkFolder, "Path not persisted");
        Check(f.Vm.Section == "Options", "Tools not inline");
        f.Vm.ReturnFromOptions();
        Check(f.Vm.Section == "Mission", "Wrong return destination");
    }

    private static void ToolsPreservesFolder()
    {
        using var f = new Fixture(savedWorkFolder: true);
        RunCommand(f.Vm.ContinueCommand);
        var view = f.Options();
        f.Vm.OptionsRequested += view.SelectPage;
        RunCommand(f.Vm.OpenMaintenanceCommand);
        Check(f.DiscoveryCalls == 0 && f.Vm.WorkPath == f.WorkFolder, "Existing path replaced");
    }

    private static void VolumeBindings()
    {
        using var f = new Fixture();
        var view = f.Options();
        view.SelectPage("Voices");
        var inline = Descendants(view).OfType<Slider>().Single(s => BoundPath(s, RangeBase.ValueProperty) == "SyntheticVolume");
        var main = new Slider { Minimum = 0, Maximum = 100, DataContext = f.Vm };
        main.SetBinding(RangeBase.ValueProperty, new Binding("SyntheticVolume") { Mode = BindingMode.TwoWay });
        Pump();
        main.SetCurrentValue(RangeBase.ValueProperty, 37d);
        Pump();
        Check(f.Vm.SyntheticVolume == 37 && inline.Value == 37, $"Main -> inline: VM={f.Vm.SyntheticVolume}, inline={inline.Value}");
        inline.SetCurrentValue(RangeBase.ValueProperty, 62d);
        Pump();
        Check(f.Vm.SyntheticVolume == 62 && main.Value == 62, "Inline -> main did not notify");
    }

    private static void SyntheticEditGates()
    {
        using var f = new Fixture();
        var view = f.Options();
        view.SelectPage("Voices");
        var editors = Descendants((DependencyObject)view.FindName("SyntheticOptions"))
            .OfType<Control>().Where(c => c is Slider or ComboBox or CheckBox or TextBox).ToArray();
        Check(editors.Length >= 12, "Synthetic controls were not traversed");
        f.Vm.CustomCallSign = true;
        Pump();
        SetState(f.Vm, "IsRunning", true);
        Check(editors.All(c => !c.IsEnabled), "At least one synthetic editor remains enabled while running");
        Check(!f.Vm.SaveAudioCommand.CanExecute(null), "Save enabled while running");
        SetState(f.Vm, "IsRunning", false);
        Check(editors.All(c => c.IsEnabled), "Editors did not re-enable after disconnect");
        SetState(f.Vm, "IsBusy", true);
        Check(editors.All(c => !c.IsEnabled), "At least one synthetic editor remains enabled while busy");
        SetState(f.Vm, "IsBusy", false);
        Check(editors.All(c => c.IsEnabled), "Editors did not re-enable after operation");
    }

    private static void WelcomeFailure()
    {
        using var f = new Fixture();
        // Real store fault, not a fake VM: File.WriteAllText cannot overwrite a directory.
        Directory.CreateDirectory(f.SettingsPath + ".new");
        RunCommand(f.Vm.ContinueCommand);
        Check(Unhandled.Count == 0, "Welcome save escaped to dispatcher: " + string.Join(Environment.NewLine, Unhandled));
        Check(!f.Vm.HasEntered && f.Vm.Section == "Disclaimer", "Failed save dismissed the notice");
        Check(!f.Store.Load().WelcomeShown, "Acceptance was silently persisted");
        Check(f.Vm.Status == f.Vm.L["C.Status.Error"] && f.Vm.Detail.Length > 0, "No recoverable error shown");
        Directory.Delete(f.SettingsPath + ".new");
        RunCommand(f.Vm.ContinueCommand);
        Check(f.Vm.HasEntered && f.Store.Load().WelcomeShown, "Retry did not persist acceptance");
        Check(f.Vm.Section == "Community", "Retry did not route normally");
        Check(f.Vm.Status != f.Vm.L["C.Status.Error"] && f.Vm.Detail.Length == 0,
            "Successful retry retained the previous persistence error");
    }

    private static void WelcomeSuccess()
    {
        using var f = new Fixture();
        Check(f.Vm.Section == "Disclaimer" && !f.Store.Load().WelcomeShown, "Constructor accepted notice");
        RunCommand(f.Vm.ContinueCommand);
        Check(f.Vm.HasEntered && f.Store.Load().WelcomeShown, "Continue did not persist");
        var next = new CompanionViewModel(f.Localization, f.Engine, f.Installer, f.Store, () => []);
        try { Check(next.Section == "Loading" && !next.HasEntered, "Returning profile flashed Disclaimer"); }
        finally { next.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void SetState(CompanionViewModel vm, string property, bool value)
    {
        // Exercise real property notifications at the runtime-state boundary without a worker.
        typeof(CompanionViewModel).GetProperty(property)!.SetValue(vm, value);
        Pump();
    }

    private static string? BoundPath(DependencyObject target, DependencyProperty property) =>
        BindingOperations.GetBinding(target, property)?.Path.Path;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static void RunCommand(ICommand command)
    {
        Check(command.CanExecute(null), "Command initially disabled");
        command.Execute(null);
        var clock = Stopwatch.StartNew();
        do
        {
            Pump();
            if (command.CanExecute(null)) return;
            Thread.Sleep(1);
        } while (clock.Elapsed < TimeSpan.FromSeconds(10));
        throw new InvalidOperationException("Command did not complete within 10 seconds");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "RE-AppTests-" + Guid.NewGuid().ToString("N"));
        public string SettingsPath => Path.Combine(_root, "settings.json");
        public string WorkFolder => Path.Combine(_root, "work");
        public CompanionSettingsStore Store { get; }
        public InstallerEngine Engine { get; } = new("RE-AppTests");
        public JsonLocalizationService Localization { get; }
        public MainWindowViewModel Installer { get; }
        public WizardState Wizard { get; } = new();
        public CompanionViewModel Vm { get; }
        public int DiscoveryCalls { get; private set; }

        public Fixture(bool savedWorkFolder = false, IUserDialogService? dialogs = null, bool savedCommunity = false, IMissionUpdateService? missionUpdates = null)
        {
            Directory.CreateDirectory(_root);
            if (savedCommunity) Directory.CreateDirectory(WorkFolder);
            Store = new CompanionSettingsStore(SettingsPath);
            Store.Save(new CompanionSettings { AudioMode = CompanionAudioMode.Synthetic,
                AudioStoragePath = Path.Combine(_root, "audio"),
                WorkFolderPath = savedWorkFolder ? WorkFolder : string.Empty,
                CommunityPath = savedCommunity ? WorkFolder : string.Empty });
            var assembly = typeof(CompanionViewModel).Assembly;
            Localization = new JsonLocalizationService(culture => assembly.GetManifestResourceStream(
                $"RandomEverywhereInstaller.App.Localization.{culture}.json")!);
            Installer = new MainWindowViewModel(Localization, Wizard, new WindowsFolderPickerService(),
                new WindowsDialogService(), Engine, new GitHubReleaseUpdateService(), ownsEngine: false);
            Vm = new CompanionViewModel(Localization, Engine, Installer, Store, () => { DiscoveryCalls++; return [WorkFolder]; }, dialogs, missionUpdates);
        }

        public CompanionOptionsView Options() => new() { DataContext = Vm };

        public void Dispose()
        {
            var runtime = typeof(CompanionViewModel).GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Vm);
            Check(runtime is null, "Test unexpectedly created an audio runtime client");
            Check(Application.Current.Windows.Count == 0, "Test unexpectedly created a Window");
            Vm.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Installer.Dispose();
            Engine.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
