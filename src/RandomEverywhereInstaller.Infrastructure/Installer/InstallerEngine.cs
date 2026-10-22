using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Core.Models;
using SharpCompress.Archives;

namespace RandomEverywhereInstaller.Infrastructure.Installer;

/// <summary>
/// Implements read-only discovery, transactional package installation, and rollback.
/// </summary>
public sealed partial class InstallerEngine : IInstallerEngine, IDisposable
{
    private const string H145BasePackageFolder = "hpg-airbus-h145";
    private const string H145ActionPackFolder = "hpg-airbus-h145-ap";
    private const string AndrewPackageFolder = "hpg-airbus-h145-z-andrew-mission-pack";
    private static readonly string AndrewCumulativeRelativePath = Path.Combine(
        "hpgmission", "RANDOM AND EVERYWHERE", "everywhere_all.json");

    private readonly HttpMessageHandler _httpHandler;
    private readonly string _runtimeDirectoryName;
    // Approval belongs to the exact inspection object, without extending the public
    // DTO contract. Weak keys avoid retaining abandoned scans for the app lifetime.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AndrewUpdateInspection, string> ApprovedCumulativeHashes = new();
    private InstallerLog? _log;

    public InstallerEngine() : this(CreateDefaultHttpHandler(), "RandomAndEverywhereInstaller")
    {
    }

    public InstallerEngine(string runtimeDirectoryName) : this(CreateDefaultHttpHandler(), runtimeDirectoryName)
    {
    }

    internal InstallerEngine(HttpMessageHandler handler) : this(handler, "RandomAndEverywhereInstaller")
    {
    }

    internal InstallerEngine(HttpMessageHandler handler, string runtimeDirectoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectoryName);
        if (runtimeDirectoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The runtime directory name contains invalid characters.", nameof(runtimeDirectoryName));
        }

