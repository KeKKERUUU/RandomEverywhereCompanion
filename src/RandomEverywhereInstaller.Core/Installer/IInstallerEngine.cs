using RandomEverywhereInstaller.Core.Models;

namespace RandomEverywhereInstaller.Core.Installer;

public interface IInstallerEngine
{
    string LogDirectory { get; }

    Task<string?> DetectCommunityPathAsync(SimulatorType simulator, CancellationToken cancellationToken);

    H145InstallationInfo DetectH145(string communityPath);
    Task<H145InstallationInfo> UpdateH145Async(string communityPath, IProgress<InstallerProgress> progress, CancellationToken cancellationToken);

    AndrewPackageInfo DetectAndrewPackage(string communityPath);

    AndrewUpdateInspection InspectAndrewUpdate(string sourcePath);

    AndrewUpdateInspection SelectNewestCumulativeUpdate(IReadOnlyCollection<string> sourcePaths);

    IReadOnlyList<PackageScanResult> InspectCommunityPackages(
        string communityPath,
        SimulatorType simulator);

    Task<IReadOnlyList<PackageScanResult>> ScanPackagesAsync(
        IReadOnlyCollection<string> archivePaths,
        SimulatorType simulator,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken);

    Task<InstallationResult> InstallAsync(
        InstallationRequest request,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken);

    Task<AndrewUpdateResult> InstallAndrewUpdateAsync(
        AndrewUpdateRequest request,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken);

    Task<int> RefreshMissionFilesAsync(string communityPath, IProgress<InstallerProgress> progress, CancellationToken cancellationToken);

    void OpenLogDirectory();
}
