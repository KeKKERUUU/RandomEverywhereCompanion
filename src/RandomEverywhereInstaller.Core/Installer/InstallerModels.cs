using RandomEverywhereInstaller.Core.Models;

namespace RandomEverywhereInstaller.Core.Installer;

public enum PackageState
{
    Found,
    Installed,
    OptionalMissing,
    RequiredMissing,
    Invalid,
    Ambiguous,
    Incompatible
}

public sealed record PackageDefinition(
    string Id,
    string DisplayName,
    bool Required,
    int InstallOrder,
    string FileNamePattern,
    IReadOnlySet<SimulatorType> Simulators,
    string DownloadUrl);

public sealed record DetectedPackageRoot(string RelativePath, string FolderName);

public sealed record PackageScanResult(
    PackageDefinition Definition,
    PackageState State,
    string? ArchivePath,
    IReadOnlyList<DetectedPackageRoot> Roots,
    string? Detail)
{
    public bool CanInstall => State == PackageState.Found;
    public bool RequirementSatisfied => State is PackageState.Found or PackageState.Installed;
}

public sealed record H145InstallationInfo(
    bool Installed,
    string PackagePath,
    int? Version,
    IReadOnlyList<int> VersionMarkers,
    bool ActionPackInstalled,
    string ActionPackPath)
{
    public bool IsVersion500OrLater => Version >= 500;
}

public enum AndrewUpdateKind
{
    Cumulative,
    Stable
}

public sealed record AndrewUpdateVersion(
    string MissionVersion,
    string DevVersion,
    string? DevType)
{
    public string Display => string.IsNullOrWhiteSpace(DevType)
        ? $"{MissionVersion} {DevVersion}"
        : $"{MissionVersion} {DevVersion} {DevType}";
}

public sealed record AndrewUpdateInspection(
    AndrewUpdateKind Kind,
    string SourcePath,
    string? ArchiveEntryPath,
    DetectedPackageRoot? StablePackageRoot,
    AndrewUpdateVersion? Version);

public sealed record AndrewPackageInfo(
    bool Installed,
    string PackagePath,
    bool CumulativeTargetPresent,
    string CumulativeTargetPath);

public sealed record AndrewUpdateRequest(
    string CommunityPath,
    AndrewUpdateInspection Update);

public sealed record AndrewUpdateResult(
    bool Success,
    bool RolledBack,
    AndrewUpdateKind Kind,
    int UpdatedLayouts,
    string LogDirectory,
    IReadOnlyList<string> Warnings);

public sealed record InstallerProgress(
    string Phase,
    string Message,
    string Detail,
    double OverallPercent,
    long Current,
    long Total);

public sealed record InstallationRequest(
    SimulatorType Simulator,
    string CommunityPath,
    IReadOnlyList<PackageScanResult> Packages,
    bool AllowMissingRequired = false,
    AndrewUpdateInspection? CumulativeUpdate = null);

public sealed record InstallationResult(
    bool Success,
    bool RolledBack,
    int InstalledPackages,
    int UpdatedLayouts,
    string LogDirectory,
    IReadOnlyList<string> Warnings);

public sealed class InstallerException : Exception
{
    public InstallerException(string code, string message) : base(message) => Code = code;
    public InstallerException(string code, string message, Exception innerException) : base(message, innerException) => Code = code;
    public string Code { get; }
}
