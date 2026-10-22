using RandomEverywhereInstaller.Core.Models;

namespace RandomEverywhereInstaller.Core.Installer;

public static class InstallationFlowPolicy
{
    public static bool CanContinueFromArchiveStep(
        InstallerMode mode,
        int inputCount,
        bool exitAvailable) =>
        mode == InstallerMode.FullInstallation || inputCount > 0 || exitAvailable;

    public static bool RequiredPackagesSatisfied(IReadOnlyCollection<PackageScanResult> packages)
    {
        var required = packages.Where(package => package.Definition.Required).ToArray();
        return required.Length > 0 && required.All(package => package.RequirementSatisfied);
    }

    public static bool HasPendingChanges(
        IReadOnlyCollection<PackageScanResult> packages,
        AndrewUpdateInspection? cumulativeUpdate) =>
        packages.Any(package => package.CanInstall) || cumulativeUpdate is not null;

    public static bool ShouldExitBeforeAddingInputs(
        IReadOnlyCollection<PackageScanResult> communityInventory,
        int inputCount) =>
        inputCount == 0 && RequiredPackagesSatisfied(communityInventory);

    public static bool ShouldExitAfterScan(
        IReadOnlyCollection<PackageScanResult> scanResults,
        AndrewUpdateInspection? cumulativeUpdate,
        bool inputsScanned) =>
        inputsScanned &&
        RequiredPackagesSatisfied(scanResults) &&
        !HasPendingChanges(scanResults, cumulativeUpdate);
}
