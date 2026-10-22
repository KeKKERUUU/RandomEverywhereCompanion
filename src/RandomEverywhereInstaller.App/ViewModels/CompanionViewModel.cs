using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using RandomEverywhereInstaller.App.Infrastructure;
using RandomEverywhereInstaller.App.Services;
using RandomEverywhereInstaller.Core.Companion;
using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Core.Localization;
using RandomEverywhereInstaller.Core.Models;
using RandomEverywhereInstaller.Core.Services;
using RandomEverywhereInstaller.Core.Wizard;
using RandomEverywhereInstaller.Infrastructure.Companion;
using RandomEverywhereInstaller.Infrastructure.Installer;

namespace RandomEverywhereInstaller.App.ViewModels;

public sealed class LocalizedText(ILocalizationService service) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => service.GetText(key);
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}
public sealed record CompanionChoice(string Id, string Name, string Detail = "", string Family = "", string Quality = "", bool Installed = false) : INotifyPropertyChanged
{
    public string Name { get; private set; } = Name;
    public string Detail { get; private set; } = Detail;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshText(string name, string? detail = null)
    {
        Name = name;
        if (detail is not null) Detail = detail;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
    }
    public override string ToString() => Name;
}
public sealed record CommunityChoice(SimulatorType Simulator, string Path)
{
    public string Name => Simulator == SimulatorType.Msfs2020 ? "Microsoft Flight Simulator 2020" : "Microsoft Flight Simulator 2024";
}
public sealed class ActorVoiceViewModel(int actor, string name) : ObservableObject
{
    private CompanionChoice? _voice;
    private double _speed = 1;
    private bool _random;
    private string _name = name;
    public int Actor { get; } = actor;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public CompanionChoice? Voice { get => _voice; set => SetProperty(ref _voice, value); }
    public double Speed { get => _speed; set => SetProperty(ref _speed, Math.Clamp(value, 0.5, 3)); }
    public bool CanRandom => Actor is 8 or 9 or 12;
    public bool Random { get => _random; set => SetProperty(ref _random, CanRandom && value); }
    private string[] _pool = [];
    public string[] Pool { get => _pool; set => SetProperty(ref _pool, value); }
}

public sealed class RecordedSoundViewModel(int id, string variable, int variants, bool enabled, string detail,
    string channel = "", string declaredFiles = "", string problems = "") : ObservableObject
{
    private bool _enabled = enabled;
    private string _detail = detail;
    public int Id { get; } = id;
    public string Variable { get; } = variable;
    public int Variants { get; } = variants;
    public bool Playable => Variants > 0;
    public string Detail => _detail;
    public string Summary => Detail.ReplaceLineEndings(" · ");
    public void RefreshText(ILocalizationService localization)
    {
        if (channel.Length == 0) return;
        _detail = localization.GetText("C.Recorded.Variants", Variants) + " · " +
            localization.GetText("C.Recorded.Channel." + channel) + Environment.NewLine + declaredFiles;
        if (problems.Length > 0) _detail += Environment.NewLine + problems;
        OnPropertiesChanged(nameof(Detail), nameof(Summary));
    }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
}

