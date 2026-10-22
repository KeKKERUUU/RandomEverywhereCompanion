using System.IO;
using System.IO.Compression;
using RandomEverywhereInstaller.App.Services;
using RandomEverywhereInstaller.App.ViewModels;
using RandomEverywhereInstaller.Core.Models;
using RandomEverywhereInstaller.Core.Services;
using RandomEverywhereInstaller.Core.Updates;
using RandomEverywhereInstaller.Core.Wizard;

namespace RandomEverywhereInstaller.App.Tests;

internal static partial class Program
{
    private static void CommandErrorsAreRecoverable()
    {
        using var fixture = new Fixture(savedCommunity: true);
        var dialogs = new CommandAuditDialogs();
        using var installer = new MainWindowViewModel(fixture.Localization, new WizardState(),
            new WindowsFolderPickerService(), dialogs, fixture.Engine, new CommandAuditUpdates(), ownsEngine: false);
        // The supplied temporary Community already exists, so preparation does not
        // need the folder picker or asynchronous simulator-path discovery.
        installer.PrepareCompanionWorkflowAsync(fixture.WorkFolder, SimulatorType.Msfs2024,
            InstallerMode.FullInstallation).GetAwaiter().GetResult();
        var archivePath = Path.Combine(Path.GetDirectoryName(fixture.SettingsPath)!, "pack vehicles fr.zip");
        WriteCommandAuditArchive(archivePath);
        Check(installer.AcceptZipDrop([archivePath]), "The temporary archive was not accepted");
        RunCommand(installer.NextCommand);
        Check(installer.CurrentStep == WizardStep.PackageCheck && installer.CanMoveNext,
            "The valid archive did not reach package review");
        Check(installer.Packages.Any(package => package.Result.CanInstall),
            "The initial scan did not recognize the fixture package");

        File.Delete(archivePath);
        RunCommand(installer.ScanAgainCommand);
        Check(Unhandled.Count == 0, "Missing archive escaped to the dispatcher: " +
            string.Join(Environment.NewLine, Unhandled));
        var expectedMessage = fixture.Localization.GetText("Error.ZipFileMissing");
        Check(dialogs.Errors.Count == 1 && dialogs.Errors[0].Contains(expectedMessage, StringComparison.Ordinal),
            "Missing archive did not produce the localized operational error");
        Check(installer.StatusMessage.Contains("ZIP-001", StringComparison.Ordinal) &&
            installer.StatusMessage.Contains(expectedMessage, StringComparison.Ordinal),
            "The review page did not retain the recoverable error");
        Check(installer.CurrentStep == WizardStep.PackageCheck && !installer.IsBusy &&
            !installer.CanMoveNext && !installer.CanStartInstallation,
            "Failed re-scan left stale downstream navigation or an installation plan enabled");
        Check(installer.ZipArchives.Count == 1 && installer.ZipArchives[0].FullPath == archivePath &&
            installer.ScanAgainCommand.CanExecute(null),
            "Recoverable failure discarded the input or disabled retry");

        WriteCommandAuditArchive(archivePath);
        RunCommand(installer.ScanAgainCommand);
        Check(dialogs.Errors.Count == 1 && Unhandled.Count == 0,
            "Successful retry added another error");
        Check(installer.CanMoveNext && !installer.IsBusy &&
            installer.Packages.Any(package => package.Result.CanInstall),
            "Restoring the source did not restore a valid package review");
        Check(installer.StatusMessage == fixture.Localization.GetText("Status.PackageScanComplete"),
            "Successful retry retained the missing-source error");
    }

    private static void WriteCommandAuditArchive(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in new[] { "manifest.json", "layout.json" })
        {
            using var writer = new StreamWriter(archive.CreateEntry("fr-emergency/" + file).Open());
            writer.Write("{}");
        }
    }

    private sealed class CommandAuditDialogs : IUserDialogService
    {
        public List<string> Errors { get; } = [];
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) => Errors.Add(message);
        public void ShowInformation(string title, string message) =>
            throw new InvalidOperationException("Unexpected informational dialog: " + message);
    }

    private sealed class CommandAuditUpdates : IApplicationUpdateService
    {
        public Task<ApplicationUpdateInfo?> CheckForUpdateAsync(Version version, string asset, CancellationToken token) =>
            throw new InvalidOperationException("The command audit must not check for application updates.");
        public Task<string> DownloadAsync(ApplicationUpdateInfo update, string destination,
            IProgress<ApplicationUpdateProgress>? progress, CancellationToken token) =>
            throw new InvalidOperationException("The command audit must not download application updates.");
    }
}