        _runtimeDirectoryName = runtimeDirectoryName;
        _httpHandler = handler;
    }

    private static HttpClientHandler CreateDefaultHttpHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        UseCookies = true
    };

    private InstallerLog Log => _log ??= new InstallerLog(_runtimeDirectoryName);

    public string LogDirectory => Log.DirectoryPath;

    // Discovery is read-only and limited to simulator configuration candidates.
    public Task<string?> DetectCommunityPathAsync(SimulatorType simulator, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = GetUserCfgCandidates(simulator);
        foreach (var candidate in candidates.Where(File.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var line = File.ReadLines(candidate).FirstOrDefault(value => value.TrimStart().StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase));
                var match = line is null ? Match.Empty : InstalledPackagesPathRegex().Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var packagesPath = Environment.ExpandEnvironmentVariables(match.Groups[1].Value.Replace("\\\\", "\\"));
                var community = Path.Combine(packagesPath, "Community");
                if (Directory.Exists(community))
                {
                    Log.Info("SIM-FOUND", "Community folder detected.", new { simulator, candidate, community });
                    return Task.FromResult<string?>(Path.GetFullPath(community));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Warning("SIM-CFG-READ", $"Could not read simulator configuration: {candidate}", exception.Message);
            }
        }

        Log.Warning("SIM-002", "Community folder was not detected automatically.", simulator);
        return Task.FromResult<string?>(null);
    }

    public H145InstallationInfo DetectH145(string communityPath)
    {
        var community = ValidateCommunityPath(communityPath, requireWrite: false);
        var packagePath = Path.Combine(community, H145BasePackageFolder);
        var actionPackPath = Path.Combine(community, H145ActionPackFolder);
        var actionPackInstalled = Directory.Exists(actionPackPath);
        if (!Directory.Exists(packagePath))
        {
            return new H145InstallationInfo(false, packagePath, null, [], actionPackInstalled, actionPackPath);
        }

        var markers = Directory.EnumerateFiles(packagePath, "Version_*.txt", SearchOption.TopDirectoryOnly)
            .Select(path => VersionMarkerRegex().Match(Path.GetFileName(path)))
            .Where(match => match.Success && int.TryParse(match.Groups[1].Value, out _))
            .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .ToArray();
        int? version = markers.Length == 0 ? null : markers[^1];
        Log.Info("H145-DETECT", "H145 installation inspected.", new { packagePath, version, markers, actionPackInstalled, actionPackPath });
        return new H145InstallationInfo(true, packagePath, version, markers, actionPackInstalled, actionPackPath);
    }

    public AndrewPackageInfo DetectAndrewPackage(string communityPath)
    {
        var community = ValidateCommunityPath(communityPath, requireWrite: false);
        var packagePath = Path.Combine(community, AndrewPackageFolder);
        var cumulativeTarget = Path.Combine(packagePath, AndrewCumulativeRelativePath);
        var installed = Directory.Exists(packagePath) &&
                        File.Exists(Path.Combine(packagePath, "manifest.json")) &&
                        File.Exists(Path.Combine(packagePath, "layout.json"));
        return new AndrewPackageInfo(installed, packagePath, File.Exists(cumulativeTarget), cumulativeTarget);
    }

    // Update kind is derived from archive structure or cumulative JSON version markers.
    public static AndrewUpdateVersion? ReadInstalledAndrewVersion(AndrewPackageInfo installed)
    {
        if (!installed.Installed || !installed.CumulativeTargetPresent) return null;
        try { return ReadAndrewVersionFile(installed.CumulativeTargetPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InstallerException) { return null; }
    }

    public AndrewUpdateInspection InspectAndrewUpdate(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new InstallerException("ANDREW-001", "The Random & Everywhere update file does not exist.");

        var fullPath = Path.GetFullPath(sourcePath);
        if (Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var input = File.OpenRead(fullPath);
            return InspectCumulativeStream(input, fullPath, null);
        }

        if (!ArchivePackageInspector.IsSupportedFile(fullPath))
            throw new InstallerException("ANDREW-002", "Use a valid cumulative Random & Everywhere JSON file, or a supported archive for a stable update.");

        IReadOnlyList<DetectedPackageRoot> roots;
        try
        {
            roots = ArchivePackageInspector.Inspect(fullPath);
        }
        catch (InstallerException exception) when (exception.Code == "ARCHIVE-007")
        {
            roots = [];
        }

        var matchingStableRoots = roots.Where(root => root.FolderName.Equals(AndrewPackageFolder, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matchingStableRoots.Length > 1)
            throw new InstallerException("ANDREW-002", "Multiple Random & Everywhere package roots were found in the stable update archive.");
        var stableRoot = matchingStableRoots.SingleOrDefault();
        if (stableRoot is null && roots.Count == 1)
        {
            var candidate = roots[0];
            using var probe = ArchivePackageInspector.Open(fullPath);
            var expected = string.IsNullOrEmpty(candidate.RelativePath)
                ? AndrewCumulativeRelativePath.Replace('\\', '/')
                : $"{candidate.RelativePath}/{AndrewCumulativeRelativePath.Replace('\\', '/')}";
            if (probe.Entries.Any(entry => !entry.IsDirectory &&
                    ArchivePackageInspector.ValidateEntry(entry).Equals(expected, StringComparison.OrdinalIgnoreCase)))
            {
                stableRoot = new DetectedPackageRoot(candidate.RelativePath, AndrewPackageFolder);
            }
        }
        if (stableRoot is not null)
        {
            using var stableArchive = ArchivePackageInspector.Open(fullPath);
            var stableJsonPath = string.IsNullOrEmpty(stableRoot.RelativePath)
                ? AndrewCumulativeRelativePath.Replace('\\', '/')
                : $"{stableRoot.RelativePath}/{AndrewCumulativeRelativePath.Replace('\\', '/')}";
            var stableJson = stableArchive.Entries.FirstOrDefault(entry =>
                !entry.IsDirectory &&
                ArchivePackageInspector.ValidateEntry(entry).Equals(stableJsonPath, StringComparison.OrdinalIgnoreCase));
            AndrewUpdateVersion? stableVersion = null;
            if (stableJson is not null)
            {
                using var stableStream = stableJson.OpenEntryStream();
                stableVersion = ReadAndrewVersionStream(stableStream, Path.GetFileName(fullPath));
            }
            return new AndrewUpdateInspection(AndrewUpdateKind.Stable, fullPath, null, stableRoot, stableVersion);
        }

        using var archive = ArchivePackageInspector.Open(fullPath);
        var matches = archive.Entries
            .Where(entry => !entry.IsDirectory)
            .Select(entry => (Entry: entry, Path: ArchivePackageInspector.ValidateEntry(entry)))
            .Where(value => Path.GetFileName(value.Path).Equals("everywhere_all.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
            throw new InstallerException("ANDREW-002", "The update is neither a complete stable Random & Everywhere package nor an unambiguous cumulative everywhere_all.json update.");

        using var stream = matches[0].Entry.OpenEntryStream();
        return InspectCumulativeStream(stream, fullPath, matches[0].Path);
    }

    private static AndrewUpdateInspection InspectCumulativeStream(Stream input, string source, string? entry)
    {
        using var hash = SHA256.Create();
        using var hashingInput = new CryptoStream(input, hash, CryptoStreamMode.Read, leaveOpen: true);
        var version = ReadAndrewVersionStream(hashingInput, Path.GetFileName(source));
        var inspection = new AndrewUpdateInspection(AndrewUpdateKind.Cumulative, source, entry, null, version);
        ApprovedCumulativeHashes.Add(inspection, Convert.ToHexString(hash.Hash!));
        return inspection;
    }

    private static void ValidateStagedCumulativeUpdate(AndrewUpdateInspection update, string stagedJson)
    {
        var version = ReadAndrewVersionFile(stagedJson);
        if (version != update.Version ||
            !ApprovedCumulativeHashes.TryGetValue(update, out var approvedHash) ||
            !ComputeSha256(stagedJson).Equals(approvedHash, StringComparison.Ordinal))
        {
            throw new InstallerException("ANDREW-002", "The cumulative input changed or was not inspected. Inspect and approve it again before installing.");
        }
    }

    public AndrewUpdateInspection SelectNewestCumulativeUpdate(IReadOnlyCollection<string> sourcePaths)
    {
        if (sourcePaths.Count == 0)
            throw new InstallerException("ANDREW-001", "No everywhere_all.json update was provided.");

        var updates = sourcePaths
            .Select(InspectAndrewUpdate)
            .Where(update => update.Kind == AndrewUpdateKind.Cumulative)
            .ToArray();
        if (updates.Length != sourcePaths.Count)
            throw new InstallerException("ANDREW-002", "Only cumulative everywhere_all.json files can be compared.");

        return updates.Aggregate((newest, candidate) =>
            CompareAndrewVersions(
                candidate.Version ?? throw new InstallerException("ANDREW-002", "A cumulative update has no readable version."),
                newest.Version ?? throw new InstallerException("ANDREW-002", "A cumulative update has no readable version.")) > 0
                ? candidate
                : newest);
    }

    // Inventory scans immediate package metadata without mutating unrelated Community data.
    public IReadOnlyList<PackageScanResult> InspectCommunityPackages(
        string communityPath,
        SimulatorType simulator)
    {
        var community = ValidateCommunityPath(communityPath, requireWrite: false);
        var directories = Directory.EnumerateDirectories(community, "*", SearchOption.TopDirectoryOnly)
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Select(CreateCommunityPackageProbe)
            .ToArray();

        var results = PackageCatalog.All
            .Where(definition => definition.Simulators.Contains(simulator))
            .OrderBy(definition => definition.InstallOrder)
            .Select(definition =>
            {
                var matches = directories
                    .Where(probe => IsInstalledPackageMatch(definition.Id, probe))
                    .ToArray();
                if (matches.Length > 0)
                {
                    return new PackageScanResult(
                        definition,
                        PackageState.Installed,
                        null,
                        matches.Select(match => new DetectedPackageRoot(match.FolderName, match.FolderName)).ToArray(),
                        string.Join(", ", matches.Select(match => match.FolderName)));
                }

                return new PackageScanResult(
                    definition,
                    definition.Required ? PackageState.RequiredMissing : PackageState.OptionalMissing,
                    null,
                    [],
                    "Not detected in the selected Community folder");
            })
            .ToArray();

        Log.Info(
            "COMMUNITY-INVENTORY",
            "Existing Community packages inspected before archive selection.",
            results.Select(result => new { result.Definition.Id, result.State, result.Detail }));
        return results;
    }

    // The update is staged, validated, applied transactionally, and verified before commit.
    public Task<IReadOnlyList<PackageScanResult>> ScanPackagesAsync(
        IReadOnlyCollection<string> archivePaths,
        SimulatorType simulator,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken) => Task.Run<IReadOnlyList<PackageScanResult>>(() =>
    {
        if (archivePaths.Count == 0)
        {
            throw new InstallerException("ARCHIVE-001", "No supported archives were added.");
        }

        var archives = archivePaths
            .Select(Path.GetFullPath)
            .Where(ArchivePackageInspector.IsSupportedFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (archives.Length != archivePaths.Count || archives.Any(path => !File.Exists(path)))
            throw new InstallerException("ARCHIVE-001", "One or more added archives no longer exist or use an unsupported format.");

        var definitions = PackageCatalog.All.Where(definition => definition.Simulators.Contains(simulator)).OrderBy(definition => definition.InstallOrder).ToArray();
        var results = new List<PackageScanResult>();
        for (var index = 0; index < definitions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = definitions[index];
            progress.Report(new InstallerProgress("PackageScan", $"Checking {definition.DisplayName}", string.Empty, 100d * index / definitions.Length, index, definitions.Length));
            var matches = archives.Where(path => PackageNameMatcher.IsMatch(definition, path)).ToArray();
            if (matches.Length == 0)
            {
                var state = definition.Required ? PackageState.RequiredMissing : PackageState.OptionalMissing;
                results.Add(new PackageScanResult(definition, state, null, [], "Archive not found"));
                continue;
            }

            if (matches.Length > 1)
            {
                results.Add(new PackageScanResult(definition, PackageState.Ambiguous, null, [], string.Join(Environment.NewLine, matches.Select(Path.GetFileName))));
                continue;
            }

            try
            {
                var roots = ArchivePackageInspector.Inspect(matches[0]);
                results.Add(new PackageScanResult(definition, PackageState.Found, matches[0], roots, $"{roots.Count} package root(s)"));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or InstallerException)
            {
                Log.Error("ZIP-INVALID", $"Package archive is invalid: {matches[0]}", exception.ToString());
                results.Add(new PackageScanResult(definition, PackageState.Invalid, matches[0], [], exception.Message));
            }
        }

        Log.Info("PKG-SCAN", "Package scan completed.", results.Select(result => new { result.Definition.Id, result.State, result.ArchivePath }));
        progress.Report(new InstallerProgress("PackageScan", "Package scan completed", string.Empty, 100, definitions.Length, definitions.Length));
        return results;
    }, cancellationToken);

    // Add-on installation preserves original archives and journals every destination write.
    public async Task<InstallationResult> InstallAsync(
        InstallationRequest request,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken)
    {
        var community = ValidateCommunityPath(request.CommunityPath, requireWrite: true);
        if (request.Packages.Where(package => package.ArchivePath is not null)
            .Select(package => Path.GetFullPath(package.ArchivePath!))
            .Any(archive => IsSameOrNested(archive, community)))
        {
            throw new InstallerException("PATH-004", "ZIP archives cannot be loaded from inside the Community folder.");
        }
        if (request.CumulativeUpdate is not null)
        {
            var cumulativeSource = Path.GetFullPath(request.CumulativeUpdate.SourcePath);
            if (!File.Exists(cumulativeSource))
                throw new InstallerException("ANDREW-001", "The selected everywhere_all.json file no longer exists.");
            if (IsSameOrNested(cumulativeSource, community))
                throw new InstallerException("PATH-004", "The cumulative update cannot be loaded from inside the Community folder.");
            if (request.CumulativeUpdate.Kind != AndrewUpdateKind.Cumulative)
                throw new InstallerException("ANDREW-002", "Only a cumulative everywhere_all.json update can accompany a full installation.");
        }

        EnsureSimulatorClosed();
        var invalidRequired = request.Packages.Where(package =>
            package.Definition.Required &&
            !package.RequirementSatisfied).ToArray();
        if (invalidRequired.Length > 0 && !request.AllowMissingRequired)
        {
            throw new InstallerException("PKG-001", $"Required packages are missing or invalid: {string.Join(", ", invalidRequired.Select(value => value.Definition.DisplayName))}");
        }

        var selected = request.Packages.Where(package => package.CanInstall).OrderBy(package => package.Definition.InstallOrder).ToArray();
        if (selected.Length == 0 && request.CumulativeUpdate is null)
            throw new InstallerException("PKG-001", "No valid package is available to install.");
        if (request.CumulativeUpdate is not null)
        {
            var primaryWillBeAvailable =
                DetectAndrewPackage(community).Installed ||
                selected.Any(package =>
                    package.Definition.Id.Equals("ANDREW_MISSION_PACK", StringComparison.OrdinalIgnoreCase));
            if (!primaryWillBeAvailable)
            {
                throw new InstallerException(
                    "ANDREW-003",
                    $"The cumulative update requires the primary Random & Everywhere package: {Path.Combine(community, AndrewPackageFolder)}");
            }
        }
        var affectedPackageFolders = selected
            .SelectMany(package => package.Roots)
            .Select(root => root.FolderName)
            .Concat(request.CumulativeUpdate is null ? [] : [AndrewPackageFolder])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (affectedPackageFolders.Any(folder =>
                folder.Equals(H145BasePackageFolder, StringComparison.OrdinalIgnoreCase) ||
                folder.Equals(H145ActionPackFolder, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InstallerException(
                "PATH-003",
                "Random & Everywhere package installation cannot modify the H145 Base Pack or Action Pack folders.");
        }
        if (invalidRequired.Length > 0)
            Log.Warning("PKG-OVERRIDE", "User explicitly chose to continue without all required packages.", invalidRequired.Select(package => new { package.Definition.Id, package.State }));
        if (selected.Length > 0)
            EnsureDiskSpace(community, selected);
        if (request.CumulativeUpdate is not null)
            EnsureAndrewUpdateDiskSpace(community, request.CumulativeUpdate);
        var session = CreateSession("installation", community);
        var transaction = new InstallTransaction(community, session.Backup, Log);
        var warnings = new List<string>();
        if (invalidRequired.Length > 0)
            warnings.Add($"Continued without required packages: {string.Join(", ", invalidRequired.Select(package => package.Definition.DisplayName))}");
        var layoutBackups = new List<FileRollbackState>();
        FileRollbackState? missionIndexBackup = null;
        var layoutScope = GetMissionLayoutPackageFolders(community).Concat(affectedPackageFolders).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        int updatedLayouts;

        try
        {
            // Copy once, then validate those exact bytes before any package mutation.
            var stagedAndrew = Path.Combine(session.Root, "Cumulative", AndrewPackageFolder);
            if (request.CumulativeUpdate is not null)
            {
                var stagedJson = Path.Combine(stagedAndrew, AndrewCumulativeRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedJson)!);
                await ExtractCumulativeUpdateAsync(request.CumulativeUpdate, stagedJson, cancellationToken).ConfigureAwait(false);
                ValidateStagedCumulativeUpdate(request.CumulativeUpdate, stagedJson);
            }
            BackupLayouts(community, layoutScope, Path.Combine(session.Backup, "Layouts"), layoutBackups);
            await InstallPackagesAsync(selected, session.Staging, transaction, progress, 0, 75, cancellationToken).ConfigureAwait(false);
            if (request.CumulativeUpdate is not null)
            {
                var installedAndrew = DetectAndrewPackage(community);
                if (!installedAndrew.Installed)
                {
                    throw new InstallerException(
                        "ANDREW-003",
                        $"everywhere_all.json requires the primary Random & Everywhere package: {installedAndrew.PackagePath}");
                }

                var incomingVersion = request.CumulativeUpdate.Version
                    ?? throw new InstallerException("ANDREW-002", "The cumulative update version could not be determined.");
                var shouldInstall = true;
                if (installedAndrew.CumulativeTargetPresent)
                {
                    try
                    {
                        var installedVersion = ReadAndrewVersionFile(installedAndrew.CumulativeTargetPath);
                        shouldInstall = CompareAndrewVersions(incomingVersion, installedVersion) > 0;
                        if (!shouldInstall)
                        {
                            warnings.Add(
                                $"The added everywhere_all.json ({incomingVersion.Display}) was not newer than the package version ({installedVersion.Display}) and was skipped.");
                        }
                    }
                    catch (InstallerException exception) when (exception.Code == "ANDREW-002")
                    {
                        Log.Warning(
                            "ANDREW-VERSION-UNKNOWN",
                            "The package everywhere_all.json version could not be read; the validated standalone update will replace it.",
                            exception.Message);
                    }
                }

                if (shouldInstall)
                {
                    progress.Report(new InstallerProgress(
                        "AndrewUpdate",
                        "Applying the newest everywhere_all.json",
                        incomingVersion.Display,
                        75,
                        0,
                        1));
                    transaction.MergePackage(stagedAndrew, AndrewPackageFolder);
                }
            }
            updatedLayouts = RunBuiltInLayoutGenerator(community, progress, cancellationToken, scopedPackageFolders: layoutScope);

            missionIndexBackup = BackupMissionIndex(community, Path.Combine(session.Backup, "MissionIndex"));
            await RunMissionIndexAsync(community, progress, cancellationToken).ConfigureAwait(false);
            VerifyInstalledPackages(community, selected);
            warnings.AddRange(transaction.Warnings);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch (Exception exception)
        {
            Log.Error("INST-FAIL", "Installation failed; rollback started.", exception.ToString());
            try
            {
                transaction.Rollback();
                RestoreLayouts(community, layoutBackups);
                RestoreFileState(community, missionIndexBackup);
                CleanupSession(session);
            }
            catch (Exception rollbackException)
            {
                Log.Error("INST-009", "Rollback failed.", rollbackException.ToString());
                throw new InstallerException("INST-009", $"Installation failed and rollback was incomplete. Logs: {Log.DirectoryPath}", new AggregateException(exception, rollbackException));
            }

            if (exception is OperationCanceledException) throw;
            throw exception is InstallerException ? exception : new InstallerException("INST-010", $"Installation failed and was rolled back: {exception.Message}", exception);
        }

        FinishCommittedOperation(session, progress,
            new InstallerProgress("Completed", "Installation completed successfully", Log.DirectoryPath, 100, 1, 1), warnings);
        Log.Success("INST-COMPLETE", "Installation completed successfully.", new { selected = selected.Length, updatedLayouts, warnings });
        return new InstallationResult(true, false, selected.Length, updatedLayouts, Log.DirectoryPath, warnings);
    }

    // Stable updates replace one package; cumulative updates replace only the target JSON.
    public async Task<AndrewUpdateResult> InstallAndrewUpdateAsync(
        AndrewUpdateRequest request,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken)
    {
        var community = ValidateCommunityPath(request.CommunityPath, requireWrite: true);
        var source = Path.GetFullPath(request.Update.SourcePath);
        if (!File.Exists(source))
            throw new InstallerException("ANDREW-001", "The selected Random & Everywhere update no longer exists.");
        if (IsSameOrNested(source, community))
            throw new InstallerException("PATH-004", "The update source cannot be loaded from inside the Community folder.");

        EnsureSimulatorClosed();
        var installed = DetectAndrewPackage(community);
        if (!installed.Installed)
            throw new InstallerException("ANDREW-003", $"The primary Random & Everywhere package is required before applying an update: {installed.PackagePath}");
        if (request.Update.Kind == AndrewUpdateKind.Cumulative && installed.CumulativeTargetPresent)
        {
            var incomingVersion = request.Update.Version
                ?? throw new InstallerException("ANDREW-002", "The cumulative update version could not be determined.");
            try
            {
                var installedVersion = ReadAndrewVersionFile(installed.CumulativeTargetPath);
                if (CompareAndrewVersions(incomingVersion, installedVersion) <= 0)
                {
                    throw new InstallerException(
                        "ANDREW-006",
                        $"The selected cumulative update ({incomingVersion.Display}) is not newer than the installed file ({installedVersion.Display}).");
                }
            }
            catch (InstallerException exception) when (exception.Code == "ANDREW-002")
            {
                Log.Warning(
                    "ANDREW-VERSION-UNKNOWN",
                    "The installed everywhere_all.json version could not be read; the validated incoming update will replace it.",
                    exception.Message);
            }
        }

        EnsureAndrewUpdateDiskSpace(community, request.Update);
        var session = CreateSession("andrew-update", community);
        var transaction = new InstallTransaction(community, session.Backup, Log);
        var layoutBackups = new List<FileRollbackState>();
        FileRollbackState? missionIndexBackup = null;
        var warnings = new List<string>();
        int updatedLayouts;
        try
        {
            BackupLayouts(community, GetMissionLayoutPackageFolders(community), Path.Combine(session.Backup, "Layouts"), layoutBackups);
            if (request.Update.Kind == AndrewUpdateKind.Cumulative)
            {
                progress.Report(new InstallerProgress("AndrewUpdate", "Preparing cumulative Random & Everywhere update", Path.GetFileName(source), 20, 0, 1));
                var stagedPackage = Path.Combine(session.Staging, AndrewPackageFolder);
                var stagedJson = Path.Combine(stagedPackage, AndrewCumulativeRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedJson)!);
                await ExtractCumulativeUpdateAsync(request.Update, stagedJson, cancellationToken).ConfigureAwait(false);
                ValidateStagedCumulativeUpdate(request.Update, stagedJson);
                transaction.MergePackage(stagedPackage, AndrewPackageFolder);
            }
            else
            {
                var root = request.Update.StablePackageRoot
                    ?? throw new InstallerException("ANDREW-002", "The stable update package root is missing.");
                progress.Report(new InstallerProgress("AndrewUpdate", "Extracting stable Random & Everywhere update", Path.GetFileName(source), 20, 0, 1));
                var stagedPackage = Path.Combine(session.Staging, AndrewPackageFolder);
                await ExtractPackageRootAsync(source, root, stagedPackage, cancellationToken).ConfigureAwait(false);
                VerifyPackageDirectory(stagedPackage, AndrewPackageFolder);
                transaction.ReplacePackage(stagedPackage, AndrewPackageFolder);
            }

            progress.Report(new InstallerProgress("AndrewUpdate", "Random & Everywhere update installed", request.Update.Kind.ToString(), 72, 1, 1));
            updatedLayouts = RunBuiltInLayoutGenerator(community, progress, cancellationToken);

            missionIndexBackup = BackupMissionIndex(community, Path.Combine(session.Backup, "MissionIndex"));
            await RunMissionIndexAsync(community, progress, cancellationToken).ConfigureAwait(false);
            VerifyPackageDirectory(installed.PackagePath, AndrewPackageFolder);
            if (request.Update.Kind == AndrewUpdateKind.Cumulative)
                ValidateJsonFile(installed.CumulativeTargetPath);

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch (Exception exception)
        {
            Log.Error("ANDREW-UPDATE-FAIL", "Random & Everywhere update failed; rollback started.", exception.ToString());
            try
            {
                transaction.Rollback();
                RestoreLayouts(community, layoutBackups);
                RestoreFileState(community, missionIndexBackup);
                CleanupSession(session);
            }
            catch (Exception rollbackException)
            {
                Log.Error("ANDREW-ROLLBACK", "Random & Everywhere rollback failed.", rollbackException.ToString());
                throw new InstallerException("ANDREW-006", $"The update failed and rollback was incomplete. Logs: {Log.DirectoryPath}", new AggregateException(exception, rollbackException));
            }

            if (exception is OperationCanceledException) throw;
            throw exception is InstallerException
                ? exception
                : new InstallerException("ANDREW-005", $"The update failed and was rolled back: {exception.Message}", exception);
        }

        FinishCommittedOperation(session, progress,
            new InstallerProgress("Completed", "Random & Everywhere update completed successfully", request.Update.Kind.ToString(), 100, 1, 1), warnings);
        Log.Success("ANDREW-UPDATE", "Random & Everywhere update completed successfully.", new { request.Update.Kind, source, updatedLayouts, warnings });
        return new AndrewUpdateResult(true, false, request.Update.Kind, updatedLayouts, Log.DirectoryPath, warnings);
    }

    public void OpenLogDirectory()
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo { FileName = LogDirectory, UseShellExecute = true });
    }

    public async Task<int> RefreshMissionFilesAsync(string communityPath, IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
    {
        var community = ValidateCommunityPath(communityPath, requireWrite: true);
        EnsureSimulatorClosed();
        var session = CreateSession("maintenance", community);
        var layoutBackups = new List<FileRollbackState>();
        FileRollbackState? indexBackup = null;
        int count;
        try
        {
            BackupLayouts(community, GetMissionLayoutPackageFolders(community), Path.Combine(session.Backup, "Layouts"), layoutBackups);
            indexBackup = BackupMissionIndex(community, Path.Combine(session.Backup, "MissionIndex"));
            count = RunBuiltInLayoutGenerator(community, progress, cancellationToken, 0, 90);
            await RunMissionIndexAsync(community, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            RestoreLayouts(community, layoutBackups);
            RestoreFileState(community, indexBackup);
            CleanupSession(session);
            throw;
        }
        FinishCommittedOperation(session);
        return count;
    }

    public void Dispose() => _httpHandler.Dispose();

    private static async Task InstallPackagesAsync(
        PackageScanResult[] packages,
        string stagingRoot,
        InstallTransaction transaction,
        IProgress<InstallerProgress> progress,
        double startPercent,
        double endPercent,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingRoot);
        for (var packageIndex = 0; packageIndex < packages.Length; packageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = packages[packageIndex];
            var archivePath = package.ArchivePath ?? throw new InstallerException("PKG-006", $"Archive path missing for {package.Definition.DisplayName}.");
            var packageStage = Path.Combine(stagingRoot, package.Definition.Id);
            Directory.CreateDirectory(packageStage);
            progress.Report(new InstallerProgress("Extraction", $"Extracting {package.Definition.DisplayName}", Path.GetFileName(archivePath), Lerp(startPercent, endPercent, (double)packageIndex / Math.Max(1, packages.Length)), packageIndex, packages.Length));

            using var archive = ArchivePackageInspector.Open(archivePath);
            foreach (var root in package.Roots)
            {
                var stagedPackage = Path.Combine(packageStage, root.FolderName);
                Directory.CreateDirectory(stagedPackage);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var normalized = ArchivePackageInspector.ValidateEntry(entry);
                    if (string.IsNullOrEmpty(normalized))
                    {
                        continue;
                    }

                    string relative;
                    if (string.IsNullOrEmpty(root.RelativePath))
                    {
                        relative = normalized;
                    }
                    else if (normalized.StartsWith(root.RelativePath + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        relative = normalized[(root.RelativePath.Length + 1)..];
                    }
                    else
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(relative))
                    {
                        continue;
                    }

                    var target = GetSafeChildPath(stagedPackage, relative);
                    if (entry.IsDirectory)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var input = entry.OpenEntryStream();
                    await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(output, 1024 * 128, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                transaction.MergePackage(stagedPackage, root.FolderName);
            }
        }
    }

    private static async Task ExtractPackageRootAsync(
        string archivePath,
        DetectedPackageRoot root,
        string stagedPackage,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagedPackage);
        using var archive = ArchivePackageInspector.Open(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = ArchivePackageInspector.ValidateEntry(entry);
            if (string.IsNullOrEmpty(normalized)) continue;

            string relative;
            if (string.IsNullOrEmpty(root.RelativePath))
            {
                relative = normalized;
            }
            else if (normalized.StartsWith(root.RelativePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                relative = normalized[(root.RelativePath.Length + 1)..];
            }
            else
            {
                continue;
            }

            if (string.IsNullOrEmpty(relative)) continue;
            var target = GetSafeChildPath(stagedPackage, relative);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.OpenEntryStream();
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 1024 * 128, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExtractCumulativeUpdateAsync(
        AndrewUpdateInspection update,
        string destination,
        CancellationToken cancellationToken)
    {
        if (update.ArchiveEntryPath is null)
        {
            await using var input = new FileStream(update.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var archive = ArchivePackageInspector.Open(update.SourcePath);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            !candidate.IsDirectory &&
            ArchivePackageInspector.ValidateEntry(candidate).Equals(update.ArchiveEntryPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new InstallerException("ANDREW-001", $"The cumulative update entry is missing: {update.ArchiveEntryPath}");

        await using var archiveInput = entry.OpenEntryStream();
        await using var fileOutput = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await archiveInput.CopyToAsync(fileOutput, 64 * 1024, cancellationToken).ConfigureAwait(false);
        await fileOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private int RunBuiltInLayoutGenerator(
        string community,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken,
        double startPercent = 78,
        double endPercent = 90,
        IReadOnlyCollection<string>? requiredPackageFolders = null,
        IReadOnlyCollection<string>? scopedPackageFolders = null,
        bool createMissingLayouts = false)
    {
        var packageFolders = GetCommunityLayoutPackageFolders(community);
        var requiredFolders = requiredPackageFolders?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var selectedFolders = scopedPackageFolders?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? GetMissionLayoutPackageFolders(community);
        var missingRequiredLayouts = requiredFolders
            .Where(required => !packageFolders.Contains(required, StringComparer.OrdinalIgnoreCase) &&
                !(createMissingLayouts && File.Exists(Path.Combine(GetSafeCommunityPackagePath(community, required), "manifest.json"))))
            .ToArray();
        Log.Info("LAYOUT-SCAN", "Community layout discovery completed.", new
        {
            community,
            discoveredCount = packageFolders.Length,
            packageFolders,
            requiredFolders,
            selectedFolders,
            missingRequiredLayouts
        });
        if (missingRequiredLayouts.Length > 0)
        {
            throw new InstallerException(
                "LAYOUT-007",
                $"Required package layout.json files were not found after the update: {string.Join(", ", missingRequiredLayouts)}");
        }
        var layouts = selectedFolders
            .Select(folder => GetSafeCommunityPackagePath(community, folder))
            .Select(directory =>
            {
                EnsureNoReparsePointsInsideRoot(community, directory);
                return Path.Combine(directory, "layout.json");
            })
            .Where(layout => File.Exists(layout) || createMissingLayouts && File.Exists(Path.Combine(Path.GetDirectoryName(layout)!, "manifest.json")))
            .Where(layout =>
            {
                EnsureNoReparsePointsInsideRoot(community, layout);
                return true;
            })
            .ToArray();

        var processed = 0;
        foreach (var layout in layouts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = CaptureLayoutDiagnostic(layout);
            Log.Info("LAYOUT-PACKAGE-START", "Regenerating package layout.json.", before);
            GenerateLayoutFile(layout);
            if (!TryValidateGeneratedLayout(layout, out var reason))
            {
                Log.Error("LAYOUT-PACKAGE-INVALID", "Generated layout.json did not pass post-write verification.", new
                {
                    before,
                    validationReason = reason
                });
                throw new InstallerException(
                    "LAYOUT-006",
                    $"layout.json verification failed after regeneration: {layout}. {reason}");
            }

            var after = CaptureLayoutDiagnostic(layout);
            Log.Success("LAYOUT-PACKAGE-COMPLETE", "Package layout.json was regenerated and verified.", new
            {
                before,
                after,
                hashChanged = !string.Equals(before.LayoutSha256, after.LayoutSha256, StringComparison.OrdinalIgnoreCase),
                writeTimeAdvanced = after.LayoutLastWriteUtc > before.LayoutLastWriteUtc
            });
            processed++;
            var overall = startPercent + (endPercent - startPercent) * processed / Math.Max(1, layouts.Length);
            progress.Report(new InstallerProgress("Layouts", "Updating layout.json files", $"{processed} / {layouts.Length}", overall, processed, layouts.Length));
        }

        Log.Success("LAYOUT-COMPLETE", "Selected layout files were regenerated and verified by the built-in generator.", new
        {
            processed,
            scope = scopedPackageFolders is null ? "AllCommunityPackages" : "SelectedPackages",
            selectedFolders
        });
        return processed;
    }

    // Native indexing is awaited before commit/rollback; no child process can outlive it.
    private Task RunMissionIndexAsync(string community, IProgress<InstallerProgress> progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            progress.Report(new InstallerProgress("MissionIndex", "Building mission index", community, 94, 0, 1));
            var count = MissionIndexGenerator.Generate(community, cancellationToken, (file, completed) =>
                progress.Report(new InstallerProgress("MissionIndex", "Indexing mission JSON", Path.GetRelativePath(community, file), 96, completed, completed)));
            Log.Success("INDEX-COMPLETE", "Mission index generated by the built-in C# generator.", new { community, missions = count });
            progress.Report(new InstallerProgress("MissionIndex", "Mission index updated", $"{count} missions", 98, count, count));
        }, cancellationToken);

    private static string ValidateCommunityPath(string path, bool requireWrite)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InstallerException("PATH-002", "Community folder is not selected.");
        }

        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(full))
        {
            throw new InstallerException("PATH-002", $"Community folder does not exist: {full}");
        }

        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar);
        // SpecialFolder.Windows is empty on platforms that have no Windows directory.
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || windows.Length > 0 && IsSameOrNested(full, windows))
        {
            throw new InstallerException("PATH-003", $"Protected destination path selected: {full}");
        }

        if (requireWrite)
        {
            var probe = Path.Combine(full, $".rae-write-test-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(probe, "write-test");
                File.Delete(probe);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InstallerException("PATH-005", $"Community folder is not writable: {full}", exception);
            }
        }

        return full;
    }

    private static void EnsureSimulatorClosed()
    {
        var names = new[] { "FlightSimulator", "FlightSimulator2024", "MicrosoftFlightSimulator" };
        if (names.Any(name => Process.GetProcessesByName(name).Length > 0))
        {
            throw new InstallerException("SIM-004", "Microsoft Flight Simulator is running. Close it before continuing.");
        }
    }

    private static void EnsureDiskSpace(string community, IReadOnlyList<PackageScanResult> packages)
    {
        var archiveBytes = packages.Where(package => package.ArchivePath is not null).Sum(package => new FileInfo(package.ArchivePath!).Length);
        var required = Math.Max(1024L * 1024 * 1024, archiveBytes * 4);
        var root = Path.GetPathRoot(community) ?? throw new InstallerException("PATH-008", "Unable to determine destination drive.");
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < required)
        {
            throw new InstallerException("PATH-008", $"Insufficient free space. Required safety budget: {required / 1024 / 1024} MB.");
        }
    }

    private static void EnsureAndrewUpdateDiskSpace(string community, AndrewUpdateInspection update)
    {
        var sourceBytes = new FileInfo(update.SourcePath).Length;
        var required = update.Kind == AndrewUpdateKind.Stable
            ? Math.Max(512L * 1024 * 1024, checked(sourceBytes * 3))
            : Math.Max(64L * 1024 * 1024, checked(sourceBytes * 4));
        var root = Path.GetPathRoot(community) ?? throw new InstallerException("PATH-008", "Unable to determine the Community drive.");
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < required)
        {
            throw new InstallerException(
                "PATH-008",
                $"Insufficient free space before the Random & Everywhere update. Drive {drive.Name} has {drive.AvailableFreeSpace / 1024 / 1024} MB available; " +
                $"the update requires a {required / 1024 / 1024} MB safety budget.");
        }
    }

    private SessionPaths CreateSession(string purpose, string community, bool allowSystemDriveFallback = true)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var name = $"{timestamp}-{purpose}-{Guid.NewGuid():N}";
        var communityParent = Directory.GetParent(Path.GetFullPath(community))?.FullName ?? Path.GetFullPath(community);
        var root = Path.Combine(communityParent, $".{_runtimeDirectoryName}", "Sessions", name);
        SessionPaths result;
        try
        {
            result = CreateSessionDirectories(root);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            root = Path.Combine(community, $".{_runtimeDirectoryName}", "Sessions", name);
            try
            {
                result = CreateSessionDirectories(root);
            }
            catch (Exception communityException) when (communityException is UnauthorizedAccessException or IOException)
            {
                if (!allowSystemDriveFallback)
                    throw new InstallerException("PATH-008", "Unable to create update staging on the Community drive. No download was started.", communityException);
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    _runtimeDirectoryName,
                    "Sessions",
                    name);
                result = CreateSessionDirectories(root);
            }
        }
        return result;
    }

    // This runs only after verification/commit has left the rollback boundary. Diagnostics
    // or UI observers cannot undo committed files or send the operation back into rollback.
    private void FinishCommittedOperation(SessionPaths session, IProgress<InstallerProgress>? progress = null,
        InstallerProgress? completion = null, ICollection<string>? warnings = null)
    {
        RunPostCommitAction(() => CleanupSession(session), "Temporary session cleanup failed after commit", warnings);
        if (progress is not null && completion is not null)
            RunPostCommitAction(() => progress.Report(completion), "Completion notification failed after commit", warnings);
    }

    private void RunPostCommitAction(Action action, string description, ICollection<string>? warnings)
    {
        try { action(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var warning = description + "; the committed files were kept. " + error.Message;
            warnings?.Add(warning);
            Log.Warning("POST-COMMIT-WARNING", warning, CreateExceptionDiagnostic(error));
        }
    }

    private void CleanupSession(SessionPaths session)
    {
        if (!Directory.Exists(session.Root)) return;
        var full = Path.GetFullPath(session.Root).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Directory.GetParent(full);
        if (parent is null || !parent.Name.Equals("Sessions", StringComparison.OrdinalIgnoreCase) ||
            parent.Parent is null || !parent.Parent.Name.Equals($".{_runtimeDirectoryName}", StringComparison.OrdinalIgnoreCase) &&
            !parent.Parent.Name.Equals(_runtimeDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerException("PATH-003", $"Refusing to clean an unexpected runtime path: {full}");
        }
        try
        {
            Directory.Delete(full, recursive: true);
            DeleteDirectoryIfEmpty(parent.FullName);
            DeleteDirectoryIfEmpty(parent.Parent.FullName);
            Log.Info("SESSION-CLEAN", "Temporary downloads, staging files, and rollback backup were removed.", full);
        }
        catch (IOException exception)
        {
            // Cleanup is best-effort after a successful commit/rollback. A locked temporary
            // file must not turn an otherwise successful installation into a failure.
            Log.Warning("SESSION-CLEAN-PENDING", "Temporary session cleanup was deferred because a file is locked.", new { full, exception.Message });
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.Warning("SESSION-CLEAN-PENDING", "Temporary session cleanup was deferred because access was denied.", new { full, exception.Message });
        }
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static SessionPaths CreateSessionDirectories(string root)
    {
        var result = new SessionPaths(
            root,
            Path.Combine(root, "Downloads"),
            Path.Combine(root, "Staging"),
            Path.Combine(root, "Backup"));
        Directory.CreateDirectory(result.Downloads);
        Directory.CreateDirectory(result.Staging);
        Directory.CreateDirectory(result.Backup);
        return result;
    }

    private static IEnumerable<string> GetUserCfgCandidates(SimulatorType simulator)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (simulator == SimulatorType.Msfs2020)
        {
            yield return Path.Combine(appData, "Microsoft Flight Simulator", "UserCfg.opt");
            yield return Path.Combine(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt");
        }
        else
        {
            yield return Path.Combine(appData, "Microsoft Flight Simulator 2024", "UserCfg.opt");
            yield return Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt");
        }
    }

    private static string GetSafeChildPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerException("ZIP-004", $"Resolved extraction path escaped staging: {relative}");
        }
        return target;
    }

    private static bool IsSameOrNested(string candidate, string parent)
    {
        var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        return fullCandidate.Equals(fullParent, StringComparison.OrdinalIgnoreCase) ||
               fullCandidate.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }


    internal static void GenerateLayoutFile(string layoutPath)
    {
        var fullLayoutPath = Path.GetFullPath(layoutPath);
        if (!Path.GetFileName(fullLayoutPath).Equals("layout.json", StringComparison.OrdinalIgnoreCase))
            throw new InstallerException("LAYOUT-006", $"The target file is not named layout.json: {fullLayoutPath}");

        var packageDirectory = Path.GetDirectoryName(fullLayoutPath)
            ?? throw new InstallerException("LAYOUT-006", $"Unable to determine the package folder: {fullLayoutPath}");
        if (!Directory.Exists(packageDirectory))
            throw new InstallerException("LAYOUT-006", $"Package folder does not exist: {packageDirectory}");

        var entries = EnumerateLayoutContent(packageDirectory)
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var serializableEntries = entries.Select(entry => new Dictionary<string, object>
        {
            ["path"] = entry.Path,
            ["size"] = entry.Size,
            ["date"] = entry.Date
        }).ToArray();
        var document = new Dictionary<string, object> { ["content"] = serializableEntries };
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var json = JsonSerializer.Serialize(document, options).Replace("\r\n", "\n", StringComparison.Ordinal);
        var temporary = fullLayoutPath + ".rae-tmp";
        TryDelete(temporary);
        try
        {
            File.WriteAllText(temporary, json, new System.Text.UTF8Encoding(false));
            File.Move(temporary, fullLayoutPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }

        UpdateManifestTotalPackageSize(packageDirectory, entries, new FileInfo(fullLayoutPath).Length, options);
    }

    internal static bool TryValidateGeneratedLayout(string layoutPath, out string reason)
    {
        try
        {
            var fullLayoutPath = Path.GetFullPath(layoutPath);
            var packageDirectory = Path.GetDirectoryName(fullLayoutPath)
                ?? throw new IOException("The package directory could not be determined.");
            var expected = EnumerateLayoutContent(packageDirectory)
                .ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
            using var stream = new FileStream(fullLayoutPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            if (!document.RootElement.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                reason = "The content array is missing.";
                return false;
            }

            var actual = new Dictionary<string, LayoutContentEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("path", out var pathElement) ||
                    pathElement.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("size", out var sizeElement) ||
                    !sizeElement.TryGetInt64(out var size) ||
                    !item.TryGetProperty("date", out var dateElement) ||
                    !dateElement.TryGetInt64(out var date))
                {
                    reason = "At least one content entry is malformed.";
                    return false;
                }

                var path = pathElement.GetString()?.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(path) || !actual.TryAdd(path, new LayoutContentEntry(path, size, date)))
                {
                    reason = $"A content path is empty or duplicated: {path}";
                    return false;
                }
            }

            if (actual.Count != expected.Count)
            {
                reason = $"Entry count mismatch. Expected {expected.Count}, found {actual.Count}.";
                return false;
            }

            foreach (var expectedEntry in expected.Values)
            {
                if (!actual.TryGetValue(expectedEntry.Path, out var actualEntry))
                {
                    reason = $"Missing file entry: {expectedEntry.Path}";
                    return false;
                }
                if (actualEntry.Size != expectedEntry.Size)
                {
                    reason = $"Incorrect size for {expectedEntry.Path}. Expected {expectedEntry.Size}, found {actualEntry.Size}.";
                    return false;
                }
                if (actualEntry.Date != expectedEntry.Date)
                {
                    reason = $"Incorrect timestamp for {expectedEntry.Path}.";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InstallerException)
        {
            reason = exception.Message;
            return false;
        }
    }

    private static List<LayoutContentEntry> EnumerateLayoutContent(string packageDirectory)
    {
        var root = Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var entries = new List<LayoutContentEntry>();
        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InstallerException("PATH-007", $"Reparse point detected while generating layout.json: {directory}");
                pending.Enqueue(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InstallerException("PATH-007", $"Reparse point detected while generating layout.json: {file}");
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (ShouldExcludeFromLayout(relative)) continue;
                var info = new FileInfo(file);
                entries.Add(new LayoutContentEntry(relative, info.Length, info.LastWriteTimeUtc.ToFileTimeUtc()));
            }
        }
        return entries;
    }

    private static bool ShouldExcludeFromLayout(string relativePath) =>
        relativePath.StartsWith("_CVT_", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Equals("layout.json", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Equals("MSFSLayoutGenerator.exe", StringComparison.OrdinalIgnoreCase) ||
        relativePath.Equals("layout.json.rae-tmp", StringComparison.OrdinalIgnoreCase);

    private static void UpdateManifestTotalPackageSize(
        string packageDirectory,
        IReadOnlyCollection<LayoutContentEntry> entries,
        long layoutBytes,
        JsonSerializerOptions options)
    {
        var manifestPath = Path.Combine(packageDirectory, "manifest.json");
        if (!File.Exists(manifestPath)) return;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath));
            if (node is not System.Text.Json.Nodes.JsonObject manifest ||
                !manifest.ContainsKey("total_package_size"))
            {
                return;
            }

            // The 20-character field fits every nonnegative Int64 total, so its
            // UTF-8 serialized length is identical before and after substitution.
            manifest["total_package_size"] = new string('0', 20);
            var manifestBytes = System.Text.Encoding.UTF8.GetByteCount(manifest.ToJsonString(options));
            var total = checked(entries.Sum(entry => entry.Size) + manifestBytes + layoutBytes);
            manifest["total_package_size"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(20, '0');
            var json = manifest.ToJsonString(options);
            File.WriteAllText(manifestPath, json, new System.Text.UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or OverflowException)
        {
            // A malformed optional manifest size field must not invalidate an otherwise
            // correct layout.json. Package validation reports manifest problems separately.
        }
    }

    private static LayoutDiagnosticSnapshot CaptureLayoutDiagnostic(string layoutPath)
    {
        var fullLayoutPath = Path.GetFullPath(layoutPath);
        var packageDirectory = Path.GetDirectoryName(fullLayoutPath)
            ?? throw new InstallerException("LAYOUT-006", $"Unable to determine the package folder: {fullLayoutPath}");
        var entries = EnumerateLayoutContent(packageDirectory);
        var layoutInfo = new FileInfo(fullLayoutPath);
        string? manifestTotalPackageSize = null;
        var manifestPath = Path.Combine(packageDirectory, "manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (manifest.RootElement.TryGetProperty("total_package_size", out var total))
                    manifestTotalPackageSize = total.ToString();
            }
            catch (JsonException)
            {
                manifestTotalPackageSize = "<invalid-json>";
            }
        }

        return new LayoutDiagnosticSnapshot(
            Path.GetFileName(packageDirectory),
            fullLayoutPath,
            entries.Count,
            entries.Sum(entry => entry.Size),
            entries.Count == 0 ? 0 : entries.Max(entry => entry.Path.Length),
            layoutInfo.Exists ? layoutInfo.Length : 0,
            layoutInfo.Exists ? ComputeSha256(fullLayoutPath) : null,
            layoutInfo.Exists ? layoutInfo.LastWriteTimeUtc : null,
            File.Exists(manifestPath),
            manifestTotalPackageSize);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static object CreateExceptionDiagnostic(Exception exception) => new
    {
        type = exception.GetType().FullName,
        exception.Message,
        exception.StackTrace,
        innerType = exception.InnerException?.GetType().FullName,
        innerMessage = exception.InnerException?.Message
    };

    private static double Lerp(double start, double end, double amount) => start + (end - start) * amount;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void BackupLayouts(
        string community,
        IReadOnlyCollection<string> packageFolders,
        string backupRoot,
        List<FileRollbackState> backups)
    {
        Directory.CreateDirectory(backupRoot);
        foreach (var folderName in packageFolders)
        {
            var packageDirectory = GetSafeCommunityPackagePath(community, folderName);
            EnsureNoReparsePointsInsideRoot(community, packageDirectory);
            foreach (var filename in new[] { "layout.json", "manifest.json" })
            {
                var target = Path.Combine(packageDirectory, filename);
                EnsureNoReparsePointsInsideRoot(community, target);
                var existed = File.Exists(target);
                var backup = existed ? Path.Combine(backupRoot, folderName + "." + filename) : null;
                if (backup is not null) File.Copy(target, backup, overwrite: false);
                backups.Add(new FileRollbackState(target, backup, existed));
            }
        }
    }

    // Maintenance does not rewrite layouts belonging to unrelated add-ons or H145 packages.
    private string[] GetMissionLayoutPackageFolders(string community) =>
        InspectCommunityPackages(community, SimulatorType.Msfs2024)
            .Where(package => package.State == PackageState.Installed)
            .SelectMany(package => package.Roots)
            .Select(root => root.FolderName)
            .Where(folder => !folder.Equals(H145BasePackageFolder, StringComparison.OrdinalIgnoreCase) && !folder.Equals(H145ActionPackFolder, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string[] GetCommunityLayoutPackageFolders(string community)
    {
        var fullCommunity = Path.GetFullPath(community).TrimEnd(Path.DirectorySeparatorChar);
        EnsureNoReparsePointsInsideRoot(fullCommunity, fullCommunity);
        return Directory.EnumerateDirectories(fullCommunity)
            .Where(directory => (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
            .Where(directory => File.Exists(Path.Combine(directory, "layout.json")))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void RestoreLayouts(string community, IReadOnlyList<FileRollbackState> backups)
    {
        foreach (var state in backups) RestoreFileState(community, state);
    }

    private static FileRollbackState BackupMissionIndex(string community, string backupRoot)
    {
        var target = Path.Combine(
            community,
            H145BasePackageFolder,
            "html_ui",
            "HPGH145-System",
            "GeneratedMissionIndex.json");
        EnsureNoReparsePointsInsideRoot(community, target);
        if (!File.Exists(target))
            return new FileRollbackState(target, null, false);

        Directory.CreateDirectory(backupRoot);
        var backup = Path.Combine(backupRoot, "GeneratedMissionIndex.json");
        File.Copy(target, backup, overwrite: true);
        return new FileRollbackState(target, backup, true);
    }

    private static void RestoreFileState(string community, FileRollbackState? state)
    {
        if (state is null) return;
        EnsureNoReparsePointsInsideRoot(community, state.Target);
        if (state.Existed)
        {
            if (state.Backup is null || !File.Exists(state.Backup))
                throw new InstallerException("INST-009", $"Rollback backup is missing: {state.Backup}");
            Directory.CreateDirectory(Path.GetDirectoryName(state.Target)!);
            File.Copy(state.Backup, state.Target, overwrite: true);
        }
        else if (File.Exists(state.Target))
        {
            File.Delete(state.Target);
        }
    }

    private static void VerifyInstalledPackages(string community, IEnumerable<PackageScanResult> packages)
    {
        foreach (var root in packages.SelectMany(package => package.Roots))
        {
            var destination = GetSafeCommunityPackagePath(community, root.FolderName);
            EnsureNoReparsePointsInsideRoot(community, destination);
            if (!File.Exists(Path.Combine(destination, "manifest.json")) || !File.Exists(Path.Combine(destination, "layout.json")))
            {
                throw new InstallerException("INST-006", $"Installed package verification failed: {root.FolderName}");
            }
        }
    }

    private static string GetSafeCommunityPackagePath(string community, string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName) ||
            folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            folderName is "." or ".." ||
            folderName.Contains(Path.DirectorySeparatorChar) ||
            folderName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InstallerException("PKG-006", $"Invalid package folder name: {folderName}");
        }

        var fullCommunity = Path.GetFullPath(community).TrimEnd(Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(fullCommunity, folderName));
        if (!destination.StartsWith(fullCommunity + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InstallerException("PATH-003", $"Package destination escaped Community: {destination}");
        return destination;
    }

    private static void EnsureNoReparsePointsInsideRoot(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerException("PATH-003", $"Path escaped the selected Community folder: {fullCandidate}");
        }

        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        var current = fullRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InstallerException("PATH-007", $"Reparse point detected in a write target: {current}");
            }
        }
    }

    private static CommunityPackageProbe CreateCommunityPackageProbe(string packageDirectory)
    {
        var folderName = Path.GetFileName(packageDirectory);
        var manifest = ReadBoundedTextFile(
            Path.Combine(packageDirectory, "manifest.json"),
            512 * 1024);
        var layout = ReadBoundedTextFile(
            Path.Combine(packageDirectory, "layout.json"),
            32 * 1024 * 1024);
        var evidence = string.Concat(folderName, "\n", manifest, "\n", layout)
            .Replace('\\', '/')
            .ToLowerInvariant();

        return new CommunityPackageProbe(
            packageDirectory,
            folderName,
            evidence);
    }

    private static string ReadBoundedTextFile(string path, long maximumBytes)
    {
        if (!File.Exists(path)) return string.Empty;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maximumBytes) return string.Empty;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool IsInstalledPackageMatch(string packageId, CommunityPackageProbe probe)
    {
        var folder = probe.FolderName.ToLowerInvariant();
        var identity = probe.Evidence;
        var tokens = Regex.Split(folder, "[^a-z0-9]+")
            .Where(token => token.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Token(params string[] values) => values.Any(tokens.Contains);
        bool FolderHas(params string[] values) => values.All(value =>
            folder.Contains(value, StringComparison.OrdinalIgnoreCase));
        bool FolderAny(params string[] values) => values.Any(value =>
            folder.Contains(value, StringComparison.OrdinalIgnoreCase));

        return packageId switch
        {
            "FR_VEHICLES" =>
                probe.FolderName.Equals("br-mission-dynamicobjectsample", StringComparison.OrdinalIgnoreCase) ||
                identity.Contains("/simobjects/airplanes/hpg-fr-", StringComparison.OrdinalIgnoreCase) ||
                (FolderAny("vehicle", "vehicule") && Token("fr", "french", "france")),
            "30WEST_HEMS" =>
                FolderHas("30west", "hems") ||
                FolderHas("30 west", "hems") ||
                identity.Contains("/simobjects/airplanes/30west_car", StringComparison.OrdinalIgnoreCase),
            "30WEST_SMOKE" =>
                FolderHas("30west", "smoke") ||
                FolderHas("30 west", "smoke"),
            "DE_VEHICLES" =>
                FolderHas("68pony", "vehicles", "de") ||
                (FolderHas("vehicle") && Token("de", "german", "germany")),
            "IT_VEHICLES" =>
                FolderHas("68pony", "vehicles", "it") ||
                (FolderHas("vehicle") && Token("it", "italian", "italy")),
            "US_VEHICLES" =>
                FolderHas("68pony", "vehicles", "us") ||
                (FolderHas("vehicle") && Token("us", "usa", "american")),
            "NL_VEHICLES" =>
                FolderHas("68pony", "vehicles", "nl") ||
                (FolderHas("vehicle") && Token("nl", "dutch", "netherlands")),
            "UK_VEHICLES" =>
                FolderHas("68pony", "vehicles", "uk") ||
                (FolderHas("vehicle") && Token("uk", "british")),
            "ANDREW_MISSION_PACK" =>
                probe.FolderName.Equals(AndrewPackageFolder, StringComparison.OrdinalIgnoreCase),
            "BUNDLE_VOICE_PACK" =>
                FolderHas("kekkeru", "voice", "pack") ||
                FolderHas("bundle", "voice") ||
                identity.Contains("/sound/kekr.kek.", StringComparison.OrdinalIgnoreCase),
            "DUSTIN_VOICE_PACK" =>
                FolderHas("dustin", "voice") ||
                identity.Contains("/sound/dustin.", StringComparison.OrdinalIgnoreCase),
            "H145_HEMS_OBJECTS" =>
                (FolderHas("68pony", "hems") ||
                 FolderHas("actionpack", "hems") ||
                 FolderHas("action pack", "hems")) &&
                !FolderAny("vehicle-us", "vehicle-uk", "vehicle-de", "vehicle-it", "vehicle-nl"),
            _ => false
        };
    }

    private static void VerifyPackageDirectory(string packagePath, string displayName)
    {
        if (!Directory.Exists(packagePath) ||
            !File.Exists(Path.Combine(packagePath, "manifest.json")) ||
            !File.Exists(Path.Combine(packagePath, "layout.json")))
        {
            throw new InstallerException("ANDREW-005", $"Installed package verification failed: {displayName}");
        }
    }

    private static void ValidateJsonFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ValidateJsonStream(stream, Path.GetFileName(path));
        }
        catch (JsonException exception)
        {
            throw new InstallerException("ANDREW-002", $"The cumulative update is not valid JSON: {Path.GetFileName(path)}", exception);
        }
    }

    private static AndrewUpdateVersion ReadAndrewVersionFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadAndrewVersionStream(stream, Path.GetFileName(path));
        }
        catch (JsonException exception)
        {
            throw new InstallerException("ANDREW-002", $"The cumulative update is not valid JSON: {Path.GetFileName(path)}", exception);
        }
    }

    private static AndrewUpdateVersion ReadAndrewVersionStream(Stream stream, string displayName)
    {
        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 128
            });
            // Current upstream releases identify their revision in the mission title.
            // Retain macro-only compatibility for older cumulative packages.
            if (TryGetProperty(document.RootElement, "id", out var identity) &&
                identity.ValueKind == JsonValueKind.String && identity.GetString() == MissionReleaseVersion.MissionId &&
                TryGetProperty(document.RootElement, "title", out var title) && title.ValueKind == JsonValueKind.String &&
                MissionReleaseVersion.FromTitle(title.GetString()) is { } titleVersion)
                return titleVersion;
            if (!TryGetProperty(document.RootElement, "macros", out var macros) ||
                !TryGetProperty(macros, "version check", out var versionCheck) ||
                versionCheck.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The macros/version check array is missing.");
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in versionCheck.EnumerateArray())
            {
                if (!TryGetProperty(item, "set", out var set) ||
                    !TryGetProperty(set, "local", out var local) ||
                    local.ValueKind != JsonValueKind.String ||
                    !TryGetProperty(item, "value", out var value))
                {
                    continue;
                }

                var name = local.GetString();
                var text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(text))
                    values[name] = text;
            }

            if (!values.TryGetValue("MISSION_VERSION", out var missionVersion) ||
                !values.TryGetValue("DEV_VERSION", out var devVersion))
            {
                throw new JsonException("MISSION_VERSION or DEV_VERSION is missing.");
            }
            ValidateVersionComponents(missionVersion, "MISSION_VERSION");
            ValidateVersionComponents(devVersion, "DEV_VERSION");
            values.TryGetValue("DEV_TYPE", out var devType);
            return new AndrewUpdateVersion(missionVersion, devVersion, devType);
        }
        catch (JsonException exception)
        {
            throw new InstallerException(
                "ANDREW-002",
                $"Unable to read the Random & Everywhere version from {displayName}.",
                exception);
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static void ValidateVersionComponents(string version, string field)
    {
        var parts = version.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(part => part.Length == 0 || !int.TryParse(part, out _)))
            throw new JsonException($"{field} has an unsupported value: {version}");
    }

    private static int CompareAndrewVersions(AndrewUpdateVersion left, AndrewUpdateVersion right)
    {
        return MissionReleaseVersion.Compare(left, right);
    }

    private static void ValidateJsonStream(Stream stream, string displayName)
    {
        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 128
            });
            if (document.RootElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
                throw new JsonException("The JSON root must be an object or array.");
        }
        catch (JsonException exception)
        {
            throw new InstallerException("ANDREW-002", $"The cumulative update is not valid JSON: {displayName}", exception);
        }
    }

    [GeneratedRegex("InstalledPackagesPath\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstalledPackagesPathRegex();
    [GeneratedRegex("^Version_(\\d+)\\.txt$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionMarkerRegex();

    private sealed record SessionPaths(string Root, string Downloads, string Staging, string Backup);
    private sealed record FileRollbackState(string Target, string? Backup, bool Existed);
    private sealed record CommunityPackageProbe(string FullPath, string FolderName, string Evidence);
    private sealed record LayoutContentEntry(string Path, long Size, long Date);
    private sealed record LayoutDiagnosticSnapshot(
        string PackageFolder,
        string LayoutPath,
        int EntryCount,
        long ContentBytes,
        int LongestRelativePath,
        long LayoutBytes,
        string? LayoutSha256,
        DateTime? LayoutLastWriteUtc,
        bool ManifestExists,
        string? ManifestTotalPackageSize);
}

/// <summary>
/// Restricts writes to Community children and records the minimum rollback state.
/// </summary>
internal sealed class InstallTransaction
{
    private readonly string _community;
    private readonly string _backupRoot;
    private readonly InstallerLog _log;
    private readonly SharedPackageProtection? _sharedProtection;
    private readonly Dictionary<string, string> _overwrittenFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _createdFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _createdDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unverifiedSharedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Destination, string Backup)> _replacedDirectories = [];
    private bool _committed;
    public IReadOnlyList<string> Warnings => _unverifiedSharedFiles.Count == 0 ? [] :
        [$"Preserved {_unverifiedSharedFiles.Count} unverified shared-object overlaps. Existing files were kept; see INST-PRESERVE-SHARED in the log before assuming these files were updated."];

    public InstallTransaction(string community, string backupRoot, InstallerLog log, SharedPackageProtection? sharedProtection = null)
    {
        _community = Path.GetFullPath(community).TrimEnd(Path.DirectorySeparatorChar);
        _backupRoot = backupRoot;
        _log = log;
        _sharedProtection = sharedProtection;
    }

    public void MergePackage(string stagedPackage, string folderName)
    {
        if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folderName is "." or "..")
        {
            throw new InstallerException("PKG-006", $"Invalid package folder name: {folderName}");
        }

        var destination = GetSafeDestination(folderName);
        var protection = folderName.Equals(SharedPackageProtection.PackageName, StringComparison.OrdinalIgnoreCase)
            ? _sharedProtection ?? SharedPackageProtection.Default : null;
        MergeDirectoryIncrementally(stagedPackage, destination, protection);
        _log.Info("INST-MERGE", "Package merged with an incremental backup.", new
        {
            stagedPackage,
            destination,
            overwrittenFiles = _overwrittenFiles.Count,
            createdFiles = _createdFiles.Count
        });
    }

    public void ReplacePackage(string stagedPackage, string folderName)
    {
        if (folderName.Equals(SharedPackageProtection.PackageName, StringComparison.OrdinalIgnoreCase))
            throw new InstallerException("PKG-009", "Shared HEMS objects require a protected merge, not directory replacement.");
        if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folderName is "." or "..")
            throw new InstallerException("PKG-006", $"Invalid package folder name: {folderName}");
        if (!Directory.Exists(stagedPackage))
            throw new InstallerException("ANDREW-005", $"Staged package does not exist: {stagedPackage}");

        var destination = GetSafeDestination(folderName);
        if (!Directory.Exists(destination))
            throw new InstallerException("ANDREW-003", $"The package to replace is not installed: {destination}");

        var backup = Path.Combine(_backupRoot, "Packages", folderName);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        if (!Path.GetPathRoot(destination)!.Equals(Path.GetPathRoot(backup), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetPathRoot(destination)!.Equals(Path.GetPathRoot(stagedPackage), StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerException("PATH-008", "Stable package replacement requires staging and backup on the Community drive.");
        }

        Directory.Move(destination, backup);
        try
        {
            Directory.Move(stagedPackage, destination);
            _replacedDirectories.Add((destination, backup));
        }
        catch
        {
            if (!Directory.Exists(destination) && Directory.Exists(backup)) Directory.Move(backup, destination);
            throw;
        }
        _log.Info("INST-REPLACE", "Package directory replaced using a same-drive transactional move.", new { destination, backup });
    }

    public void Commit()
    {
        _committed = true;
        _log.Success("INST-COMMIT", "Installation transaction committed.", new
        {
            overwrittenFiles = _overwrittenFiles.Count,
            createdFiles = _createdFiles.Count,
            createdDirectories = _createdDirectories.Count,
            replacedDirectories = _replacedDirectories.Count
        });
    }

    // Rollback reverses only files and directories recorded by this transaction.
    public void Rollback()
    {
        if (_committed) return;
        foreach (var replacement in _replacedDirectories.AsEnumerable().Reverse())
        {
            EnsureSafeDestination(replacement.Destination);
            if (!Directory.Exists(replacement.Backup))
                throw new InstallerException("INST-009", $"Rollback backup is missing: {replacement.Backup}");
            if (Directory.Exists(replacement.Destination)) Directory.Delete(replacement.Destination, recursive: true);
            if (Directory.Exists(replacement.Backup)) Directory.Move(replacement.Backup, replacement.Destination);
        }
        foreach (var file in _createdFiles.OrderByDescending(path => path.Length))
        {
            EnsureSafeDestination(file);
            if (File.Exists(file)) File.Delete(file);
        }
        foreach (var pair in _overwrittenFiles)
        {
            EnsureSafeDestination(pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
            File.Copy(pair.Value, pair.Key, overwrite: true);
        }
        foreach (var directory in _createdDirectories.OrderByDescending(path => path.Length))
        {
            EnsureSafeDestination(directory);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        _log.Success("INST-ROLLBACK", "Installation transaction rolled back.");
    }

    private string GetSafeDestination(string folderName)
    {
        var destination = Path.GetFullPath(Path.Combine(_community, folderName));
        EnsureSafeDestination(destination);
        return destination;
    }

    private void EnsureSafeDestination(string destination)
    {
        var prefix = _community + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerException("PATH-003", $"Package destination escaped Community: {destination}");
        }

        var relative = Path.GetRelativePath(_community, destination);
        var current = _community;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InstallerException("PATH-007", $"Reparse point detected in a package destination: {current}");
            }
        }
    }

    private void MergeDirectoryIncrementally(string source, string destination, SharedPackageProtection? protection)
    {
        EnsureDirectory(destination);
        var queue = new Queue<(string Source, string Destination)>();
        queue.Enqueue((source, destination));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var directory in Directory.EnumerateDirectories(current.Source))
            {
                var attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InstallerException("PATH-007", $"Reparse point detected inside package: {directory}");
                }
                var childDestination = Path.Combine(current.Destination, Path.GetFileName(directory));
                EnsureDirectory(childDestination);
                queue.Enqueue((directory, childDestination));
            }

            foreach (var file in Directory.EnumerateFiles(current.Source))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InstallerException("PATH-007", $"Reparse point detected inside package: {file}");
                }
                var target = Path.Combine(current.Destination, Path.GetFileName(file));
                EnsureSafeDestination(target);
                if (protection is not null && !protection.ShouldCopy(Path.GetRelativePath(source, file), file, target, out var reason))
                {
                    if (reason == "preserve-canonical-installed-content")
                        _log.Info("INST-PRESERVE-SHARED", "Canonical shared package file preserved.", new { target, reason });
                    else
                    {
                        _unverifiedSharedFiles.Add(target);
                        _log.Warning("INST-PRESERVE-SHARED", "Shared package file preserved; no verified upgrade.", new { target, reason });
                    }
                    continue;
                }
                if (File.Exists(target))
                {
                    if (!_createdFiles.Contains(target) && !_overwrittenFiles.ContainsKey(target))
                    {
                        var relative = Path.GetRelativePath(_community, target);
                        var backup = Path.Combine(_backupRoot, "Files", relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(target, backup, overwrite: false);
                        _overwrittenFiles[target] = backup;
                    }
                }
                else
                {
                    _createdFiles.Add(target);
                }
                var temporary = target + $".rae-{Guid.NewGuid():N}.tmp";
                try
                {
                    File.Copy(file, temporary, true);
                    File.Move(temporary, target, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
            }
        }
    }

    private void EnsureDirectory(string directory)
    {
        EnsureSafeDestination(directory);
        if (Directory.Exists(directory)) return;
        Directory.CreateDirectory(directory);
        _createdDirectories.Add(directory);
    }
}
