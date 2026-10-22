namespace RandomEverywhereInstaller.Core.Installer;

public enum ReviewOperation { Merge, Replace, Cumulative, Metadata }
public sealed record InstallationReviewItem(string Package, string Destination, ReviewOperation Operation);

// Read-only projection of the accepted plan; never performs installation work.
public static class InstallationReview
{
    public static IReadOnlyList<InstallationReviewItem> Build(string community,
        IReadOnlyList<PackageScanResult> packages, AndrewUpdateInspection? update,
        AndrewPackageInfo? installed, bool updateOnly)
    {
        var rows = new List<InstallationReviewItem>();
        if (!updateOnly)
            foreach (var package in packages.Where(item => item.CanInstall).OrderBy(item => item.Definition.InstallOrder))
                foreach (var root in package.Roots)
                    rows.Add(new(package.Definition.DisplayName, Path.Combine(community, root.FolderName), ReviewOperation.Merge));
        if (update is not null)
        {
            var packagePath = installed?.Installed == true ? installed.PackagePath :
                Path.Combine(community, "hpg-airbus-h145-z-andrew-mission-pack");
            var target = update.Kind == AndrewUpdateKind.Cumulative
                ? installed?.CumulativeTargetPath is { Length: > 0 } path ? path :
                    Path.Combine(packagePath, "hpgmission", "RANDOM AND EVERYWHERE", "everywhere_all.json")
                : packagePath;
            rows.Add(new("Random & Everywhere", target,
                update.Kind == AndrewUpdateKind.Cumulative ? ReviewOperation.Cumulative : ReviewOperation.Replace));
        }
        if (rows.Count > 0) rows.Add(new("layout.json / mission index", community, ReviewOperation.Metadata));
        return rows;
    }
}