/// <summary>Owns the companion session while reusing the existing installer engine.</summary>
public sealed partial class CompanionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ILocalizationService _localization;
    private readonly InstallerEngine _engine;
    private readonly MainWindowViewModel _installer;
    private readonly CompanionSettingsStore _store;
    private readonly Func<IReadOnlyList<string>> _findWorkFolders;
    private readonly CompanionSettings _settings;
    private readonly WindowsFolderPickerService _folders = new();
    private readonly IUserDialogService _dialogs;
    private readonly DispatcherTimer _poll;
    private CompanionRuntimeClient? _runtime;
    private JsonElement _snapshot;
    private string _section = "Disclaimer";
    private string _status;
    private string _detail = string.Empty;
    private string _statusColor = "#9EB5C7";
    private bool _busy;
    private bool _running;
    private bool _simulatorConnected;
    private string _simulatorLinkDetail = string.Empty;
    private bool _polling;
    private bool _disposed;
    private bool _loading;
    private bool _recordedVolumePushInProgress;
    private bool _updatingRecordedSounds;
    private int _recordedVolume = 80;
    private int _syntheticVolume = 100;
    private bool _recordedMasterMuted;
    private string _recordedLastPlayed = string.Empty;
    private RecordedSoundViewModel? _recordedSound;
    private bool _entered;
    private bool _inventoryReady;
    private CommunityChoice? _detectedCommunity;
    private double _progress;
    private string _prerequisites = string.Empty;
    private string _missionVersion = string.Empty;
    private string _inventorySummary = string.Empty;
    private string _workPath = string.Empty;
    private CompanionChoice? _recordedPack;
    private CompanionChoice? _textPack;
    private CompanionChoice? _recordedDevice;
    private CompanionChoice? _headsetDevice;
    private CompanionChoice? _externalDevice;
    private CompanionChoice? _catalogVoice;
    private LanguageOptionViewModel? _language;
    private string _voiceLanguage = "EN";
    private string _engineVersion = string.Empty;
    private string _activeMissionId = string.Empty;
    private H145InstallationInfo? _helicopter;
    private CancellationTokenSource? _helicopterCancellation;

    public CompanionViewModel(ILocalizationService localization, InstallerEngine engine, MainWindowViewModel installer,
        CompanionSettingsStore? settingsStore = null, Func<IReadOnlyList<string>>? findWorkFolders = null,
        IUserDialogService? dialogs = null, RandomEverywhereInstaller.Core.Updates.IMissionUpdateService? missionUpdates = null)
    {
        _localization = localization; _engine = engine; _installer = installer;
        FilteredRecordedSounds = new ListCollectionView(RecordedSounds);
        FilteredRecordedSounds.Filter = item => item is RecordedSoundViewModel sound &&
            (sound.Variable.Contains(RecordedSearch.Trim(), StringComparison.OrdinalIgnoreCase) ||
             sound.Detail.Contains(RecordedSearch.Trim(), StringComparison.OrdinalIgnoreCase));
        FilteredCatalogVoices = new ListCollectionView(CatalogVoices);
        FilteredCatalogVoices.SortDescriptions.Add(new SortDescription(nameof(CompanionChoice.Name), ListSortDirection.Ascending));
        FilteredCatalogVoices.Filter = item => item is CompanionChoice voice &&
            (CatalogLanguage.Length == 0 || voice.Family.Equals(CatalogLanguage, StringComparison.OrdinalIgnoreCase)) &&
            (CatalogQuality.Length == 0 || voice.Quality.Equals(CatalogQuality, StringComparison.OrdinalIgnoreCase)) &&
            (!CatalogInstalledOnly || voice.Installed) &&
            (voice.Name.Contains(CatalogSearch.Trim(), StringComparison.OrdinalIgnoreCase) || voice.Detail.Contains(CatalogSearch.Trim(), StringComparison.OrdinalIgnoreCase));
        _dialogs = dialogs ?? new WindowsDialogService(localization);
        _store = settingsStore ?? new CompanionSettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RandomAndEverywhereCompanion", "settings.json"));
        _findWorkFolders = findWorkFolders ?? CompanionWorkFolderService.FindWorkFolders;
        _settings = _store.Load();
        _section = _settings.WelcomeShown ? "Loading" : "Disclaimer";
        _workPath = _settings.WorkFolderPath;
        if (_localization.SupportedCultures.Contains(_settings.Culture)) _localization.SetCulture(_settings.Culture);
        L = new LocalizedText(localization);
        Languages = installer.Languages;
        _language = Languages.First(option => option.Culture == localization.CurrentCulture);
        _status = T("C.Status.Ready");
        NavigateCommand = new AsyncRelayCommandWithParameter<string>(NavigateAsync, _ => !IsBusy);
        CancelCommunityCommand = new RelayCommand(CancelCommunity, () => !IsBusy && CanCancelCommunity);
        ContinueCommand = new AsyncRelayCommand(EnterCompanionAsync, () => !IsBusy);
        UseDetectedCommunityCommand = new AsyncRelayCommand(UseDetectedCommunityAsync, () => !IsBusy && DetectedCommunity is not null);
        BrowseCommunityCommand = new AsyncRelayCommand(BrowseCommunityAsync, () => !IsBusy && !IsRunning);
        RefreshCommand = new AsyncRelayCommand(RefreshInventoryAsync, () => !IsBusy);
        StartCommand = new AsyncRelayCommand(StartAudioAsync, () => !IsBusy && !IsRunning && !HasPendingAudioEdits && (CanPreviewAudio || _runtime is null && HasCommunity && _inventoryReady));
        StopCommand = new AsyncRelayCommand(StopAudioAsync, () => !IsBusy && IsRunning);
        TestCommand = new AsyncRelayCommand(() => RuntimeActionAsync("test", new { Text = TestText }), () => !IsBusy && !HasPendingAudioEdits && CanPreviewAudio);
        ToggleRecordedMuteCommand = new RelayCommand(() => RecordedMasterMuted = !RecordedMasterMuted, () => CanAdjustRecordedVolume);
        RegisterRecordedCommand = new AsyncRelayCommand(RegisterRecordedAsync, () => CanEditAudio && IsRecordedMode);
        ReloadRecordedCommand = new AsyncRelayCommand(() => RecordedLibraryActionAsync("recorded-reload"), () => CanEditAudio && IsRecordedMode);
        ForgetRecordedCommand = new AsyncRelayCommand(ForgetRecordedAsync, () => CanEditAudio && IsRecordedMode && RecordedPack is not null);
        PreviewRecordedSoundCommand = new AsyncRelayCommand(() => RuntimeActionAsync("recorded-preview", new { Id = RecordedSound!.Id }), () => !IsBusy && !HasPendingAudioEdits && IsRecordedMode && RecordedSound is { Playable: true });
        CopyRuntimeLogCommand = new AsyncRelayCommand(() => ExecuteAsync(() => { Clipboard.SetText(DiagnosticText); return Task.CompletedTask; }), () => !IsBusy && DiagnosticContent.Length > 0);
        SaveAudioCommand = new AsyncRelayCommand(SaveAudioAsync, () => !IsBusy && !IsRunning);
        ImportPackCommand = new AsyncRelayCommand(ImportPackAsync, () => !IsBusy && !IsRunning);
        CatalogCommand = new AsyncRelayCommand(LoadCatalogAsync, () => !IsBusy);
        InstallVoiceCommand = new AsyncRelayCommand(InstallVoiceAsync, () => !IsBusy && !IsRunning && CatalogVoice is not null);
        RecommendedCommand = new AsyncRelayCommand(ApplyRecommendedAsync, () => !IsBusy && !IsRunning);
        ExportProfileCommand = new AsyncRelayCommand(ExportProfileAsync, () => !IsBusy);
        ImportProfileCommand = new AsyncRelayCommand(ImportProfileAsync, () => !IsBusy && !IsRunning);
        InstallCommand = new AsyncRelayCommand(() => OpenInstallerAsync(InstallerMode.FullInstallation), () => !IsBusy && !IsRunning && Directory.Exists(CommunityPath));
        UpdateMissionCommand = new AsyncRelayCommand(() => ShowOptionsAsync("MissionUpdate"), () => !IsBusy && !IsRunning && Directory.Exists(CommunityPath));
        MaintainCommand = new AsyncRelayCommand(MaintainAsync, () => !IsBusy && !IsRunning && Directory.Exists(CommunityPath));
        BrowseWorkCommand = new RelayCommand(BrowseWork, () => !IsBusy);
        CleanDebugCommand = new AsyncRelayCommand(CleanDebugAsync, () => !IsBusy && !IsRunning && Directory.Exists(WorkPath));
        BrowseStorageCommand = new AsyncRelayCommand(BrowseStorageAsync, () => !IsBusy && !IsRunning);
        ImportSimConnectCommand = new AsyncRelayCommand(ImportSimConnectAsync, () => !IsBusy && !IsRunning);
        OpenCommunityCommand = new RelayCommand(() => OpenFolder(CommunityPath), () => Directory.Exists(CommunityPath));
        OpenLogCommand = new RelayCommand(_engine.OpenLogDirectory);
        CheckAppUpdateCommand = new AsyncRelayCommand(CheckAppUpdateAsync, () => !IsBusy && !IsRunning);
        OpenOptionsCommand = new AsyncRelayCommand(() => ShowOptionsAsync("General"), () => !IsBusy);
        OpenAudioOptionsCommand = new AsyncRelayCommand(() => ShowOptionsAsync(IsRecordedMode ? "Audio" : "Voices"), () => !IsBusy && !IsRunning);
        OpenMaintenanceCommand = new AsyncRelayCommand(() => ShowOptionsAsync("Tools"), () => !IsBusy && !IsRunning);
        UpdateH145Command = new AsyncRelayCommand(UpdateH145Async, () => !IsBusy && !IsRunning && _helicopter is { Installed: true });
        CancelH145Command = new RelayCommand(() => _helicopterCancellation?.Cancel(), () => _helicopterCancellation is not null);
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await PollAsync(), Dispatcher.CurrentDispatcher);
        _localization.CultureChanged += CultureChanged;
        InitializeAudioDraftTracking();
        InitializeMissionUpdates(missionUpdates);
        InitializeWorldData();
    }

    public event Func<InstallerMode, Task>? InstallerRequested;
    public event Action<string>? OptionsRequested;
    public LocalizedText L { get; }
    public ObservableCollection<LanguageOptionViewModel> Languages { get; }
    public ObservableCollection<CompanionChoice> RecordedPacks { get; } = [];
    public ObservableCollection<RecordedSoundViewModel> RecordedSounds { get; } = [];
    public ICollectionView FilteredRecordedSounds { get; }
    private string _recordedSearch = string.Empty;
    public string RecordedSearch
    {
        get => _recordedSearch;
        set { if (SetProperty(ref _recordedSearch, value ?? string.Empty)) FilteredRecordedSounds.Refresh(); }
    }
    public ObservableCollection<CompanionChoice> TextPacks { get; } = [];
    public ObservableCollection<CompanionChoice> Devices { get; } = [];
    public ObservableCollection<CompanionChoice> Voices { get; } = [];
    public ObservableCollection<CompanionChoice> CatalogVoices { get; } = [];
    public ICollectionView FilteredCatalogVoices { get; }
    public ObservableCollection<string> CatalogLanguages { get; } = [string.Empty];
    public ObservableCollection<string> CatalogQualities { get; } = [string.Empty];
    private string _catalogLanguage = string.Empty, _catalogQuality = string.Empty, _catalogSearch = string.Empty;
    private bool _catalogInstalledOnly;
    public string CatalogLanguage { get => _catalogLanguage; set { if (SetProperty(ref _catalogLanguage, value ?? string.Empty)) RefreshCatalogFilter(); } }
    public string CatalogQuality { get => _catalogQuality; set { if (SetProperty(ref _catalogQuality, value ?? string.Empty)) RefreshCatalogFilter(); } }
    public string CatalogSearch { get => _catalogSearch; set { if (SetProperty(ref _catalogSearch, value ?? string.Empty)) RefreshCatalogFilter(); } }
    public bool CatalogInstalledOnly { get => _catalogInstalledOnly; set { if (SetProperty(ref _catalogInstalledOnly, value)) RefreshCatalogFilter(); } }
    private void RefreshCatalogFilter()
    {
        FilteredCatalogVoices.Refresh();
        if (CatalogVoice is not null && !FilteredCatalogVoices.Contains(CatalogVoice)) CatalogVoice = null;
    }
    public ObservableCollection<ActorVoiceViewModel> Actors { get; } = [];
    public ObservableCollection<PackageStatusViewModel> InstalledPackages { get; } = [];
    public ObservableCollection<CommunityChoice> DetectedCommunities { get; } = [];
    public CommunityChoice? DetectedCommunity { get => _detectedCommunity; set { if (SetProperty(ref _detectedCommunity, value)) RefreshCommands(); } }
    public bool HasCommunity => Directory.Exists(CommunityPath);
    public bool HasDetectedCommunities => DetectedCommunities.Count > 0;
    public bool IsRecordedMode => _settings.AudioMode == CompanionAudioMode.Recorded;
    public bool IsSyntheticMode => !IsRecordedMode;
    public bool CanEditAudio => !IsBusy && !IsRunning;
    public bool CanAdjustRecordedVolume => !IsBusy && IsRecordedMode;
    public string SimulatorConnectionStatus => T(!IsRunning ? "C.Connection.Disconnected"
        : _simulatorConnected ? "C.Connection.Connected" : "C.Connection.Waiting");
    public string SimulatorConnectionColor => !IsRunning ? "#A8B6C0" : _simulatorConnected ? "#8ACBA6" : "#E2BD7D";
    public string ConnectionActionHint => IsBusy ? T("C.Status.Working")
        : _runtime is null ? T("C.Connection.Hint")
        : CanPreviewAudio ? T("C.Connection.Hint")
        : string.IsNullOrWhiteSpace(AudioAvailabilityHint) ? T("C.Flow.NoPack") : AudioAvailabilityHint;
    public string SimulatorLinkDetail { get => _simulatorLinkDetail; private set => SetProperty(ref _simulatorLinkDetail, value); }
    public bool ShowProgress => IsBusy;
    public bool IsIndeterminate => IsBusy && Progress <= 0;
    public string SimulatorName => IsMsfs2020 ? "MSFS 2020" : "MSFS 2024";
    public string AudioModeName => T(IsRecordedMode ? "C.Flow.Recorded" : "C.Flow.Synthetic");
    public string SelectedPackName => (IsRecordedMode ? RecordedPack : TextPack)?.Name ?? T("C.Flow.NoPack");
    public bool CanPreviewAudio => IsRecordedMode ? RecordedPack is not null && RecordedSounds.Any(s => s.Playable) : TextPack is not null && Voices.Count > 1;
    public string AudioAvailabilityHint => IsBusy ? string.Empty : IsRecordedMode && RecordedPacks.Count == 0 ? T("C.Flow.NoRecordings")
        : IsSyntheticMode && Voices.Count <= 1 ? T("C.Flow.NoVoices")
        : IsSyntheticMode && !HasMatchingVoiceLanguage ? T("C.Voice.LanguageMismatch", VoiceLanguage) : string.Empty;
    public bool HasMatchingVoiceLanguage => Voices.Any(voice => !string.IsNullOrEmpty(voice.Id) &&
        voice.Family.Equals(VoiceLanguage, StringComparison.OrdinalIgnoreCase));
    public string AudioEditsHint => T(IsRecordedMode ? "C.Audio.RecordedEditHint" : "C.Audio.SyntheticEditHint");
    public string[] VoiceLanguages { get; } = ["EN", "DE", "FR", "IT"];
    public string[] RadioQualities { get; } = ["Clean", "Aviation", "Rough"];
    public string[] RadioBeeps { get; } = ["None", "Click", "Beep", "Double beep", "Chirp"];
    public string TestText { get; set; } = "<actor1>Ready for the mission.";
    public int RecordedVolume
    {
        get => _recordedVolume;
        set
        {
            if (SetProperty(ref _recordedVolume, Math.Clamp(value, 0, 100)) && !_loading && !_disposed)
                _ = PushRecordedVolumeAsync();
        }
    }
    public int SyntheticVolume { get => _syntheticVolume; set => SetProperty(ref _syntheticVolume, Math.Clamp(value, 0, 100)); }
    public bool RecordedMasterMuted
    {
        get => _recordedMasterMuted;
        private set
        {
            if (!SetProperty(ref _recordedMasterMuted, value)) return;
            OnPropertyChanged(nameof(RecordedMuteLabel));
            if (!_loading && !_disposed) _ = PushRecordedVolumeAsync();
        }
    }
    public string RecordedMuteLabel => T(RecordedMasterMuted ? "C.Recorded.Unmute" : "C.Recorded.Mute");
    public string RecordedLastPlayed { get => _recordedLastPlayed; private set => SetProperty(ref _recordedLastPlayed, value); }
    public string RecordedDiagnostics { get; private set; } = string.Empty;
    public RecordedSoundViewModel? RecordedSound { get => _recordedSound; set { if (SetProperty(ref _recordedSound, value)) RefreshCommands(); } }
    private int _headsetLevel = 100, _externalLevel = 100, _notifyLevel = 100;
    public int HeadsetLevel { get => _headsetLevel; set => SetProperty(ref _headsetLevel, Math.Clamp(value, 0, 100)); }
    public int ExternalLevel { get => _externalLevel; set => SetProperty(ref _externalLevel, Math.Clamp(value, 0, 100)); }
    public int NotifyLevel { get => _notifyLevel; set => SetProperty(ref _notifyLevel, Math.Clamp(value, 0, 100)); }
    private bool _audioInhibit = true, _missionVolumes = true, _radioTail = true;
    private string _radioQuality = "Aviation", _radioBeep = "Beep", _variety = "random", _callSign = string.Empty;
    public bool AudioInhibit { get => _audioInhibit; set => SetProperty(ref _audioInhibit, value); }
    public bool MissionVolumes { get => _missionVolumes; set => SetProperty(ref _missionVolumes, value); }
    public string RadioQuality { get => _radioQuality; set => SetProperty(ref _radioQuality, value); }
    public string RadioBeep { get => _radioBeep; set => SetProperty(ref _radioBeep, value); }
    private int _radioNoise = 20, _beepLevel = 50;
    public int RadioNoise { get => _radioNoise; set => SetProperty(ref _radioNoise, Math.Clamp(value, 0, 100)); }
    public int BeepLevel { get => _beepLevel; set => SetProperty(ref _beepLevel, Math.Clamp(value, 0, 100)); }
    public bool RadioTail { get => _radioTail; set => SetProperty(ref _radioTail, value); }
    public string Variety { get => _variety; set => SetProperty(ref _variety, value); }
    public string RuntimeLog { get; private set; } = string.Empty;
    private string _diagnosticLevel = "all";
    public IReadOnlyList<CompanionChoice> DiagnosticLevels =>
        [new("all", T("C.Catalog.All")), new("warnings", T("C.Diagnostics.Warnings")), new("errors", T("C.Diagnostics.Errors"))];
    public string DiagnosticLevel
    {
        get => _diagnosticLevel;
        set
        {
            if (!SetProperty(ref _diagnosticLevel, value ?? "all")) return;
            OnPropertyChanged(nameof(DiagnosticText));
            ((AsyncRelayCommand)CopyRuntimeLogCommand).RaiseCanExecuteChanged();
        }
    }
    private string RawDiagnosticContent => string.Join(Environment.NewLine + Environment.NewLine,
        new[] { RecordedDiagnostics.Length > 0 ? "[WARN] " + RecordedDiagnostics : string.Empty, RuntimeLog }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string DiagnosticContent => DiagnosticLogFilter.Apply(RawDiagnosticContent, DiagnosticLevel);
    public string DiagnosticText => DiagnosticContent.Length > 0
        ? $"{AppEdition.ProductName} {Version}{Environment.NewLine}{AudioModeName}{Environment.NewLine}{Environment.NewLine}{DiagnosticContent}"
        : T(RawDiagnosticContent.Length == 0 ? "C.Diagnostics.Empty" : "C.Diagnostics.NoMatches");
    public string Version { get; } = AppEdition.Version;
    public string Section
    {
        get => _section;
        private set
        {
            if (!SetProperty(ref _section, value)) return;
            OnPropertiesChanged(nameof(SectionTitle), nameof(IsMissionPage), nameof(IsSoundPage), nameof(IsSoundArea), nameof(HasMainPage), nameof(IsOptionsPage));
            if (value is "Mission" or "Audio") { _settings.MainPage = value; _store.Save(_settings); }
        }
    }
    public string SectionTitle => T("C.Nav." + Section);
    public bool HasEntered => _entered;
    public bool IsDisposed => _disposed;
    public bool IsMissionPage => Section == "Mission";
    public bool IsSoundPage => Section == "Audio";
    public bool IsSoundArea => IsSoundPage || IsOptionsPage && _optionsOrigin == "Audio";
    public bool IsOptionsPage => Section == "Options";
    public bool HasMainPage => Section is "Mission" or "Audio" || IsOptionsPage && _optionsOrigin is "Mission" or "Audio";
    private string _optionsOrigin = "Audio";
    private string? _communityOrigin;
    private SimulatorType? _pendingSimulator;
    public bool CanCancelCommunity => _communityOrigin is not null;
    public ICommand CancelCommunityCommand { get; }

    private void CancelCommunity()
    {
        if (_communityOrigin is null) return;
        var origin = _communityOrigin;
        _communityOrigin = null;
        _pendingSimulator = null;
        OnPropertiesChanged(nameof(CanCancelCommunity), nameof(IsMsfs2020), nameof(IsMsfs2024), nameof(SimulatorName));
        Section = origin;
        RefreshCommands();
    }
    private Task ShowOptionsAsync(string page) => ExecuteAsync(async () =>
    {
        if (page is "Audio" or "Voices") await EnsureRuntimeAsync().ConfigureAwait(true);
        if (page == "Tools") EnsureWorkFolder();
        _optionsOrigin = page is "Audio" or "Voices" ? "Audio" : page == "Tools" ? "Mission" : IsOptionsPage ? _optionsOrigin : Section;
        OptionsRequested?.Invoke(page);
        Section = "Options";
        Status = T("C.Status.Ready");
    });
    public void ReturnFromOptions()
    {
        if (!IsBusy && TryLeaveAudioEdits()) Section = _optionsOrigin;
    }
    public string EngineVersion { get => _engineVersion; private set => SetProperty(ref _engineVersion, value); }
    public string ActiveMissionId { get => _activeMissionId; private set => SetProperty(ref _activeMissionId, value); }
    private bool _customCallSign;
    public bool CustomCallSign { get => _customCallSign; set => SetProperty(ref _customCallSign, value); }
    public string CallSign { get => _callSign; set => SetProperty(ref _callSign, value); }
    public string RecordedPackSummary => T("C.New.PacksFound", RecordedPacks.Count);
    public string SyntheticPackSummary => T("C.New.PacksFound", TextPacks.Count);
    public string CommunitySummary => T(HasCommunity ? "C.New.FolderReady" : "C.Path.SelectCommunity");
    public string H145ActionHint => _helicopter is { IsVersion500OrLater: true } ? T("C.New.H145LayoutOnly")
        : _helicopter is { Installed: true, ActionPackInstalled: false } ? T("C.New.H145BaseOnly") : T("C.New.H145Hint");
    public bool IsUpdatingH145 => _helicopterCancellation is not null;
    public string CommunityPath => _settings.CommunityPath;
    public string StoragePath => string.IsNullOrEmpty(_settings.AudioStoragePath) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RandomAndEverywhereCompanion", "Audio") : _settings.AudioStoragePath;
    public string WorkPath { get => _workPath; private set { if (SetProperty(ref _workPath, value)) { _settings.WorkFolderPath = value; _store.Save(_settings); OnPropertyChanged(nameof(WorkFolderDisplay)); RefreshCommands(); } } }
    public string WorkFolderDisplay => string.IsNullOrWhiteSpace(WorkPath) ? T("C.Tools.WorkFolderEmpty") : WorkPath;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public string StatusColor { get => _statusColor; private set => SetProperty(ref _statusColor, value); }
    public string Prerequisites { get => _prerequisites; private set => SetProperty(ref _prerequisites, value); }
    public string MissionVersion { get => _missionVersion; private set { if (SetProperty(ref _missionVersion, value)) OnPropertyChanged(nameof(MissionVersionColor)); } }
    public string MissionVersionColor => MissionVersion == T("C.Mission.Missing") || MissionVersion == T("C.Mission.Unknown") ? "#F3BF5B" : "#8ACBA6";
    public string InventorySummary { get => _inventorySummary; private set => SetProperty(ref _inventorySummary, value); }
    public double Progress { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(IsIndeterminate)); } }
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) { OnPropertiesChanged(nameof(CanEditAudio), nameof(CanAdjustRecordedVolume), nameof(ShowProgress), nameof(IsIndeterminate), nameof(AudioAvailabilityHint)); RefreshCommands(); } } }
    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (!SetProperty(ref _running, value)) return;
            if (!value) _simulatorConnected = false;
            OnPropertiesChanged(nameof(CanEditAudio), nameof(SimulatorConnectionStatus), nameof(SimulatorConnectionColor));
            RefreshCommands();
        }
    }
    public bool IsMsfs2020 { get => (_pendingSimulator ?? _settings.Simulator) == SimulatorType.Msfs2020; set { if (value) SetSimulator(SimulatorType.Msfs2020); } }
    public bool IsMsfs2024 { get => (_pendingSimulator ?? _settings.Simulator) == SimulatorType.Msfs2024; set { if (value) SetSimulator(SimulatorType.Msfs2024); } }
    public LanguageOptionViewModel? Language { get => _language; set { if (SetProperty(ref _language, value) && value is not null) { _localization.SetCulture(value.Culture); _settings.Culture = value.Culture; _store.Save(_settings); } } }
    public string VoiceLanguage
    {
        get => _voiceLanguage;
        set
        {
            if (!_loading && value != _voiceLanguage && !TryLeaveAudioEdits()) { OnPropertyChanged(nameof(VoiceLanguage)); return; }
            if (!SetProperty(ref _voiceLanguage, value)) return;
            if (!_loading) BuildActors();
            OnPropertiesChanged(nameof(HasMatchingVoiceLanguage), nameof(AudioAvailabilityHint));
        }
    }
    public CompanionChoice? RecordedPack
    {
        get => _recordedPack;
        set
        {
            if (!_loading && value != _recordedPack && !TryLeaveAudioEdits()) { OnPropertyChanged(nameof(RecordedPack)); return; }
            if (SetProperty(ref _recordedPack, value))
            {
                if (!_loading) BuildRecordedSounds();
                OnPropertyChanged(nameof(SelectedPackName)); RefreshCommands();
            }
        }
    }
    public CompanionChoice? TextPack { get => _textPack; set { if (SetProperty(ref _textPack, value)) { OnPropertyChanged(nameof(SelectedPackName)); RefreshCommands(); } } }
    public CompanionChoice? RecordedDevice { get => _recordedDevice; set => SetProperty(ref _recordedDevice, value); }
    public CompanionChoice? HeadsetDevice { get => _headsetDevice; set => SetProperty(ref _headsetDevice, value); }
    public CompanionChoice? ExternalDevice { get => _externalDevice; set => SetProperty(ref _externalDevice, value); }
    public CompanionChoice? CatalogVoice { get => _catalogVoice; set { SetProperty(ref _catalogVoice, value); RefreshCommands(); } }

    public ICommand NavigateCommand { get; }
    public ICommand ContinueCommand { get; }
    public ICommand BrowseCommunityCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand ToggleRecordedMuteCommand { get; }
    public ICommand RegisterRecordedCommand { get; }
    public ICommand ReloadRecordedCommand { get; }
    public ICommand ForgetRecordedCommand { get; }
    public ICommand PreviewRecordedSoundCommand { get; }
    public ICommand CopyRuntimeLogCommand { get; }
    public ICommand SaveAudioCommand { get; }
    public ICommand ImportPackCommand { get; }
    public ICommand CatalogCommand { get; }
    public ICommand InstallVoiceCommand { get; }
    public ICommand RecommendedCommand { get; }
    public ICommand ExportProfileCommand { get; }
    public ICommand ImportProfileCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand UpdateMissionCommand { get; }
    public ICommand MaintainCommand { get; }
    public ICommand BrowseWorkCommand { get; }
    public ICommand CleanDebugCommand { get; }
    public ICommand BrowseStorageCommand { get; }
    public ICommand ImportSimConnectCommand { get; }
    public ICommand OpenCommunityCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand CheckAppUpdateCommand { get; }
    public ICommand UseDetectedCommunityCommand { get; }
    public ICommand OpenOptionsCommand { get; }
    public ICommand OpenAudioOptionsCommand { get; }
    public ICommand OpenMaintenanceCommand { get; }
    public ICommand UpdateH145Command { get; }
    public ICommand CancelH145Command { get; }

    public async Task InitializeAsync()
    {
        if (_settings.WelcomeShown) Section = "Loading";
        await InitializeInventoryAsync().ConfigureAwait(true);
        if (_settings.WelcomeShown) await EnterCompanionAsync().ConfigureAwait(true);
    }

    private Task InitializeInventoryAsync() => ExecuteAsync(async () =>
    {
        if (!HasCommunity)
        {
            DetectedCommunities.Clear();
            foreach (var simulator in new[] { SimulatorType.Msfs2024, SimulatorType.Msfs2020 })
            {
                var path = await _engine.DetectCommunityPathAsync(simulator, CancellationToken.None).ConfigureAwait(true);
                if (path is not null && Directory.Exists(path) && !DetectedCommunities.Any(choice => choice.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    DetectedCommunities.Add(new CommunityChoice(simulator, path));
            }
            if (DetectedCommunities.Count == 1) SetCommunity(DetectedCommunities[0].Path, DetectedCommunities[0].Simulator);
            DetectedCommunity = DetectedCommunities.FirstOrDefault();
            OnPropertyChanged(nameof(HasDetectedCommunities));
        }
        await ReadInventoryAsync().ConfigureAwait(true);
    });

    private async Task EnterCompanionAsync()
    {
        if (!_settings.WelcomeShown)
        {
            _settings.WelcomeShown = true;
            try { _store.Save(_settings); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Acceptance is not complete until it is durable. Leave Continue retryable.
                _settings.WelcomeShown = false;
                Status = T("C.Status.Error"); Detail = exception.Message; StatusColor = "#F01B3B";
                return;
            }
        }
        _entered = true;
        OnPropertyChanged(nameof(HasEntered));
        Status = T("C.Status.Ready"); Detail = string.Empty; StatusColor = "#9EB5C7";
        await RouteAfterCommunityCheckAsync().ConfigureAwait(true);
    }

    private async Task RouteAfterCommunityCheckAsync()
    {
        if (!_inventoryReady) { Section = "Community"; return; }
        var target = CompanionStartupPolicy.Resolve(HasCommunity, InstalledPackages.Select(package => package.Result).ToArray());
        if (target == CompanionStartupDestination.SelectCommunity) { Section = "Community"; return; }
        if (target == CompanionStartupDestination.InstallPackages)
        {
            Section = "Mission";
            return;
        }
        if (_settings.MainPage == "Mission") Section = "Mission";
        else await ShowAudioAsync().ConfigureAwait(true);
    }

    private async Task ShowAudioAsync()
    {
        Section = "Audio";
        await ExecuteAsync(async () => { Status = T("C.Flow.LoadingAudio"); await EnsureRuntimeAsync().ConfigureAwait(true); Status = T("C.Status.Ready"); }).ConfigureAwait(true);
    }

    private void SetCommunity(string path, SimulatorType simulator)
    {
        _pendingSimulator = null;
        _communityOrigin = null;
        OnPropertyChanged(nameof(CanCancelCommunity));
        _settings.CommunityPath = path; _settings.Simulator = simulator;
        if (string.IsNullOrEmpty(_settings.AudioStoragePath))
            _settings.AudioStoragePath = Path.Combine(Directory.GetParent(path)?.FullName ?? path, ".RandomEverywhereCompanion", "Audio");
        _store.Save(_settings);
        OnPropertiesChanged(nameof(CommunityPath), nameof(StoragePath), nameof(HasCommunity), nameof(IsMsfs2020), nameof(IsMsfs2024), nameof(SimulatorName), nameof(CommunitySummary));
    }

    private async Task UseDetectedCommunityAsync()
    {
        if (DetectedCommunity is null) return;
        if (_runtime is not null) { await _runtime.DisposeAsync().ConfigureAwait(true); _runtime = null; }
        SetCommunity(DetectedCommunity.Path, DetectedCommunity.Simulator);
        await RefreshInventoryAsync().ConfigureAwait(true);
        await RouteAfterCommunityCheckAsync().ConfigureAwait(true);
    }
    private string T(string key, params object[] arguments) => _localization.GetText(key, arguments);
    private async Task ExecuteAsync(Func<Task> operation)
    {
        if (IsBusy) return;
        IsBusy = true; Progress = 0; Status = T("C.Status.Working"); StatusColor = "#72BEFF";
        try { await operation().ConfigureAwait(true); Progress = 100; if (Status == T("C.Status.Working")) Status = T("C.Status.Done"); if (StatusColor == "#72BEFF") StatusColor = "#38D985"; }
        catch (OperationCanceledException)
        {
            Status = T("Status.Cancelled"); Detail = string.Empty; StatusColor = "#72BEFF";
        }
        catch (Exception exception)
        {
            Status = T("C.Status.Error"); Detail = exception.Message; StatusColor = "#F01B3B";
            try { await File.AppendAllTextAsync(Path.Combine(_engine.LogDirectory, "companion.log"), DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " " + exception + Environment.NewLine).ConfigureAwait(true); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
        }
        finally { IsBusy = false; }
    }

    private async Task NavigateAsync(string? section)
    {
        if (section is null || !_entered) return;
        if (section is "Mission" or "Community" or "Recorded" or "Synthetic" && !TryLeaveAudioEdits()) return;
        if (section is "Recorded" or "Synthetic")
        {
            if (IsRunning) return;
            _settings.AudioMode = section == "Recorded" ? CompanionAudioMode.Recorded : CompanionAudioMode.Synthetic;
            _store.Save(_settings);
            OnPropertiesChanged(nameof(IsRecordedMode), nameof(IsSyntheticMode), nameof(CanAdjustRecordedVolume), nameof(AudioModeName), nameof(SelectedPackName), nameof(AudioAvailabilityHint), nameof(AudioEditsHint));
            RefreshCommands();
            Section = "Audio";
            await ExecuteAsync(async () =>
            {
                await EnsureRuntimeAsync().ConfigureAwait(true);
                ApplySnapshot(await _runtime!.RequestAsync("stop").ConfigureAwait(true), false);
                Status = T("C.Status.Ready");
            }).ConfigureAwait(true);
        }
        else if (section == "Community")
        {
            if (IsRunning || Section == "Community") return;
            _communityOrigin = Section;
            _pendingSimulator = null;
            OnPropertyChanged(nameof(CanCancelCommunity));
            Section = "Community";
            RefreshCommands();
        }
        else if (section == "Audio") await ShowAudioAsync().ConfigureAwait(true);
        else if (section == "Mission") Section = "Mission";
    }

    private void EnsureWorkFolder()
    {
        if (string.IsNullOrEmpty(WorkPath))
        {
            var workFolders = _findWorkFolders();
            WorkPath = workFolders.Count > 0 ? workFolders[0] : string.Empty;
        }
    }

    private async Task BrowseCommunityAsync()
    {
        var selected = _folders.PickFolder(CommunityPath, T("C.Path.Community"));
        if (selected is null) return;
        SetCommunity(selected, _pendingSimulator ?? _settings.Simulator);
        if (_runtime is not null) { await _runtime.DisposeAsync().ConfigureAwait(true); _runtime = null; }
        await RefreshInventoryAsync().ConfigureAwait(true);
        if (_entered) await RouteAfterCommunityCheckAsync().ConfigureAwait(true);
    }

    private Task RefreshInventoryAsync() => ExecuteAsync(ReadInventoryAsync);
    private async Task ReadInventoryAsync()
    {
        _inventoryReady = false;
        InstalledPackages.Clear();
        _helicopter = null;
        if (!Directory.Exists(CommunityPath)) { Prerequisites = T("C.Path.SelectCommunity"); MissionVersion = T("C.Mission.Missing"); InventorySummary = string.Empty; Status = T("C.Path.SelectCommunity"); OnPropertyChanged(nameof(H145ActionHint)); RefreshCommands(); return; }
        var inventory = await Task.Run(() => _engine.InspectCommunityPackages(CommunityPath, _settings.Simulator)).ConfigureAwait(true);
        foreach (var item in inventory) InstalledPackages.Add(new PackageStatusViewModel(item, T("PackageState." + item.State), T(item.Definition.Required ? "Package.Required" : "Package.Optional")));
        var h145 = _engine.DetectH145(CommunityPath);
        _helicopter = h145;
        OnPropertyChanged(nameof(H145ActionHint));
        Prerequisites = h145.Installed ? T("C.Prerequisites.Version", h145.Version?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?", T(h145.ActionPackInstalled ? "C.Prerequisites.Ready" : "C.Prerequisites.ActionMissing")) : T("C.Prerequisites.H145Missing");
        var mission = _engine.DetectAndrewPackage(CommunityPath);
        MissionVersion = mission.CumulativeTargetPresent ? _engine.InspectAndrewUpdate(mission.CumulativeTargetPath).Version?.Display ?? T("C.Mission.Unknown") : T(mission.Installed ? "C.Mission.Unknown" : "C.Mission.Missing");
        InventorySummary = T("Packages.Summary", inventory.Count(p => p.Definition.Required && p.RequirementSatisfied), inventory.Count(p => p.Definition.Required), inventory.Count(p => !p.Definition.Required && p.RequirementSatisfied), inventory.Count(p => !p.Definition.Required));
        _inventoryReady = true;
        Status = T("C.Status.Ready"); Detail = CommunityPath;
        OnPropertyChanged(nameof(HasCommunity)); RefreshCommands();
    }

    private async Task EnsureRuntimeAsync()
    {
        if (_runtime is { IsStarted: true }) return;
        _runtime ??= new CompanionRuntimeClient(StoragePath);
        var state = await _runtime.RequestAsync("scan", new { CommunityPath }).ConfigureAwait(true);
        ApplySnapshot(state, true);
    }

    private object Configuration(string? mode = null) => new
    {
        Mode = mode ?? (IsRecordedMode ? "recorded" : "synthetic"),
        WorldDataRevision = WorldRevisionForSession(),
        RecordedVolume,
        RecordedMasterMuted,
        SyntheticVolume,
        RecordedMuted = RecordedSounds.Where(s => !s.Enabled).Select(s => s.Variable).ToArray(),
        RecordedDevice = RecordedDevice?.Id ?? "",
        RecordedPack = RecordedPack?.Id ?? "",
        HeadsetDevice = HeadsetDevice?.Id ?? "",
        ExternalDevice = ExternalDevice?.Id ?? "",
        Language = VoiceLanguage,
        TextPack = TextPack?.Id ?? "",
        AudioInhibit,
        MissionVolumes,
        HeadsetLevel,
        ExternalLevel,
        NotifyLevel,
        RadioQuality,
        RadioBeep,
        RadioNoise,
        BeepLevel,
        RadioTail,
        Variety,
        CallSignMode = CustomCallSign ? "custom" : "airline",
        CallSign
    };

    // Coalesce live level changes without stopping playback or locking the editor.
    private async Task PushRecordedVolumeAsync()
    {
        if (_recordedVolumePushInProgress || _runtime is not { IsStarted: true }) return;
        var runtime = _runtime;
        _recordedVolumePushInProgress = true;
        try
        {
            while (!_disposed && ReferenceEquals(runtime, _runtime) && runtime.IsStarted)
            {
                var volume = RecordedVolume;
                var muted = RecordedMasterMuted;
                _ = await runtime.RequestAsync("recorded-volume", new { Volume = volume, Muted = muted }).ConfigureAwait(true);
                await Task.Delay(100).ConfigureAwait(true);
                if (volume == RecordedVolume && muted == RecordedMasterMuted) break;
            }
        }
        catch (Exception exception)
        {
            if (!_disposed && ReferenceEquals(runtime, _runtime))
            {
                Status = T("C.Status.Error"); Detail = exception.Message; StatusColor = "#F01B3B";
            }
        }
        finally { _recordedVolumePushInProgress = false; }
    }

    private Task SaveAudioAsync() => ExecuteAsync(async () =>
    {
        await EnsureRuntimeAsync().ConfigureAwait(true);
        if (IsSyntheticMode)
            _ = await _runtime!.RequestAsync("actors", new { Language = VoiceLanguage, Actors = Actors.Select(a => new { a.Actor, Voice = a.Voice?.Id ?? "", a.Speed, a.Random, a.Pool }).ToArray() }).ConfigureAwait(true);
        ApplySnapshot(await _runtime!.RequestAsync("configure", Configuration()).ConfigureAwait(true), false);
        _store.Save(_settings);
        CaptureAudioDraft();
    });
    private Task StartAudioAsync() => ExecuteAsync(async () =>
    {
        if (!Directory.Exists(CommunityPath) || !_engine.DetectAndrewPackage(CommunityPath).Installed)
            throw new InvalidOperationException(T("C.Mission.Missing"));
        await EnsureRuntimeAsync().ConfigureAwait(true);
        _ = await _runtime!.RequestAsync("configure", Configuration()).ConfigureAwait(true);
        ApplySnapshot(await _runtime.RequestAsync("start").ConfigureAwait(true), false);
        _poll.Start(); Status = SimulatorConnectionStatus; StatusColor = SimulatorConnectionColor;
    });
    private Task StopAudioAsync() => ExecuteAsync(async () =>
    {
        if (_runtime is { IsStarted: true }) ApplySnapshot(await _runtime.RequestAsync("stop").ConfigureAwait(true), false);
        _poll.Stop(); IsRunning = false; Status = T("C.Audio.Stopped");
    });
    private Task RuntimeActionAsync(string action, object? payload = null) => ExecuteAsync(async () =>
    {
        await EnsureRuntimeAsync().ConfigureAwait(true);
        if (action is "test" or "recorded-preview" && !IsRunning) _ = await _runtime!.RequestAsync("configure", Configuration()).ConfigureAwait(true);
        ApplySnapshot(await _runtime!.RequestAsync(action, payload, new Progress<RuntimeProgress>(p => { Progress = p.Percent; Detail = p.Message; })).ConfigureAwait(true), false);
        if (action is "test" or "recorded-preview") _poll.Start();
    });

    private async Task PollAsync()
    {
        if (_polling || IsBusy || _runtime is null || _disposed) return;
        _polling = true;
        try
        {
            ApplySnapshot(await _runtime.RequestAsync("state").ConfigureAwait(true), false);
            if (IsRunning) { Status = SimulatorConnectionStatus; StatusColor = SimulatorConnectionColor; }
        }
        catch (Exception exception) { _poll.Stop(); IsRunning = false; Status = T("C.Status.Error"); Detail = SimulatorLinkDetail = exception.Message; StatusColor = "#F01B3B"; }
        finally { _polling = false; }
    }

    private void ApplySnapshot(JsonElement snapshot, bool full)
    {
        if (snapshot.TryGetProperty(nameof(RecordedPacks), out _)) _snapshot = snapshot;
        if (snapshot.TryGetProperty(nameof(EngineVersion), out var engine)) EngineVersion = engine.GetString() ?? string.Empty;
        if (snapshot.TryGetProperty("MissionId", out var missionId)) ActiveMissionId = missionId.GetString() ?? string.Empty;
        IsRunning = snapshot.GetProperty("Running").GetBoolean();
        // A running audio worker may still be waiting for the simulator.
        var connected = IsRunning && snapshot.TryGetProperty("SimConnected", out var simConnected) && simConnected.ValueKind == JsonValueKind.True;
        if (SetProperty(ref _simulatorConnected, connected, nameof(SimulatorConnectionStatus)))
            OnPropertyChanged(nameof(SimulatorConnectionColor));
        Detail = SimulatorLinkDetail = snapshot.GetProperty("SimStatus").GetString() ?? string.Empty;
        RuntimeLog = string.Join(Environment.NewLine, snapshot.GetProperty("Logs").EnumerateArray().Select(p => p.GetString())); OnPropertiesChanged(nameof(RuntimeLog), nameof(DiagnosticText));
        RecordedLastPlayed = snapshot.TryGetProperty(nameof(RecordedLastPlayed), out var lastPlayed) ? lastPlayed.GetString() ?? string.Empty : string.Empty;
        ((AsyncRelayCommand)CopyRuntimeLogCommand).RaiseCanExecuteChanged();
        if (!full) return;
        _loading = true;
        try
        {
            Devices.Clear(); foreach (var device in snapshot.GetProperty(nameof(Devices)).EnumerateArray()) Devices.Add(new CompanionChoice(device.GetString() ?? "", string.IsNullOrEmpty(device.GetString()) ? T("C.Audio.DefaultDevice") : device.GetString()!));
            Voices.Clear(); Voices.Add(new CompanionChoice("", T("C.Voice.Automatic")));
            foreach (var voice in snapshot.GetProperty(nameof(Voices)).EnumerateArray()) Voices.Add(new CompanionChoice(voice.GetProperty("Id").GetString()!, voice.GetProperty("Display").GetString()!, Family: voice.TryGetProperty("Family", out var family) ? family.GetString() ?? string.Empty : string.Empty));
            RecordedPacks.Clear(); foreach (var pack in snapshot.GetProperty(nameof(RecordedPacks)).EnumerateArray()) RecordedPacks.Add(new CompanionChoice(pack.GetProperty("File").GetString()!, pack.GetProperty("Name").GetString()!, T("C.Audio.MappingCount", pack.GetProperty("Mappings").GetInt32()) + " · " + string.Join("; ", pack.GetProperty("Warnings").EnumerateArray().Select(p => p.GetString()))));
            TextPacks.Clear();
            foreach (var pack in snapshot.GetProperty(nameof(TextPacks)).EnumerateArray())
            {
                var name = pack.GetProperty("Name").GetString();
                var version = pack.GetProperty(nameof(Version)).GetInt32();
                var usable = pack.TryGetProperty("Usable", out var available) ? available.GetBoolean() : !string.IsNullOrWhiteSpace(name) && version > 0;
                if (usable) TextPacks.Add(new CompanionChoice(name!, name + " v" + version, pack.GetProperty("Check").GetString() ?? string.Empty));
            }
            var preferences = snapshot.GetProperty("Preferences"); var settings = snapshot.GetProperty("Settings");
            var selectedPath = preferences.GetProperty(nameof(RecordedPack)).GetString();
            RecordedPack = RecordedPacks.FirstOrDefault(p => p.Id.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
            BuildRecordedSounds();
            RecordedDevice = Devices.FirstOrDefault(p => p.Id == preferences.GetProperty(nameof(RecordedDevice)).GetString()) ?? Devices.First();
            // The worker also knows the mission-declared pack; show its effective choice.
            // Keep older snapshots deterministic instead of selecting alphabetical DE.
            var effectivePack = snapshot.TryGetProperty("EffectiveTextPack", out var effective) ? effective.GetString() : null;
            TextPack = FindTextPack(effectivePack)
                ?? FindTextPack(settings.GetProperty("BridgePack").GetString())
                ?? FindTextPack(settings.GetProperty("DefaultLanguage").GetString())
                ?? FindTextPack("EN")
                ?? TextPacks.FirstOrDefault();
            var audio = settings.GetProperty("AudioOut"); var radio = settings.GetProperty("Radio");
            HeadsetDevice = Devices.FirstOrDefault(p => p.Id == audio.GetProperty(nameof(HeadsetDevice)).GetString()) ?? Devices.First();
            ExternalDevice = Devices.FirstOrDefault(p => p.Id == audio.GetProperty(nameof(ExternalDevice)).GetString()) ?? Devices.First();
            RecordedVolume = preferences.GetProperty(nameof(RecordedVolume)).GetInt32(); SyntheticVolume = settings.GetProperty("MasterVolume").GetInt32();
            RecordedMasterMuted = preferences.TryGetProperty(nameof(RecordedMasterMuted), out var masterMuted) && masterMuted.GetBoolean();
            HeadsetLevel = audio.GetProperty(nameof(HeadsetLevel)).GetInt32(); ExternalLevel = audio.GetProperty(nameof(ExternalLevel)).GetInt32(); NotifyLevel = audio.GetProperty(nameof(NotifyLevel)).GetInt32();
            VoiceLanguage = settings.GetProperty("DefaultLanguage").GetString() ?? "EN";
            AudioInhibit = settings.GetProperty("AutoAudioInhibit").GetBoolean(); MissionVolumes = settings.GetProperty("UseMissionVolumes").GetBoolean();
            RadioQuality = radio.GetProperty("Quality").GetString()!; RadioBeep = radio.GetProperty("Beep").GetString()!; RadioNoise = radio.GetProperty("NoiseLevel").GetInt32(); BeepLevel = radio.GetProperty(nameof(BeepLevel)).GetInt32(); RadioTail = radio.GetProperty("Tail").GetBoolean();
            Variety = settings.GetProperty(nameof(Variety)).GetString()!;
            CustomCallSign = settings.GetProperty("CallSignMode").GetString() == "custom";
            CallSign = settings.GetProperty(nameof(CallSign)).GetString() ?? string.Empty;
            OnPropertiesChanged(nameof(CustomCallSign), nameof(CallSign), nameof(RecordedPackSummary), nameof(SyntheticPackSummary));
            foreach (var name in new[] { nameof(RecordedVolume), nameof(SyntheticVolume), nameof(HeadsetLevel), nameof(ExternalLevel), nameof(NotifyLevel), nameof(AudioInhibit), nameof(MissionVolumes), nameof(RadioQuality), nameof(RadioBeep), nameof(RadioNoise), nameof(BeepLevel), nameof(RadioTail), nameof(Variety) }) OnPropertyChanged(name);
            BuildActors();
            OnPropertiesChanged(nameof(AudioAvailabilityHint), nameof(HasMatchingVoiceLanguage), nameof(AudioEditsHint));
            RefreshCommands();
        }
        finally { _loading = false; }
        CaptureAudioDraft();
    }

    private CompanionChoice? FindTextPack(string? name) => TextPacks.FirstOrDefault(pack => pack.Id.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void BuildActors()
    {
        if (_snapshot.ValueKind != JsonValueKind.Object) return;
        Actors.Clear();
        var names = _snapshot.GetProperty(nameof(Actors)).EnumerateArray().ToArray();
        var settings = _snapshot.GetProperty("Settings").GetProperty(nameof(Languages));
        if (!settings.TryGetProperty(VoiceLanguage, out var language)) return;
        var rows = language.GetProperty(nameof(Actors)).EnumerateArray().ToArray();
        for (var index = 0; index < names.Length; index++)
        {
            var row = new ActorVoiceViewModel(index + 1, T("C.Actor." + (index + 1))) { Voice = Voices.FirstOrDefault(v => v.Id == rows[index].GetProperty("Voice").GetString()) ?? Voices.First(), Speed = rows[index].GetProperty("Speed").GetDouble() };
            row.Random = rows[index].TryGetProperty("Random", out var random) && random.GetBoolean();
            if (rows[index].TryGetProperty("Pool", out var pool) && pool.ValueKind == JsonValueKind.Array)
                row.Pool = pool.EnumerateArray().Select(item => item.GetString()!).ToArray();
            Actors.Add(row);
        }
    }
    private void BuildRecordedSounds()
    {
        var selectedId = RecordedSound?.Id;
        RecordedSounds.Clear();
        RecordedSound = null;
        RecordedDiagnostics = string.Empty;
        OnPropertiesChanged(nameof(RecordedDiagnostics), nameof(DiagnosticText));
        if (_snapshot.ValueKind != JsonValueKind.Object) return;
        if (_snapshot.TryGetProperty("RecordedDiscoveryWarnings", out var warnings))
            RecordedDiagnostics = string.Join(Environment.NewLine, warnings.EnumerateArray().Select(p => p.GetString()));
        OnPropertiesChanged(nameof(RecordedDiagnostics), nameof(DiagnosticText));
        if (RecordedPack is null) return;
        var muted = _snapshot.GetProperty("Preferences").GetProperty("RecordedMuted").EnumerateArray().Select(p => p.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in _snapshot.GetProperty(nameof(RecordedPacks)).EnumerateArray())
        {
            if (pack.GetProperty("File").GetString() != RecordedPack.Id) continue;
            foreach (var sound in pack.GetProperty("Sounds").EnumerateArray())
            {
                var variable = sound.GetProperty("Variable").GetString()!;
                var variants = sound.GetProperty("Variants").GetInt32();
                var detail = T("C.Recorded.Variants", variants) + " · " + T("C.Recorded.Channel." + sound.GetProperty("Channel").GetString()) + Environment.NewLine
                    + sound.GetProperty("DeclaredFiles").GetString();
                var problems = string.Join(Environment.NewLine, sound.GetProperty("Problems").EnumerateArray().Select(p => p.GetString()));
                if (problems.Length > 0) detail += Environment.NewLine + problems;
                var row = new RecordedSoundViewModel(sound.GetProperty("Id").GetInt32(), variable, variants, !muted.Contains(variable), detail,
                    sound.GetProperty("Channel").GetString() ?? string.Empty, sound.GetProperty("DeclaredFiles").GetString() ?? string.Empty, problems);
                row.PropertyChanged += RecordedSoundChanged;
                RecordedSounds.Add(row);
            }
            RecordedSound = RecordedSounds.FirstOrDefault(s => s.Id == selectedId) ?? RecordedSounds.FirstOrDefault();
            break;
        }
    }

    private void RecordedSoundChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_updatingRecordedSounds || args.PropertyName != nameof(RecordedSoundViewModel.Enabled) || sender is not RecordedSoundViewModel changed) return;
        _updatingRecordedSounds = true;
        try
        {
            // Simulator muting is per variable, including every row sharing that counter.
            foreach (var row in RecordedSounds.Where(s => s.Variable.Equals(changed.Variable, StringComparison.OrdinalIgnoreCase))) row.Enabled = changed.Enabled;
        }
        finally { _updatingRecordedSounds = false; }
    }

    // Registration stores paths, never copies or removes pack content.
    public Task RegisterRecordedPathsAsync(IEnumerable<string> paths)
    {
        if (!CanEditAudio || !IsRecordedMode) return Task.CompletedTask;
        return RecordedLibraryActionAsync("recorded-register", new { Paths = paths.ToArray() });
    }

    private async Task RegisterRecordedAsync()
    {
        var picker = new OpenFileDialog { Filter = "Recorded SoundList (*.xml)|*.xml", Multiselect = true, Title = T("C.Recorded.Add"), InitialDirectory = FilePickerLocations.Shared.InitialDirectory("recorded", RecordedPack?.Id) };
        if (picker.ShowDialog() == true)
        {
            FilePickerLocations.Shared.RememberFile("recorded", picker.FileName);
            await RegisterRecordedPathsAsync(picker.FileNames).ConfigureAwait(true);
        }
    }

    private async Task ForgetRecordedAsync()
    {
        if (RecordedPack is null || !_dialogs.Confirm(AppEdition.ProductName, T("C.Recorded.RemoveConfirm", RecordedPack.Name))) return;
        await RecordedLibraryActionAsync("recorded-forget", new { Path = RecordedPack.Id }).ConfigureAwait(true);
    }

    private Task RecordedLibraryActionAsync(string command, object? payload = null) => !TryLeaveAudioEdits() ? Task.CompletedTask : ExecuteAsync(async () =>
    {
        await EnsureRuntimeAsync().ConfigureAwait(true);
        _ = await _runtime!.RequestAsync("configure", Configuration()).ConfigureAwait(true);
        ApplySnapshot(await _runtime.RequestAsync(command, payload).ConfigureAwait(true), true);
        Status = T("C.Recorded.LibraryUpdated");
        Detail = RecordedDiagnostics;
    });

    public Task ImportTextPackAsync(string path) => ExecuteAsync(async () =>
    {
        await EnsureRuntimeAsync().ConfigureAwait(true);
        var replace = _dialogs.Confirm(AppEdition.ProductName, T("C.Pack.ImportConfirm", Path.GetFileName(path)));
        if (!replace) return;
        ApplySnapshot(await _runtime!.RequestAsync("import-pack", new { Path = path, Replace = true }).ConfigureAwait(true), true);
    });
    private async Task ImportPackAsync()
    {
        if (!TryLeaveAudioEdits()) return;
        var picker = new OpenFileDialog { Filter = "Voice pack (*.xml)|*.xml", Title = T("C.Pack.Import"), InitialDirectory = FilePickerLocations.Shared.InitialDirectory("synthetic") };
        if (picker.ShowDialog() == true)
        {
            FilePickerLocations.Shared.RememberFile("synthetic", picker.FileName);
            await ImportTextPackAsync(picker.FileName).ConfigureAwait(true);
        }
    }
    private Task LoadCatalogAsync() => ExecuteAsync(async () =>
    {
        await EnsureRuntimeAsync().ConfigureAwait(true);
        var catalog = await _runtime!.RequestAsync("catalog").ConfigureAwait(true);
        CatalogVoices.Clear();
        foreach (var item in catalog.EnumerateArray()) CatalogVoices.Add(new CompanionChoice(item.GetProperty("Key").GetString()!, item.GetProperty("LangCode").GetString() + " · " + item.GetProperty("Name").GetString() + " · " + item.GetProperty("Quality").GetString(), (item.GetProperty("OnnxSize").GetInt64() / 1048576) + " MB · " + item.GetProperty("Source").GetString() + " · " + item.GetProperty("License").GetString() + " · " + item.GetProperty("Note").GetString(), item.GetProperty("LangCode").GetString() ?? string.Empty, item.GetProperty("Quality").GetString() ?? string.Empty, item.GetProperty("Installed").GetBoolean()));
        CatalogLanguages.Clear(); CatalogLanguages.Add(string.Empty);
        foreach (var language in CatalogVoices.Select(voice => voice.Family).Distinct().Order()) CatalogLanguages.Add(language);
        CatalogQualities.Clear(); CatalogQualities.Add(string.Empty);
        foreach (var quality in CatalogVoices.Select(voice => voice.Quality).Distinct().Order()) CatalogQualities.Add(quality);
        CatalogLanguage = string.Empty; CatalogQuality = string.Empty;
        CatalogVoice = FilteredCatalogVoices.Cast<CompanionChoice>().FirstOrDefault();
    });
    private async Task InstallVoiceAsync()
    {
        if (!TryLeaveAudioEdits()) return;
        if (CatalogVoice is null || !_dialogs.Confirm(AppEdition.ProductName, T("C.Voice.DownloadConfirm", CatalogVoice.Name, CatalogVoice.Detail))) return;
        var selected = CatalogVoice;
        await ExecuteAsync(async () =>
        {
            var state = await _runtime!.RequestAsync("install-voice", new { Key = selected.Id }, new Progress<RuntimeProgress>(p => { Progress = p.Percent; Detail = p.Message; })).ConfigureAwait(true);
            ApplySnapshot(state, true);
            var index = CatalogVoices.IndexOf(selected);
            if (index >= 0) CatalogVoices[index] = selected with { Installed = true };
            RefreshCatalogFilter();
        }).ConfigureAwait(true);
    }
    private async Task ApplyRecommendedAsync()
    {
        if (!TryLeaveAudioEdits()) return;
        long bytes = 0;
        var ready = false;
        await ExecuteAsync(async () =>
        {
            await EnsureRuntimeAsync().ConfigureAwait(true);
            var plan = await _runtime!.RequestAsync("profile-plan").ConfigureAwait(true);
            bytes = plan.GetProperty("Bytes").GetInt64();
            var drive = new DriveInfo(Path.GetPathRoot(StoragePath)!);
            if (drive.AvailableFreeSpace < bytes * 2 + 128L * 1024 * 1024) throw new IOException(T("C.Storage.Insufficient"));
            ready = true;
        }).ConfigureAwait(true);
        if (!ready) return;
        if (!_dialogs.Confirm(AppEdition.ProductName, T("C.Profile.DownloadConfirm", (bytes + 1048575) / 1048576))) return;
        await ExecuteAsync(async () => ApplySnapshot(await _runtime!.RequestAsync("profile-install", progress: new Progress<RuntimeProgress>(p => { Progress = p.Percent; Detail = p.Message; })).ConfigureAwait(true), true)).ConfigureAwait(true);
    }
    private async Task ExportProfileAsync()
    {
        var picker = new SaveFileDialog { Filter = "Settings profile (*.json)|*.json", FileName = "RE-voice-profile.json", Title = T("C.Profile.Export"), InitialDirectory = FilePickerLocations.Shared.InitialDirectory("profiles") };
        if (picker.ShowDialog() != true) return;
        FilePickerLocations.Shared.RememberFile("profiles", picker.FileName);
        await ExecuteAsync(async () => { await EnsureRuntimeAsync().ConfigureAwait(true); var profile = await _runtime!.RequestAsync("profile-export").ConfigureAwait(true); await File.WriteAllTextAsync(picker.FileName, profile.GetString()!).ConfigureAwait(true); }).ConfigureAwait(true);
    }
    private async Task ImportProfileAsync()
    {
        if (!TryLeaveAudioEdits()) return;
        var picker = new OpenFileDialog { Filter = "Settings profile (*.json)|*.json", Title = T("C.Profile.Import"), InitialDirectory = FilePickerLocations.Shared.InitialDirectory("profiles") };
        if (picker.ShowDialog() != true || !_dialogs.Confirm(AppEdition.ProductName, T("C.Profile.ImportConfirm"))) return;
        FilePickerLocations.Shared.RememberFile("profiles", picker.FileName);
        await ExecuteAsync(async () => { await EnsureRuntimeAsync().ConfigureAwait(true); ApplySnapshot(await _runtime!.RequestAsync("profile-import", new { Json = await File.ReadAllTextAsync(picker.FileName).ConfigureAwait(true) }).ConfigureAwait(true), true); }).ConfigureAwait(true);
    }

    private async Task OpenInstallerAsync(InstallerMode mode)
    {
        if (InstallerRequested is not null) await InstallerRequested(mode).ConfigureAwait(true);
        if (_disposed) return;
        await RefreshInventoryAsync().ConfigureAwait(true);
        if (!_inventoryReady) { Section = "Community"; return; }
        if (_runtime is { IsStarted: true }) await ExecuteAsync(async () => ApplySnapshot(await _runtime.RequestAsync("scan", new { CommunityPath }).ConfigureAwait(true), true)).ConfigureAwait(true);
        Section = HasCommunity ? "Mission" : "Community";
    }
    private async Task MaintainAsync()
    {
        if (!_dialogs.Confirm(AppEdition.ProductName, T("C.Tools.MaintainConfirm", CommunityPath)))
        {
            Status = T("Status.Cancelled");
            return;
        }
        await ExecuteAsync(async () =>
        {
            var progress = new Progress<InstallerProgress>(p => { Progress = p.OverallPercent; Detail = p.Detail; });
            var count = await _engine.RefreshMissionFilesAsync(CommunityPath, progress, CancellationToken.None).ConfigureAwait(true);
            Status = T("C.Tools.Refreshed", count);
        }).ConfigureAwait(true);
    }
    private async Task UpdateH145Async()
    {
        var current = _engine.DetectH145(CommunityPath);
        if (!current.Installed) return;
        var sources = InstallerEngine.GetH145UpdateSourceUrls(current);
        var operation = current.IsVersion500OrLater ? T("C.New.H145LayoutOnly") :
            current.ActionPackInstalled ? T("C.New.H145Both") : T("C.New.H145BaseOnly");
        if (!_dialogs.Confirm(AppEdition.ProductName, T("C.New.H145Confirm", operation, CommunityPath, string.Join(Environment.NewLine, sources))))
        { Status = T("Status.Cancelled"); return; }
        await ExecuteAsync(async () =>
        {
        using var cancel = new CancellationTokenSource();
        _helicopterCancellation = cancel;
        OnPropertyChanged(nameof(IsUpdatingH145)); RefreshCommands();
        try
        {
            // Release runtime handles before touching aircraft package files.
            if (_runtime is not null) { await _runtime.DisposeAsync().ConfigureAwait(true); _runtime = null; _poll.Stop(); }
            var progress = new Progress<InstallerProgress>(p => { Progress = p.OverallPercent; Detail = p.Detail; });
            _ = await Task.Run(() => _engine.UpdateH145Async(CommunityPath, progress, cancel.Token)).ConfigureAwait(true);
            await ReadInventoryAsync().ConfigureAwait(true);
            Status = T("C.New.H145Done");
        }
        finally { _helicopterCancellation = null; OnPropertyChanged(nameof(IsUpdatingH145)); RefreshCommands(); }
        }).ConfigureAwait(true);
    }

    private void BrowseWork() { var path = _folders.PickFolder(WorkPath, T("C.Tools.WorkFolder")); if (path is not null) WorkPath = path; }
    private async Task CleanDebugAsync()
    {
        if (!_dialogs.Confirm(AppEdition.ProductName, T("C.Tools.CleanConfirm"))) return;
        await ExecuteAsync(async () =>
        {
            var parent = Directory.GetParent(WorkPath)?.FullName ?? WorkPath;
            var result = await Task.Run(() => CompanionWorkFolderService.CleanupDebug(WorkPath, Path.Combine(parent, ".RandomEverywhereCompanion", "WorkBackups"))).ConfigureAwait(true);
            Status = T("C.Tools.Cleaned", result.DeletedFiles); Detail = result.BackupPath ?? string.Empty;
        }).ConfigureAwait(true);
    }
    private async Task BrowseStorageAsync()
    {
        var path = _folders.PickFolder(StoragePath, T("C.Storage.Title"));
        if (path is null) return;
        if (_runtime is not null) { await _runtime.DisposeAsync().ConfigureAwait(true); _runtime = null; }
        _settings.AudioStoragePath = Path.Combine(path, "RandomEverywhereCompanionAudio"); _store.Save(_settings); OnPropertyChanged(nameof(StoragePath));
        Status = T("C.Storage.Changed");
    }
    private async Task ImportSimConnectAsync()
    {
        var picker = new OpenFileDialog { Filter = "SimConnect.dll|SimConnect.dll", Title = T("C.Settings.SimConnect"), InitialDirectory = FilePickerLocations.Shared.InitialDirectory("simconnect", StoragePath) };
        if (picker.ShowDialog() != true) return;
        FilePickerLocations.Shared.RememberFile("simconnect", picker.FileName);
        await ExecuteAsync(async () =>
        {
            if (!Path.GetFileName(picker.FileName).Equals("SimConnect.dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Select SimConnect.dll from the MSFS SDK.");
            Directory.CreateDirectory(StoragePath);
            var target = Path.Combine(StoragePath, "SimConnect.dll");
            if (Path.GetFullPath(picker.FileName).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
            await using var input = File.OpenRead(picker.FileName);
            var temporary = target + ".new";
            await using (var output = File.Create(temporary)) await input.CopyToAsync(output).ConfigureAwait(true);
            File.Move(temporary, target, overwrite: true);
        }).ConfigureAwait(true);
    }
    private void SetSimulator(SimulatorType simulator)
    {
        if (IsBusy || IsRunning || (_pendingSimulator ?? _settings.Simulator) == simulator) return;
        // Simulator choice is provisional until a Community folder is accepted.
        _pendingSimulator = simulator;
        OnPropertiesChanged(nameof(IsMsfs2020), nameof(IsMsfs2024), nameof(SimulatorName));
        RefreshCommands();
    }
    private static void OpenFolder(string path) { if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private Task CheckAppUpdateAsync() => ExecuteAsync(async () =>
    {
        void Mirror(object? sender, PropertyChangedEventArgs args)
        {
            Status = _installer.StatusMessage; Detail = _installer.StatusDetail;
            Progress = _installer.ProgressValue; StatusColor = _installer.StatusMessageColor;
        }
        _installer.PropertyChanged += Mirror;
        try { await _installer.CheckForUpdatesAsync().ConfigureAwait(true); }
        finally { _installer.PropertyChanged -= Mirror; Mirror(null, new PropertyChangedEventArgs(null)); }
    });
    private void CultureChanged(object? sender, EventArgs args)
    {
        L.Refresh(); OnPropertyChanged(nameof(SectionTitle));
        RefreshMissionUpdatePresentation();
        RelocalizeWorld();
        OnPropertyChanged(nameof(RecordedMuteLabel));
        OnPropertiesChanged(nameof(RecordedPackSummary), nameof(SyntheticPackSummary), nameof(CommunitySummary), nameof(H145ActionHint));
        foreach (var actor in Actors) actor.Name = T("C.Actor." + actor.Actor);
        // Relocalize existing objects; rebuilding a snapshot would discard pending edits.
        foreach (var device in Devices.Where(item => item.Id.Length == 0)) device.RefreshText(T("C.Audio.DefaultDevice"));
        foreach (var voice in Voices.Where(item => item.Id.Length == 0)) voice.RefreshText(T("C.Voice.Automatic"));
        foreach (var sound in RecordedSounds) sound.RefreshText(_localization);
        if (_snapshot.ValueKind == JsonValueKind.Object && _snapshot.TryGetProperty(nameof(RecordedPacks), out var packs))
        {
            foreach (var pack in packs.EnumerateArray())
            {
                var item = RecordedPacks.FirstOrDefault(candidate => candidate.Id == pack.GetProperty("File").GetString());
                item?.RefreshText(item.Name, T("C.Audio.MappingCount", pack.GetProperty("Mappings").GetInt32()) + " · " +
                    string.Join("; ", pack.GetProperty("Warnings").EnumerateArray().Select(warning => warning.GetString())));
            }
        }
        OnPropertiesChanged(nameof(AudioEditsHint), nameof(AudioAvailabilityHint), nameof(DiagnosticText), nameof(DiagnosticLevels), nameof(AudioEditsStatus), nameof(WorkFolderDisplay));
        OnPropertiesChanged(nameof(SimulatorConnectionStatus), nameof(ConnectionActionHint), nameof(AudioModeName), nameof(SelectedPackName));
        if (!IsBusy && !IsRunning) Status = T("C.Status.Ready");
        for (var index = 0; index < InstalledPackages.Count; index++)
        {
            var item = InstalledPackages[index];
            InstalledPackages[index] = new PackageStatusViewModel(item.Result, T("PackageState." + item.Result.State), T(item.Result.Definition.Required ? "Package.Required" : "Package.Optional"));
        }
        if (!IsBusy && !IsRunning) _ = RefreshInventoryAsync();
    }
    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(ConnectionActionHint));
        foreach (var command in GetType().GetProperties().Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType)).Select(p => p.GetValue(this)))
        {
            if (command is AsyncRelayCommand asyncCommand) asyncCommand.RaiseCanExecuteChanged();
            if (command is RelayCommand syncCommand) syncCommand.RaiseCanExecuteChanged();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _poll.Stop(); _localization.CultureChanged -= CultureChanged;
        _missionCancellation?.Cancel();
        if (_ownsMissionUpdates && _missionUpdates is IDisposable missionService) missionService.Dispose();
        if (_runtime is not null) await _runtime.DisposeAsync().ConfigureAwait(false);
    }
}
