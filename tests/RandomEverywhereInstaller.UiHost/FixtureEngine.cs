using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Core.Models;

namespace RandomEverywhereInstaller.UiHost;

// No production engine, filesystem mutation, process, network, simulator or audio calls.
internal sealed class FixtureEngine(string community) : IInstallerEngine
{
    public string Outcome { get; set; } = "Success";
    public string LogDirectory => "UI fixture — no log directory is written";
    public Task<string?> DetectCommunityPathAsync(SimulatorType simulator, CancellationToken cancellationToken) => Task.FromResult<string?>(community);
    public H145InstallationInfo DetectH145(string communityPath) => new(true, community, 500, [500], true, community);
    public AndrewPackageInfo DetectAndrewPackage(string communityPath) => new(true, community + "/fixture-primary", false, community + "/fixture-primary/hpgmission/RANDOM AND EVERYWHERE/everywhere_all.json");
    public AndrewUpdateInspection InspectAndrewUpdate(string sourcePath) => new(
        sourcePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? AndrewUpdateKind.Cumulative : AndrewUpdateKind.Stable,
        sourcePath, null, new DetectedPackageRoot("wrapper", "fixture-primary"), new AndrewUpdateVersion("0.99634", "1.3", "UI FIXTURE"));
    public AndrewUpdateInspection SelectNewestCumulativeUpdate(IReadOnlyCollection<string> sourcePaths) => InspectAndrewUpdate(sourcePaths.First());
    public IReadOnlyList<PackageScanResult> InspectCommunityPackages(string communityPath, SimulatorType simulator) => Packages(false);
    public async Task<IReadOnlyList<PackageScanResult>> ScanPackagesAsync(IReadOnlyCollection<string> archivePaths, SimulatorType simulator,
        IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new("Scan", "UI fixture: scanning in-memory package data", "No archive is opened", 20, 0, 13));
        await Task.Delay(400, cancellationToken);
        return Packages(true);
    }
    private static PackageScanResult[] Packages(bool supplied) => Enumerable.Range(1, 13).Select(index =>
        new PackageScanResult(new PackageDefinition("fixture-" + index, "Fixture package " + index, index <= 3, index, ".*",
            new HashSet<SimulatorType> { SimulatorType.Msfs2024 }, string.Empty),
            supplied ? PackageState.Found : index <= 3 ? PackageState.Installed : PackageState.OptionalMissing,
            supplied ? "ui-fixture.zip" : null, [new DetectedPackageRoot("wrapper/root", "fixture-package-" + index)], null)).ToArray();
    private async Task ProgressAsync(IProgress<InstallerProgress> progress, CancellationToken token)
    {
        for (var step = 1; step <= 5; step++)
        {
            await Task.Delay(2000, token);
            progress.Report(new("Fixture", "UI fixture: no files are being installed", Outcome, step * 20, step, 5));
            if (step == 2 && Outcome == "Error") throw new InstallerException("UI-FIXTURE", "Intentional fixture failure. No files were changed.");
            if (step == 2 && Outcome == "Cancelled") throw new OperationCanceledException("Intentional fixture cancellation", token);
        }
    }
    public async Task<InstallationResult> InstallAsync(InstallationRequest request, IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
    {
        await ProgressAsync(progress, cancellationToken);
        return new(true, false, 13, 13, LogDirectory, []);
    }
    public async Task<AndrewUpdateResult> InstallAndrewUpdateAsync(AndrewUpdateRequest request, IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
    {
        await ProgressAsync(progress, cancellationToken);
        return new(true, false, request.Update.Kind, 1, LogDirectory, []);
    }
    public void OpenLogDirectory() { }
    public Task<H145InstallationInfo> UpdateH145Async(string communityPath, IProgress<InstallerProgress> progress, CancellationToken cancellationToken) => throw new NotSupportedException("UI fixture only");
    public Task<int> RefreshMissionFilesAsync(string communityPath, IProgress<InstallerProgress> progress, CancellationToken cancellationToken) => throw new NotSupportedException("UI fixture only");
}
