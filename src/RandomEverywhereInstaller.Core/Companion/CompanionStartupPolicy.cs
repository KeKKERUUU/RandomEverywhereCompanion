using RandomEverywhereInstaller.Core.Installer;

namespace RandomEverywhereInstaller.Core.Companion;

public enum CompanionStartupDestination { SelectCommunity, InstallPackages, Audio }

/// <summary>Only installed required packages determine the initial destination.</summary>
public static class CompanionStartupPolicy
{
    public static CompanionStartupDestination Resolve(bool communityExists, IReadOnlyCollection<PackageScanResult> inventory)
    {
        if (!communityExists || inventory.Count == 0) return CompanionStartupDestination.SelectCommunity;
        var required = inventory.Where(package => package.Definition.Required).ToArray();
        if (required.Length == 0) return CompanionStartupDestination.SelectCommunity;
        return required.All(package => package.State == PackageState.Installed)
            ? CompanionStartupDestination.Audio
            : CompanionStartupDestination.InstallPackages;
    }
}
