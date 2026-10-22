using System.IO;
using System.Windows.Input;
using RandomEverywhereInstaller.App.Infrastructure;
using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Core.Models;
using RandomEverywhereInstaller.Core.Updates;
using RandomEverywhereInstaller.Infrastructure.Installer;
using RandomEverywhereInstaller.Infrastructure.Updates;

namespace RandomEverywhereInstaller.App.ViewModels;

public sealed partial class CompanionViewModel
{
    private IMissionUpdateService _missionUpdates = null!;
    private bool _ownsMissionUpdates;
    private MissionUpdateInfo? _publishedMission;
    private AndrewUpdateVersion? _installedMissionRevision;
    private string _missionCheckCommunity = string.Empty;
    private bool _missionPrimaryInstalled;
    private CancellationTokenSource? _missionCancellation;

    public ICommand CheckMissionUpdateCommand { get; private set; } = null!;
    public ICommand DownloadMissionUpdateCommand { get; private set; } = null!;
    public ICommand CancelMissionUpdateCommand { get; private set; } = null!;
    public ICommand ImportMissionUpdateCommand { get; private set; } = null!;
    public event Func<string, Task>? MissionUpdateReady;
    public string MissionUpdateNotes => _publishedMission?.Notes is { Length: > 0 } notes ? notes : T("C.MissionUpdate.NoNotes");
    public bool HasMissionUpdate => _publishedMission is not null && _missionPrimaryInstalled &&
        _missionCheckCommunity.Equals(CommunityPath, StringComparison.OrdinalIgnoreCase) &&
        (_installedMissionRevision is null || MissionReleaseVersion.Compare(_publishedMission.Version, _installedMissionRevision) > 0);
    public string MissionUpdateSummary => _publishedMission is null || _missionCheckCommunity != CommunityPath ? T("C.MissionUpdate.Hint") :
        T(HasMissionUpdate ? "C.MissionUpdate.Available" : "C.MissionUpdate.Current",
            _installedMissionRevision?.Display ?? T("C.Mission.Unknown"), _publishedMission.Version.Display);
    public string MissionUpdateSource => _publishedMission is null ? string.Empty :
        $"{_publishedMission.Source} · {_publishedMission.Size / 1048576d:0.0} MB\n{_publishedMission.DownloadUri}\n" +
        T(_publishedMission.Sha256 is null ? "C.MissionUpdate.NoDigest" : "C.MissionUpdate.Digest");

    private void InitializeMissionUpdates(IMissionUpdateService? service)
    {
        _ownsMissionUpdates = service is null;
        _missionUpdates = service ?? new MissionUpdateService();
        CheckMissionUpdateCommand = new AsyncRelayCommand(CheckMissionUpdateAsync, () => !IsBusy && !IsRunning && HasCommunity);
        DownloadMissionUpdateCommand = new AsyncRelayCommand(DownloadMissionUpdateAsync, () => !IsBusy && !IsRunning && HasMissionUpdate);
        CancelMissionUpdateCommand = new RelayCommand(() => _missionCancellation?.Cancel(), () => _missionCancellation is not null);
        ImportMissionUpdateCommand = new AsyncRelayCommand(() => OpenInstallerAsync(InstallerMode.AndrewUpdateOnly), () => !IsBusy && !IsRunning && HasCommunity);
    }

    private void RefreshMissionUpdatePresentation()
    {
        OnPropertiesChanged(nameof(MissionUpdateNotes), nameof(MissionUpdateSummary), nameof(MissionUpdateSource), nameof(HasMissionUpdate));
        RefreshCommands();
    }

    private async Task CheckMissionUpdateAsync()
    {
        if (!TryLeaveAudioEdits()) return;
        using var cancellation = new CancellationTokenSource();
        _missionCancellation = cancellation; RefreshCommands();
        try
        {
            await ExecuteAsync(async () =>
            {
                _publishedMission = null;
                _missionCheckCommunity = CommunityPath;
                var installed = _engine.DetectAndrewPackage(CommunityPath);
                _missionPrimaryInstalled = installed.Installed;
                _installedMissionRevision = await Task.Run(() => InstallerEngine.ReadInstalledAndrewVersion(installed), cancellation.Token).ConfigureAwait(true);
                RefreshMissionUpdatePresentation();
                if (!installed.Installed) { Status = T("C.MissionUpdate.Missing"); StatusColor = "#E2BD7D"; return; }
                _publishedMission = await _missionUpdates.CheckAsync(cancellation.Token).ConfigureAwait(true);
                RefreshMissionUpdatePresentation();
                Status = MissionUpdateSummary;
                StatusColor = HasMissionUpdate ? "#E2BD7D" : "#38D985";
            }).ConfigureAwait(true);
        }
        finally { _missionCancellation = null; RefreshMissionUpdatePresentation(); }
    }

    private async Task DownloadMissionUpdateAsync()
    {
        if (!HasMissionUpdate || _publishedMission is not { } update || !TryLeaveAudioEdits()) return;
        // Confirmation precedes cache creation or network activity, not merely Community writes.
        if (!_dialogs.Confirm(AppEdition.ProductName, T("C.MissionUpdate.Confirm", update.Version.Display, MissionUpdateSource))) return;
        MissionDownloadCache? cache = null;
        var ready = false;
        using var cancellation = new CancellationTokenSource();
        _missionCancellation = cancellation; RefreshCommands();
        try
        {
            await ExecuteAsync(async () =>
            {
                if (!_engine.DetectAndrewPackage(CommunityPath).Installed) throw new IOException(T("C.MissionUpdate.Missing"));
                cache = MissionDownloadCache.Create(CommunityPath, update.Size);
                var progress = new Progress<ApplicationUpdateProgress>(p => { Progress = p.Percent; Detail = $"{p.BytesReceived:N0} / {p.TotalBytes:N0}"; });
                var digest = await _missionUpdates.DownloadAsync(update, cache.FilePath, progress, cancellation.Token).ConfigureAwait(true);
                cancellation.Token.ThrowIfCancellationRequested();
                await File.AppendAllTextAsync(Path.Combine(_engine.LogDirectory, "companion.log"),
                    $"{DateTime.UtcNow:O} [INFO] Mission download verified: source={update.Source}; version={update.Version.Display}; bytes={update.Size}; sha256={digest}{Environment.NewLine}").ConfigureAwait(true);
                ready = true;
            }).ConfigureAwait(true);
            _missionCancellation = null; RefreshCommands();
            if (!ready || cache is null || _disposed) return;
            if (MissionUpdateReady is null) throw new InvalidOperationException("Mission update review is not connected.");
            await MissionUpdateReady(cache.FilePath).ConfigureAwait(true);
            if (!_disposed)
            {
                await RefreshInventoryAsync().ConfigureAwait(true);
                _installedMissionRevision = InstallerEngine.ReadInstalledAndrewVersion(_engine.DetectAndrewPackage(CommunityPath));
                Section = "Mission";
            }
        }
        catch (Exception error)
        {
            Status = T("C.Status.Error"); Detail = error.Message; StatusColor = "#F01B3B";
            System.Diagnostics.Trace.TraceError("Mission update review failed: {0}", error);
        }
        finally { _missionCancellation = null; cache?.Dispose(); RefreshMissionUpdatePresentation(); }
    }
}
