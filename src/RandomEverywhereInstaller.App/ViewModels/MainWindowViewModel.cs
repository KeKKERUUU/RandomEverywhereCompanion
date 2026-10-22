using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using RandomEverywhereInstaller.Infrastructure.Installer;
using System.Windows.Input;
using System.Windows.Threading;
using RandomEverywhereInstaller.App.Infrastructure;
using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Core.Localization;
using RandomEverywhereInstaller.Core.Models;
using RandomEverywhereInstaller.Core.Services;
using RandomEverywhereInstaller.Core.Updates;
using RandomEverywhereInstaller.Core.Wizard;
using RandomEverywhereInstaller.App.Services;

namespace RandomEverywhereInstaller.App.ViewModels;

/// <summary>
/// Coordinates localized wizard state and delegates all filesystem work to the engine.
/// </summary>
public sealed record InstallationReviewRow(string Package, string Destination, string Effect);

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService _localization;
    private readonly WizardState _wizard;
    private readonly IFolderPickerService _folderPicker;
    private readonly IUserDialogService _dialogs;
    private readonly IInstallerEngine _engine;
    private readonly bool _ownsEngine;
    private readonly IApplicationUpdateService _applicationUpdates;
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _elapsedTimer;
    private CancellationTokenSource? _operationCancellation;
    private IReadOnlyList<PackageScanResult> _scanResults = [];
    private IReadOnlyList<PackageScanResult> _communityInventory = [];
    private H145InstallationInfo? _h145;
    private AndrewPackageInfo? _andrewPackage;
    private AndrewUpdateInspection? _andrewUpdate;
    private LanguageOptionViewModel? _selectedLanguage;
    private string _communityPath = string.Empty;
    private SimulatorType _selectedSimulator = SimulatorType.Msfs2024;
    private string _statusMessage;
    private string _statusDetail;
    private string _statusMessageColor = "#F3F7FA";
    private string _elapsedTime = "00:00:00";
    private string _completedSummary = string.Empty;
    private double _progressValue;
    private bool _isBusy;
    private bool _allowMissingRequired;
    private bool _inputsScanned;
    private InstallerMode _installerMode = InstallerMode.FullInstallation;

    public MainWindowViewModel(
        ILocalizationService localization,
        WizardState wizard,
        IFolderPickerService folderPicker,
        IUserDialogService dialogs,
        IInstallerEngine engine,
        IApplicationUpdateService applicationUpdates,
        bool ownsEngine = true)
    {
        _localization = localization;
        _wizard = wizard;
        _folderPicker = folderPicker;
        _dialogs = dialogs;
        _engine = engine;
        _ownsEngine = ownsEngine;
        _applicationUpdates = applicationUpdates;
        _statusMessage = localization.GetText("Status.Ready");
        _statusDetail = localization.GetText("Status.Step.Disclaimer");

        Steps = [];
        RebuildWizardSteps();
        Languages = new ObservableCollection<LanguageOptionViewModel>
        {
            new("en-US", "English", "🇬🇧", "Recommended"),
            new("de-DE", "Deutsch", "🇩🇪", "Automatisch generierte Übersetzung"),
            new("fr-FR", "Français", "🇫🇷", "Traduction générée automatiquement"),
            new("es-ES", "Español", "🇪🇸", "Traducción generada automáticamente"),
            new("it-IT", "Italiano", "🇮🇹", "Traduzione generata automaticamente")
        };
        _selectedLanguage = Languages.First(option => option.Culture == localization.CurrentCulture);

        BackCommand = new RelayCommand(MoveBack, () => _wizard.CanMoveBack && !IsBusy);
        NextCommand = new AsyncRelayCommand(MoveNextAsync, () => CanMoveNext && !IsBusy);
        NavigateCommand = new RelayCommand<WizardStepItemViewModel>(NavigateTo);
        BrowseCommunityCommand = new RelayCommand(BrowseCommunityFolder, () => !IsBusy);
        ScanAgainCommand = new AsyncRelayCommand(async () => { _ = await ScanPackagesAsync().ConfigureAwait(true); }, () => !IsBusy && ZipArchives.Count > 0);
        StartInstallationCommand = new AsyncRelayCommand(InstallAsync, () => !IsBusy && CanStartInstallation);
        CancelCommand = new RelayCommand(CancelOperation, () => IsBusy);
        OpenLogCommand = new RelayCommand(_engine.OpenLogDirectory, () => !IsBusy);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsBusy);
        OpenDownloadCommand = new RelayCommand<PackageStatusViewModel>(OpenDownloadPage);
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty), () => !IsBusy);

        _wizard.Changed += OnWizardChanged;
        _localization.CultureChanged += OnCultureChanged;
        _elapsedTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateElapsedTime(), Dispatcher.CurrentDispatcher);
        RefreshWizardState();
    }

    public event EventHandler? ExitRequested;

    public ObservableCollection<WizardStepItemViewModel> Steps { get; }
    public ObservableCollection<LanguageOptionViewModel> Languages { get; }
    public ObservableCollection<PackageStatusViewModel> Packages { get; } = [];
    public ObservableCollection<PackageStatusViewModel> CommunityPackages { get; } = [];
    public ObservableCollection<ZipArchivePathViewModel> ZipArchives { get; } = [];
    public IReadOnlyList<InstallationReviewRow> ReviewItems => InstallationReview.Build(CommunityPath, _scanResults, _andrewUpdate, _andrewPackage, IsAndrewUpdateOnly)
        .Select(item => new InstallationReviewRow(item.Package, item.Destination, _localization.GetText("Review." + item.Operation))).ToArray();
    public string ReviewSafety => _localization.GetText("Review.Safety");
    public string ReturnToMissionLabel => _localization.GetText("Review.Return");
    private AndrewUpdateVersion? _reviewInstalledVersion;
    private AndrewUpdateVersion? _reviewNewVersion;
    public string ReviewVersions => _localization.GetText("Review.Versions",
        _reviewInstalledVersion?.Display ?? _localization.GetText("Review.Unknown"),
        _reviewNewVersion?.Display ?? _localization.GetText("Review.Unknown"));

    public ICommand BackCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand BrowseCommunityCommand { get; }
    public ICommand ScanAgainCommand { get; }
    public ICommand StartInstallationCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand OpenDownloadCommand { get; }
    public ICommand ExitCommand { get; }

    public string AppTitle => _localization.GetText("App.Title");
    public string CurrentTitle => _localization.GetText(IsAndrewUpdateOnly && CurrentStep is WizardStep.ZipFolder or WizardStep.Installation
        ? $"Page.Andrew{CurrentStep}.Title"
        : $"Page.{CurrentStep}.Title");
    public string CurrentDescription => _localization.GetText(IsAndrewUpdateOnly && CurrentStep is WizardStep.ZipFolder or WizardStep.Installation
        ? $"Page.Andrew{CurrentStep}.Description"
        : $"Page.{CurrentStep}.Description");
    public string StatusMessage => _statusMessage;
    public string StatusDetail => _statusDetail;
    public string StatusMessageColor => _statusMessageColor;
    public string BackLabel => _localization.GetText("Action.Back");
    public string NextLabel => CurrentStep == WizardStep.Disclaimer
        ? _localization.GetText("Action.Continue")
        : ShouldExitCurrentStep
            ? _localization.GetText("Action.Exit")
            : _localization.GetText("Action.Next");
    public string BrowseLabel => _localization.GetText("Action.Browse");
    public string ScanAgainLabel => _localization.GetText("Action.ScanAgain");
    public string InstallLabel => _localization.GetText(IsAndrewUpdateOnly ? "Action.InstallAndrewUpdate" : "Action.Install");
    public string DownloadLabel => _localization.GetText("Action.Download");
    public string ZipFolderLabel => _localization.GetText(IsAndrewUpdateOnly ? "AndrewUpdate.DropLabel" : "ZipFolder.Label");
    public string ZipFolderHint => _localization.GetText(IsAndrewUpdateOnly ? "AndrewUpdate.DropHint" : "ZipFolder.Hint");
    public string ZipFolderLocationHint => _localization.GetText(IsAndrewUpdateOnly ? "AndrewUpdate.LocationHint" : "ZipFolder.LocationHint");
    public string ZipFolderEmptyStatus => _localization.GetText(IsAndrewUpdateOnly ? "AndrewUpdate.EmptyStatus" : "ZipFolder.EmptyStatus");
    public string VersionDetectedTitle => _h145 switch
    {
        null => _localization.GetText("H145.NotChecked"),
        { Installed: false } => _localization.GetText("H145.NotFound"),
        { Version: null } => _localization.GetText("H145.UnknownVersion"),
        { Version: int version } => _localization.GetText("H145.DetectedTitle", version)
    };
    public string VersionDetectedDetail => _h145 switch
    {
        { IsVersion500OrLater: true, Version: int version } => _localization.GetText("H145.DetectedDetail", version),
        { Installed: true } => _localization.GetText("H145.UpdateRequired"),
        { Installed: false } => _localization.GetText("H145.NotFound"),
        _ => _localization.GetText("H145.NotChecked")
    };
    public string VersionMarkerText => _h145?.Version is int version ? $"Version_{version}.txt" : "Version_*.txt";
    public string H145StatusColor => _h145?.IsVersion500OrLater == true ? "#38D985" : "#F01B3B";
    public string ActionPackStatus => _h145?.ActionPackInstalled == true
        ? _localization.GetText("H145.ActionPackFound")
        : _localization.GetText("H145.ActionPackMissingBaseOnly");
    public string ActionPackStatusColor => _h145?.ActionPackInstalled == true ? "#38D985" : "#F0B429";
    public string PackageSummary => _localization.GetText(
        "Packages.Summary",
        _scanResults.Count(result => result.Definition.Required && result.RequirementSatisfied),
        _scanResults.Count(result => result.Definition.Required),
        _scanResults.Count(result => !result.Definition.Required && result.RequirementSatisfied),
        _scanResults.Count(result => !result.Definition.Required));
    public string CommunityPackageSummary => _localization.GetText(
        "Packages.Summary",
        _communityInventory.Count(result => result.Definition.Required && result.State == PackageState.Installed),
        _communityInventory.Count(result => result.Definition.Required),
        _communityInventory.Count(result => !result.Definition.Required && result.State == PackageState.Installed),
        _communityInventory.Count(result => !result.Definition.Required));
    public string ReadyBanner => IsAndrewUpdateOnly && _andrewUpdate is not null
        ? _localization.GetText($"AndrewUpdate.{_andrewUpdate.Kind}.Detected")
        : _localization.GetText("Installation.ReadyBanner");
    public string CompletedSummary => _completedSummary;
    public string ActionLabel => _localization.GetText("Chrome.Action");
    public string CancelLabel => _localization.GetText("Chrome.Cancel");
    public string SafetyMessage => _localization.GetText("Chrome.SafetyMessage");
    public string TranslationNotice => _localization.GetText("Language.TranslationNotice");
    public string ElapsedTime { get => _elapsedTime; private set => SetProperty(ref _elapsedTime, value); }

    public WizardStep CurrentStep => _wizard.CurrentStep;
    public int CurrentStepNumber => _wizard.GetDisplayNumber(CurrentStep);
    public bool HasArchiveInputs => ZipArchives.Count > 0;
    public bool IsExitAction => ShouldExitCurrentStep;
    public bool CanMoveNext =>
        CurrentStep is not WizardStep.Installation and not WizardStep.Completed &&
        _wizard.CanMoveNext &&
        (CurrentStep != WizardStep.ZipFolder ||
         InstallationFlowPolicy.CanContinueFromArchiveStep(
             _installerMode,
             ZipArchives.Count,
             ShouldExitCurrentStep)) &&
        (CurrentStep != WizardStep.PackageCheck || _installerMode != InstallerMode.FullInstallation || _inputsScanned);
    public bool CanInstall => _scanResults.Count > 0 && _scanResults.All(result => !result.Definition.Required || result.RequirementSatisfied);
    public bool CanStartInstallation => _installerMode switch
    {
        InstallerMode.FullInstallation =>
            (_scanResults.Any(result => result.CanInstall) || _andrewUpdate is not null) &&
            (CanInstall || _allowMissingRequired),
        InstallerMode.AndrewUpdateOnly => _andrewUpdate is not null && _andrewPackage?.Installed == true,
        _ => false
    };
    public bool IsFullInstallation
    {
        get => _installerMode == InstallerMode.FullInstallation;
        set { if (value) SetInstallerMode(InstallerMode.FullInstallation); }
    }
    public bool IsAndrewUpdateOnly
    {
        get => _installerMode == InstallerMode.AndrewUpdateOnly;
        set { if (value) SetInstallerMode(InstallerMode.AndrewUpdateOnly); }
    }
    public bool IsMsfs2020 { get => _selectedSimulator == SimulatorType.Msfs2020; set { if (value) SetSimulator(SimulatorType.Msfs2020); } }
    public bool IsMsfs2024 { get => _selectedSimulator == SimulatorType.Msfs2024; set { if (value) SetSimulator(SimulatorType.Msfs2024); } }

    public LanguageOptionViewModel? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (SetProperty(ref _selectedLanguage, value) && value is not null) _localization.SetCulture(value.Culture);
        }
    }

    public string ZipArchiveCount => _localization.GetText(IsAndrewUpdateOnly ? "AndrewUpdate.SourceCount" : "ZipFolder.Count", ZipArchives.Count);
    public string CommunityPath { get => _communityPath; private set => SetProperty(ref _communityPath, value); }
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RaiseCommandStates();
        }
    }

    // Navigation gates execute detection and validation before exposing later steps.
    public async Task ReviewDownloadedMissionAsync(string file)
    {
        if (!IsAndrewUpdateOnly || CurrentStep != WizardStep.ZipFolder || IsBusy)
            throw new InvalidOperationException("A fresh mission update workflow is required.");
        if (!AcceptZipDrop([file])) throw new InvalidOperationException("The verified mission input could not be loaded.");
        await MoveNextAsync().ConfigureAwait(true);
        if (CurrentStep != WizardStep.Installation) throw new InvalidOperationException("The mission could not reach final review.");
    }

    private async Task MoveNextAsync()
    {
        try
        {
            if (ShouldExitCurrentStep)
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            switch (CurrentStep)
            {
                case WizardStep.ZipFolder:
                    if (_installerMode == InstallerMode.AndrewUpdateOnly)
                    {
                        ValidateAndrewUpdateSource();
                        var sources = ZipArchives.Select(item => item.FullPath).ToArray();
                        _andrewUpdate = sources.All(IsCumulativeJsonInput)
                            ? _engine.SelectNewestCumulativeUpdate(sources)
                            : _engine.InspectAndrewUpdate(sources.Single());
                        SetStatus(
                            _localization.GetText($"AndrewUpdate.{_andrewUpdate.Kind}.Detected"),
                            _andrewUpdate.Version?.Display ?? Path.GetFileName(_andrewUpdate.SourcePath));
                        OnPropertiesChanged(nameof(ReadyBanner), nameof(CanStartInstallation));
                    }
                    else
                    {
                        if (ZipArchives.Count > 0)
                        {
                            if (!await ScanPackagesAsync().ConfigureAwait(true)) return;
                        }
                        else
                        {
                            // The initial Community inventory is informational. Missing
                            // packages are supplied by drag and drop on the next step.
                            _scanResults = _communityInventory;
                            _allowMissingRequired = false;
                            _inputsScanned = true;
                            RebuildPackageRows();
                            SetStatus(
                                _localization.GetText("Status.PackageScanComplete"),
                                PackageSummary,
                                CanInstall ? "#38D985" : "#F0B429");
                            OnPropertiesChanged(
                                nameof(CanMoveNext),
                                nameof(NextLabel),
                                nameof(IsExitAction),
                                nameof(CanStartInstallation));
                        }
                    }
                    break;
                case WizardStep.Simulator:
                    await EnsureCommunityAndH145Async().ConfigureAwait(true);
                    if (string.IsNullOrEmpty(CommunityPath)) return;
                    break;
                case WizardStep.PackageCheck:
                    if (!CanInstall)
                    {
                        if (!_scanResults.Any(result => result.CanInstall) && _andrewUpdate is null)
                        {
                            _dialogs.ShowError(AppTitle, _localization.GetText("Packages.NoValid"));
                            return;
                        }
                        var unavailable = string.Join(Environment.NewLine, _scanResults
                            .Where(result => result.Definition.Required && !result.CanInstall)
                            .Select(result => $"• {result.Definition.DisplayName}"));
                        if (!_dialogs.Confirm(AppTitle, _localization.GetText("Packages.ConfirmProceedAnyway", unavailable))) return;
                        _allowMissingRequired = true;
                        OnPropertiesChanged(nameof(CanStartInstallation));
                        RaiseCommandStates();
                    }
                    break;
            }

            if (CurrentStep == WizardStep.PackageCheck || CurrentStep == WizardStep.ZipFolder && IsAndrewUpdateOnly)
            {
                IsBusy = true;
                try
                {
                var versions = await Task.Run(() =>
                {
                    var installed = _andrewPackage is null ? null : InstallerEngine.ReadInstalledAndrewVersion(_andrewPackage);
                    var next = _andrewUpdate?.Version;
                    var primary = _scanResults.FirstOrDefault(item => item.CanInstall && item.Definition.Id == "ANDREW_MISSION_PACK");
                    if (next is null && primary?.ArchivePath is { } source)
                    {
                        try { next = _engine.InspectAndrewUpdate(source).Version; }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InstallerException) { }
                    }
                    return (installed, next);
                }).ConfigureAwait(true);
                (_reviewInstalledVersion, _reviewNewVersion) = versions;
                }
                finally { IsBusy = false; }
            }
            _wizard.MoveNext();
        }
        catch (Exception exception)
        {
            HandleError(exception);
        }
    }

    public async Task PrepareCompanionWorkflowAsync(string communityPath, SimulatorType simulator, InstallerMode mode)
    {
        SetInstallerMode(mode);
        _selectedSimulator = simulator;
        CommunityPath = communityPath;
        await EnsureCommunityAndH145Async().ConfigureAwait(true);
        if (_installerMode == InstallerMode.AndrewUpdateOnly && _andrewPackage?.Installed != true)
            throw new InstallerException("ANDREW-003", _localization.GetText("AndrewUpdate.PrimaryMissing", communityPath));
        _wizard.BeginCompanionWorkflow();
        RebuildWizardSteps();
        RefreshWizardState();
    }

    private void MoveBack() => _wizard.MoveBack();

    private void NavigateTo(WizardStepItemViewModel? item)
    {
        if (!IsBusy && item is not null) _wizard.NavigateTo(item.Step);
    }

    public bool AcceptZipDrop(IReadOnlyCollection<string> droppedPaths)
    {
        if (IsBusy || droppedPaths.Count == 0) return false;

        var candidates = new List<string>();
        foreach (var droppedPath in droppedPaths)
        {
            if (Directory.Exists(droppedPath))
            {
                candidates.AddRange(EnumerateDroppedFiles(droppedPath)
                    .Where(path => IsAndrewUpdateOnly ? IsSupportedAndrewUpdate(path) : IsSupportedInstallationInput(path)));
            }
            else if (File.Exists(droppedPath) && (IsAndrewUpdateOnly ? IsSupportedAndrewUpdate(droppedPath) : IsSupportedInstallationInput(droppedPath)))
            {
                candidates.Add(droppedPath);
            }
        }

        if (candidates.Count == 0)
        {
            _dialogs.ShowError(AppTitle, _localization.GetText(IsAndrewUpdateOnly ? "Error.AndrewSingleSource" : "Error.ZipDropInvalid"));
            return false;
        }

        var distinctCandidates = candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (IsAndrewUpdateOnly &&
            distinctCandidates.Length != 1 &&
            !distinctCandidates.All(IsCumulativeJsonInput))
        {
            _dialogs.ShowError(AppTitle, _localization.GetText("Error.AndrewSingleSource"));
            return false;
        }

        var existing = ZipArchives.Select(item => item.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (IsAndrewUpdateOnly)
        {
            var canAccumulateCumulativeFiles =
                distinctCandidates.All(IsCumulativeJsonInput) &&
                ZipArchives.All(item => IsCumulativeJsonInput(item.FullPath));
            if (!canAccumulateCumulativeFiles)
            {
                ZipArchives.Clear();
                existing.Clear();
            }
        }
        foreach (var candidate in candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (existing.Add(candidate)) ZipArchives.Add(new ZipArchivePathViewModel(candidate));
        }

        _scanResults = _communityInventory;
        _andrewUpdate = null;
        _allowMissingRequired = false;
        _inputsScanned = false;
        RebuildPackageRows();
        OnPropertiesChanged(
            nameof(ZipArchiveCount),
            nameof(HasArchiveInputs),
            nameof(PackageSummary),
            nameof(CanInstall),
            nameof(CanStartInstallation),
            nameof(CanMoveNext),
            nameof(NextLabel),
            nameof(IsExitAction));
        SetStatus(_localization.GetText("Status.ZipFilesAdded", candidates.Count), ZipArchiveCount);
        RaiseCommandStates();
        return true;
    }

    private static bool IsSupportedArchive(string path)
    {
        string[] extensions = [".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz", ".tbz2", ".xz", ".txz", ".lz", ".lzip", ".zst", ".zstd", ".tzst", ".arj", ".arc"];
        return extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsSupportedAndrewUpdate(string path) =>
        IsSupportedArchive(path) || IsCumulativeJsonInput(path);

    private bool IsSupportedInstallationInput(string path) =>
        IsSupportedArchive(path) || IsCumulativeJsonInput(path);

    private bool IsCumulativeJsonInput(string path)
    {
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return _engine.InspectAndrewUpdate(path).Kind == AndrewUpdateKind.Cumulative;
        }
        catch (InstallerException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateDroppedFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files) yield return file;

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(directory);
                }
                catch (IOException)
                {
                    // Ignore directories that disappear while a dropped tree is being inspected.
                }
            }
        }
    }

    private void BrowseCommunityFolder()
    {
        var selected = _folderPicker.PickFolder(CommunityPath, _localization.GetText("Simulator.SelectCommunity"));
        if (string.IsNullOrWhiteSpace(selected)) return;
        CommunityPath = selected;
        _h145 = _engine.DetectH145(selected);
        _andrewPackage = _engine.DetectAndrewPackage(selected);
        _communityInventory = _engine.InspectCommunityPackages(selected, _selectedSimulator);
        _scanResults = _communityInventory;
        _inputsScanned = false;
        RebuildCommunityPackageRows();
        RebuildPackageRows();
        RefreshH145Properties();
        OnPropertiesChanged(nameof(CanMoveNext), nameof(NextLabel), nameof(IsExitAction));
        SetStatus(_localization.GetText("Status.CommunitySelected"), selected);
    }

    private async Task EnsureCommunityAndH145Async()
    {
        if (string.IsNullOrWhiteSpace(CommunityPath) || !Directory.Exists(CommunityPath))
        {
            SetStatus(_localization.GetText("Status.DetectingCommunity"), string.Empty);
            CommunityPath = await _engine.DetectCommunityPathAsync(_selectedSimulator, CancellationToken.None).ConfigureAwait(true) ?? string.Empty;
            if (string.IsNullOrEmpty(CommunityPath))
            {
                BrowseCommunityFolder();
            }
        }

        if (!string.IsNullOrEmpty(CommunityPath))
        {
            _h145 = _engine.DetectH145(CommunityPath);
            _andrewPackage = _engine.DetectAndrewPackage(CommunityPath);
            _communityInventory = _engine.InspectCommunityPackages(CommunityPath, _selectedSimulator);
            _scanResults = _communityInventory;
            _inputsScanned = false;
            RebuildCommunityPackageRows();
            RebuildPackageRows();
            RefreshH145Properties();
            OnPropertiesChanged(nameof(CanMoveNext), nameof(NextLabel), nameof(IsExitAction));
            SetStatus(
                _localization.GetText("Status.H145Checked"),
                CommunityPath,
                _h145.IsVersion500OrLater ? "#38D985" : "#F0B429");
        }
    }

    private async Task<bool> ScanPackagesAsync()
    {
        var succeeded = await RunBusyAsync(async (progress, token) =>
        {
            // Invalidate the previous plan before touching inputs that may have
            // disappeared. Publish a replacement only after the scan succeeds.
            _inputsScanned = false;
            _allowMissingRequired = false;
            _andrewUpdate = null;
            _scanResults = _communityInventory;
            RebuildPackageRows();

            ValidateZipSource();
            var archivePaths = ZipArchives
                .Select(item => item.FullPath)
                .Where(IsSupportedArchive)
                .ToArray();
            var cumulativePaths = ZipArchives
                .Select(item => item.FullPath)
                .Where(IsCumulativeJsonInput)
                .ToArray();
            var andrewUpdate = cumulativePaths.Length == 0
                ? null
                : _engine.SelectNewestCumulativeUpdate(cumulativePaths);
            IReadOnlyList<PackageScanResult> scanResults = _communityInventory;
            if (archivePaths.Length > 0)
            {
                var archiveResults = await _engine.ScanPackagesAsync(archivePaths, _selectedSimulator, progress, token).ConfigureAwait(true);
                scanResults = archiveResults
                    .Select(archiveResult =>
                    {
                        if (archiveResult.CanInstall) return archiveResult;
                        var installed = _communityInventory.FirstOrDefault(result =>
                            result.Definition.Id == archiveResult.Definition.Id &&
                            result.State == PackageState.Installed);
                        return installed ?? archiveResult;
                    })
                    .ToArray();
            }
            if (andrewUpdate is not null)
            {
                var primaryAvailable = _andrewPackage?.Installed == true ||
                    scanResults.Any(result =>
                        result.Definition.Id == "ANDREW_MISSION_PACK" &&
                        result.CanInstall);
                if (!primaryAvailable)
                {
                    throw new InstallerException(
                        "ANDREW-003",
                        _localization.GetText(
                            "AndrewUpdate.PrimaryMissing",
                            Path.Combine(CommunityPath, "hpg-airbus-h145-z-andrew-mission-pack")));
                }
            }
            _andrewUpdate = andrewUpdate;
            _scanResults = scanResults;
            _inputsScanned = true;
            RebuildPackageRows();
            SetStatus(_localization.GetText("Status.PackageScanComplete"), PackageSummary, CanInstall ? "#38D985" : "#F0B429");
        }).ConfigureAwait(true);
        OnPropertiesChanged(nameof(CanMoveNext), nameof(NextLabel), nameof(IsExitAction), nameof(CanStartInstallation));
        return succeeded;
    }

    private async Task InstallAsync()
    {
        if (!CanStartInstallation) return;
        if (_installerMode == InstallerMode.AndrewUpdateOnly)
        {
            var update = _andrewUpdate ?? throw new InstallerException("ANDREW-001", _localization.GetText("Error.AndrewUpdateMissing"));
            _ = await RunBusyAsync(async (progress, token) =>
            {
                var result = await _engine.InstallAndrewUpdateAsync(new AndrewUpdateRequest(CommunityPath, update), progress, token).ConfigureAwait(true);
                _completedSummary = _localization.GetText(
                    "Completed.AndrewUpdateSummary",
                    _localization.GetText($"AndrewUpdate.Kind.{result.Kind}"),
                    result.UpdatedLayouts,
                    result.LogDirectory);
                OnPropertyChanged(nameof(CompletedSummary));
                _wizard.MoveNext();
            }).ConfigureAwait(true);
            return;
        }

        var request = new InstallationRequest(
            _selectedSimulator,
            CommunityPath,
            _scanResults,
            _allowMissingRequired,
            _andrewUpdate);
        _ = await RunBusyAsync(async (progress, token) =>
        {
            var result = await _engine.InstallAsync(request, progress, token).ConfigureAwait(true);
            _completedSummary = _localization.GetText("Completed.RealSummary", result.InstalledPackages, result.UpdatedLayouts, result.LogDirectory);
            OnPropertyChanged(nameof(CompletedSummary));
            _wizard.MoveNext();
        }).ConfigureAwait(true);
    }

    public async Task CheckForUpdatesAsync()
    {
        ApplicationUpdateInfo? update = null;
        string? downloadedExecutable = null;
        var completed = await RunBusyAsync(async (_, token) =>
        {
            SetStatus(_localization.GetText("Update.Checking"), string.Empty);
            var currentVersion = Version.TryParse(AppEdition.Version, out var parsedVersion)
                ? parsedVersion
                : new Version(0, 0, 0);

            try
            {
                update = await _applicationUpdates
                    .CheckForUpdateAsync(currentVersion, AppEdition.ExecutableAssetName, token)
                    .ConfigureAwait(true);
            }
            catch (ApplicationUpdateException exception)
            {
                throw new InstallerException(exception.Code, LocalizeUpdateError(exception));
            }

            if (update is null)
            {
                SetStatus(_localization.GetText("Update.UpToDate"), AppEdition.Version, "#38D985");
                _dialogs.ShowInformation(AppTitle, _localization.GetText("Update.UpToDate"));
                return;
            }

            if (!_dialogs.Confirm(
                    _localization.GetText("Update.DialogTitle"),
                    _localization.GetText("Update.AvailableConfirm", update.Version, currentVersion)))
            {
                SetStatus(_localization.GetText("Status.Cancelled"), string.Empty);
                update = null;
                return;
            }

            var updateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppEdition.RuntimeDirectoryName,
                "Updates",
                update.Version.ToString());
            var downloadProgress = new Progress<ApplicationUpdateProgress>(value =>
            {
                ProgressValue = value.Percent;
                SetStatus(
                    _localization.GetText("Update.Downloading", update.Version),
                    value.TotalBytes is > 0
                        ? $"{value.BytesReceived / 1024 / 1024} MB / {value.TotalBytes.Value / 1024 / 1024} MB"
                        : $"{value.BytesReceived / 1024 / 1024} MB");
            });

            try
            {
                downloadedExecutable = await _applicationUpdates
                    .DownloadAsync(update, updateDirectory, downloadProgress, token)
                    .ConfigureAwait(true);
            }
            catch (ApplicationUpdateException exception)
            {
                throw new InstallerException(exception.Code, LocalizeUpdateError(exception));
            }

            ProgressValue = 100;
            SetStatus(_localization.GetText("Update.Restarting"), update.Version.ToString(), "#38D985");
        }).ConfigureAwait(true);

        if (!completed || update is null || downloadedExecutable is null)
        {
            return;
        }

        try
        {
            ApplicationUpdateBootstrapper.StartApply(downloadedExecutable, update.Sha256);
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            HandleError(new InstallerException("UPDATE-APPLY", _localization.GetText("Update.ApplyFailed"), exception));
        }
    }

    private string LocalizeUpdateError(ApplicationUpdateException exception) => exception.Code switch
    {
        "UPDATE-PRIVATE" => _localization.GetText("Update.RepositoryUnavailable"),
        "UPDATE-ASSET" => _localization.GetText("Update.AssetMissing", AppEdition.ExecutableAssetName),
        "UPDATE-HASH" or "UPDATE-SIZE" => _localization.GetText("Update.IntegrityFailed"),
        "UPDATE-DOWNLOAD" => _localization.GetText("Update.DownloadFailed"),
        _ => _localization.GetText("Update.CheckFailed")
    };

    private async Task<bool> RunBusyAsync(Func<IProgress<InstallerProgress>, CancellationToken, Task> operation)
    {
        if (IsBusy) return false;
        IsBusy = true;
        ProgressValue = 0;
        _operationCancellation = new CancellationTokenSource();
        _stopwatch.Restart();
        _elapsedTimer.Start();
        var progress = new Progress<InstallerProgress>(UpdateProgress);
        try
        {
            await operation(progress, _operationCancellation.Token).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus(_localization.GetText("Status.Cancelled"), _engine.LogDirectory);
            return false;
        }
        catch (Exception exception)
        {
            HandleError(exception);
            return false;
        }
        finally
        {
            _stopwatch.Stop();
            _elapsedTimer.Stop();
            _operationCancellation.Dispose();
            _operationCancellation = null;
            IsBusy = false;
        }
    }

    private void UpdateProgress(InstallerProgress value)
    {
        ProgressValue = Math.Clamp(value.OverallPercent, 0, 100);
        var color = value.Phase.Equals("Completed", StringComparison.OrdinalIgnoreCase) || value.OverallPercent >= 100
            ? "#38D985"
            : "#2997FF";
        SetStatus(value.Message, value.Detail, color);
    }

    private void CancelOperation() => _operationCancellation?.Cancel();

    public void RequestCancellation() => CancelOperation();

    private static void OpenDownloadPage(PackageStatusViewModel? package)
    {
        if (package is null || string.IsNullOrWhiteSpace(package.DownloadUrl)) return;
        Process.Start(new ProcessStartInfo { FileName = package.DownloadUrl, UseShellExecute = true });
    }

    private void ValidateZipSource()
    {
        if (ZipArchives.Count == 0) throw new InstallerException("ZIP-001", _localization.GetText("Error.NoZipFiles"));
        if (ZipArchives.Any(item => !File.Exists(item.FullPath))) throw new InstallerException("ZIP-001", _localization.GetText("Error.ZipFileMissing"));
    }

    private void ValidateAndrewUpdateSource()
    {
        if (ZipArchives.Count == 0 ||
            (ZipArchives.Count > 1 && !ZipArchives.All(item => IsCumulativeJsonInput(item.FullPath))))
        {
            throw new InstallerException("ANDREW-001", _localization.GetText("Error.AndrewSingleSource"));
        }
        if (ZipArchives.Any(item => !File.Exists(item.FullPath) || !IsSupportedAndrewUpdate(item.FullPath)))
            throw new InstallerException("ANDREW-001", _localization.GetText("Error.AndrewUpdateMissing"));
    }

    private void RebuildPackageRows()
    {
        Packages.Clear();
        foreach (var result in _scanResults)
        {
            Packages.Add(new PackageStatusViewModel(
                result,
                _localization.GetText($"PackageState.{result.State}"),
                _localization.GetText(result.Definition.Required ? "Package.Required" : "Package.Optional")));
        }
        OnPropertiesChanged(nameof(PackageSummary), nameof(CanInstall), nameof(CanStartInstallation));
        RaiseCommandStates();
    }

    private void RebuildCommunityPackageRows()
    {
        CommunityPackages.Clear();
        foreach (var result in _communityInventory)
        {
            CommunityPackages.Add(new PackageStatusViewModel(
                result,
                _localization.GetText($"PackageState.{result.State}"),
                _localization.GetText(result.Definition.Required ? "Package.Required" : "Package.Optional")));
        }
        OnPropertyChanged(nameof(CommunityPackageSummary));
    }

    private void RefreshH145Properties() => OnPropertiesChanged(nameof(VersionDetectedTitle), nameof(VersionDetectedDetail), nameof(VersionMarkerText), nameof(H145StatusColor), nameof(ActionPackStatus), nameof(ActionPackStatusColor));

    private void SetSimulator(SimulatorType simulator)
    {
        if (_selectedSimulator == simulator) return;
        _selectedSimulator = simulator;
        CommunityPath = string.Empty;
        _h145 = null;
        _andrewPackage = null;
        _communityInventory = [];
        _scanResults = [];
        _inputsScanned = false;
        Packages.Clear();
        CommunityPackages.Clear();
        OnPropertiesChanged(nameof(IsMsfs2020), nameof(IsMsfs2024), nameof(VersionDetectedTitle), nameof(VersionDetectedDetail), nameof(VersionMarkerText), nameof(H145StatusColor), nameof(ActionPackStatus), nameof(ActionPackStatusColor), nameof(CanStartInstallation));
    }

    // Changing mode resets data that belongs to a different workflow route.
    private void SetInstallerMode(InstallerMode mode)
    {
        if (_installerMode == mode) return;
        _installerMode = mode;
        _wizard.SetMode(mode);
        _scanResults = [];
        _communityInventory = [];
        _andrewPackage = null;
        _andrewUpdate = null;
        _allowMissingRequired = false;
        _inputsScanned = false;
        Packages.Clear();
        CommunityPackages.Clear();
        ZipArchives.Clear();
        RebuildWizardSteps();
        OnPropertiesChanged(
            nameof(IsFullInstallation),
            nameof(IsAndrewUpdateOnly),
            nameof(CurrentStepNumber),
            nameof(CanStartInstallation),
            nameof(ZipArchiveCount),
            nameof(HasArchiveInputs),
            nameof(IsExitAction),
            nameof(ZipFolderLabel),
            nameof(ZipFolderHint),
            nameof(ZipFolderLocationHint),
            nameof(ZipFolderEmptyStatus),
            nameof(InstallLabel),
            nameof(CurrentTitle),
            nameof(CurrentDescription),
            nameof(ReadyBanner));
        SetStatus(
            _localization.GetText(mode switch
            {
                InstallerMode.FullInstallation => "Status.Mode.Full",
                InstallerMode.AndrewUpdateOnly => "Status.Mode.AndrewOnly",
                _ => "Status.Ready"
            }),
            string.Empty);
        RefreshWizardState();
    }

    private void RebuildWizardSteps()
    {
        Steps.Clear();
        foreach (var step in _wizard.Route.Where(step => step != WizardStep.Disclaimer))
        {
            Steps.Add(new WizardStepItemViewModel(
                step,
                _wizard.GetDisplayNumber(step),
                $"Step.{step}",
                $"Step.{step}.Description",
                _localization));
        }
    }

    private void HandleError(Exception exception)
    {
        var code = exception is InstallerException installer ? installer.Code : "APP-001";
        SetStatus($"{code}: {exception.Message}", _engine.LogDirectory, "#F46D75");
        _dialogs.ShowError(AppTitle, $"{code}{Environment.NewLine}{exception.Message}{Environment.NewLine}{Environment.NewLine}{_engine.LogDirectory}");
    }

    private void OnWizardChanged(object? sender, EventArgs e)
    {
        RefreshWizardState();
        if (!IsBusy) SetStatus(_localization.GetText($"Status.Step.{CurrentStep}"), string.Empty);
    }

    private void RefreshWizardState()
    {
        foreach (var item in Steps)
        {
            item.IsActive = item.Step == CurrentStep;
            item.IsComplete = (int)item.Step < (int)CurrentStep;
            item.IsEnabled = _wizard.CanNavigateTo(item.Step);
        }
        OnPropertiesChanged(nameof(CurrentStep), nameof(CurrentStepNumber), nameof(CurrentTitle), nameof(CurrentDescription), nameof(CanMoveNext), nameof(NextLabel), nameof(IsExitAction), nameof(ReviewItems), nameof(ReviewVersions));
        RaiseCommandStates();
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        foreach (var step in Steps) step.RefreshText();
        if (_scanResults.Count > 0) RebuildPackageRows();
        if (_communityInventory.Count > 0) RebuildCommunityPackageRows();
        OnPropertiesChanged(string.Empty);
    }

    private void UpdateElapsedTime() => ElapsedTime = $"{_stopwatch.Elapsed:hh\\:mm\\:ss}";

    private void SetStatus(string message, string detail, string color = "#2997FF")
    {
        _statusMessage = message;
        _statusDetail = detail;
        _statusMessageColor = color;
        OnPropertiesChanged(nameof(StatusMessage), nameof(StatusDetail), nameof(StatusMessageColor));
    }

    private void RaiseCommandStates()
    {
        ((RelayCommand)BackCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)NextCommand).RaiseCanExecuteChanged();
        ((RelayCommand)BrowseCommunityCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ScanAgainCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)StartInstallationCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenLogCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)CheckForUpdatesCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ExitCommand).RaiseCanExecuteChanged();
    }

    private bool ShouldExitCurrentStep =>
        _installerMode == InstallerMode.FullInstallation &&
        (CurrentStep switch
        {
            WizardStep.ZipFolder => InstallationFlowPolicy.ShouldExitBeforeAddingInputs(_communityInventory, ZipArchives.Count),
            WizardStep.PackageCheck => InstallationFlowPolicy.ShouldExitAfterScan(_scanResults, _andrewUpdate, _inputsScanned),
            _ => false
        });

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wizard.Changed -= OnWizardChanged;
        _localization.CultureChanged -= OnCultureChanged;
        _elapsedTimer.Stop();
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        if (_ownsEngine && _engine is IDisposable disposable) disposable.Dispose();
        if (_applicationUpdates is IDisposable updateDisposable) updateDisposable.Dispose();
        GC.SuppressFinalize(this);
    }
}
