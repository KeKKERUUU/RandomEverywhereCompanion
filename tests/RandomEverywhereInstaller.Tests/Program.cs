using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Reflection.Metadata;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Core.Companion;
using RandomEverywhereInstaller.Infrastructure.Companion;
using RandomEverywhereInstaller.Core.Models;
using RandomEverywhereInstaller.Core.Updates;
using RandomEverywhereInstaller.Core.Wizard;
using RandomEverywhereInstaller.Infrastructure.Localization;
using RandomEverywhereInstaller.Infrastructure.Installer;
using RandomEverywhereInstaller.Infrastructure.Updates;

namespace RandomEverywhereInstaller.Tests;

internal static partial class Program
{
    private static readonly List<string> Failures = [];
    private static int _passedTests;

    public static int Main()
    {
        Run("World data preserves upstream coordinate and waypoint rules", WorldCoordinates);
        Run("World local entries validate, edit, remove and retain backups", WorldLocalEditing);
        Run("World sync verifies manifests, local precedence, cache and counters", WorldVerifiedSync);
        Run("World sync rejects manifest traversal and cancellation", WorldSourceGuards);
        Run("World database preview apply restore preserves unrelated files", WorldDatabaseRoundtrip);
        Run("World database rejects stale plans and external changes", WorldDatabaseConflicts);
        Run("World database partial failure rolls back touched files", WorldDatabaseRollback);
        Run("World data rejects linked paths", WorldLinkGuard);
        Run("World database cancellation restores committed files", WorldDatabaseCancellation);
        Run("World database removal rules and backup integrity are enforced", WorldRemovalAndBackup);
        Run("Wizard starts at disclaimer", WizardStartsAtDisclaimer);
        Run("Wizard blocks future navigation", WizardBlocksFutureNavigation);
        Run("Wizard transitions through every step", WizardTransitionsThroughEveryStep);
        Run("Wizard reads Community before archive collection", WizardReadsCommunityBeforeArchives);
        Run("Wizard Andrew update mode uses the dedicated route", WizardAndrewUpdateUsesDedicatedRoute);
        Run("Companion routes exclude repeated onboarding and preserve step guards", CompanionRoutesExcludeOnboarding);
        Run("Installation review reflects merge stable cumulative and metadata scope", InstallationReviewMatchesPlan);
        Run("Full installation initial scan does not require archives", FullInstallationInitialScanDoesNotRequireArchives);
        Run("Wizard allows navigation to reached steps", WizardAllowsReachedStepNavigation);
        Run("Localization files have complete matching keys", LocalizationFilesHaveMatchingKeys);
        Run("Localization placeholders match English", LocalizationPlaceholdersMatchEnglish);
        Run("Localization files contain no duplicate keys", LocalizationFilesHaveNoDuplicateKeys);
        Run("Localization switches culture at runtime", LocalizationSwitchesCultureAtRuntime);
        Run("Package scanner detects valid and missing packages", PackageScannerDetectsPackages);
        Run("Community inventory detects packages before archive selection", CommunityInventoryDetectsInstalledPackages);
        Run("Community inventory recognizes real package roots from diagnostics", CommunityInventoryRecognizesDiagnosticRoots);
        Run("Community inventory uses layout evidence for renamed packages", CommunityInventoryUsesLayoutEvidence);
        Run("Community inventory remains distinct from archive availability", CommunityInventoryRemainsDistinctFromArchives);
        Run("Offshore is excluded from the catalog while existing files are preserved", OffshoreRemovedWithoutDeletion);
        Run("Package catalog matches original filenames and requirement levels", PackageCatalogMatchesOriginalFiles);
        Run("Package scanner accepts archives from different folders", PackageScannerAcceptsDifferentFolders);
        Run("Package scanner accepts safe overlay archives", PackageScannerAcceptsOverlayArchives);
        Run("Package scanner resolves deeply nested Community roots", PackageScannerResolvesNestedCommunityRoot);
        Run("Package scanner rejects path traversal", PackageScannerRejectsTraversal);
        Run("Package scanner accepts highly compressible MSFS textures", PackageScannerAcceptsCompressedTextures);
        Run("H145 detection reports Action Pack presence", H145DetectionReportsActionPack);
        Run("H145 build 500 refreshes only aircraft layouts without network", H145CurrentBuildOnlyRefreshesScopedLayouts);
        Run("H145 patches only installed Base and Action packages", H145PatchRespectsActionPackPresence);
        Run("H145 invalid patches restore content and layout absence", H145InvalidPatchRollsBack);
        Run("H145 rejects unexpected package roots and download hosts", H145RejectsUnexpectedRootsAndRedirects);
        Run("H145 cancelled maintenance restores layout and manifest metadata", H145CancelledLayoutRefreshRestoresMetadata);
        Run("Andrew updater detects cumulative and stable inputs", AndrewUpdaterDetectsInputKinds);
        Run("Andrew title release versions match upstream ordering", AndrewTitleReleaseVersions);
        Run("Andrew inspector accepts current upstream mission identity and title", AndrewTitleInspection);
        Run("Mission update metadata preserves upstream revision and release notes", MissionUpdateMetadata);
        Run("Mission download verifies digest identity revision and length", MissionDownloadValidation);
        Run("Mission download rejects redirects before contacting unknown hosts", MissionRedirectGuard);
        Run("Mission fallback reads only the configured source after HTTP failure", MissionFallback);
        Run("Mission downloads preserve existing files and respect cancellation", MissionDownloadOwnership);
        Run("Mission cache stays outside Community and preserves unknown files", MissionCacheOwnership);
        Run("Andrew updater accepts a renamed cumulative JSON", AndrewUpdaterAcceptsRenamedCumulativeJson);
        Run("Andrew updater selects the newest cumulative JSON", AndrewUpdaterSelectsNewestCumulativeJson);
        Run("Andrew cumulative update requires the primary package", AndrewCumulativeUpdateRequiresPrimaryPackage);
        Run("Andrew cumulative update prevents a downgrade", AndrewCumulativeUpdatePreventsDowngrade);
        Run("Andrew cumulative updater replaces only everywhere_all.json", AndrewCumulativeUpdateReplacesJson);
        Run("Full installation applies the newest JSON after the primary package", FullInstallationAppliesJsonAfterPrimaryPackage);
        Run("Full installation applies only a cumulative JSON to an installed primary package", FullInstallationAppliesOnlyCumulativeJson);
        Run("Andrew stable updater replaces the complete package", AndrewStableUpdateReplacesPackage);
        Run("Installation flow exits when required packages are already installed", InstallationFlowExitsWhenRequiredPackagesAreInstalled);
        Run("Transactional installer installs a valid package", TransactionalInstallerInstallsPackage);
        Run("Installer preserves unrelated Community folders", InstallerPreservesUnrelatedCommunityFolders);
        Run("Shared object merges preserve canonical and unknown installed data", SharedPackageProtectionPreservesKnownAndUnknownContent);
        Run("Regular installation cannot overwrite H145 core folders", RegularInstallCannotOverwriteH145Core);
        Run("Transactional installer rolls back after post-processing failure", TransactionalInstallerRollsBack);
        Run("Built-in layout generator creates a complete verified layout", BuiltInLayoutGeneratorCreatesVerifiedLayout);
        Run("Native mission index matches HPG hierarchy and merges package folders", NativeIndexStructure);
        Run("Native mission index preserves Unicode for legacy Base64 consumers", NativeIndexUnicode);
        Run("Native mission index rejects invalid JSON without replacing prior output", NativeIndexFailureAtomicity);
        Run("Native mission index cancellation preserves prior output and removes partials", NativeIndexCancellation);
        Run("Native mission index refuses linked paths outside Community", NativeIndexLinks);
        Run("Native mission index never executes or requires the legacy script", NativeIndexDoesNotExecuteScript);
        Run("Layout validation rejects stale content", LayoutValidationRejectsStaleContent);
        Run("Installation refreshes scoped layouts and preserves unrelated add-ons", InstallationRegeneratesScopedLayoutsWithoutNetwork);
        Run("GitHub updater selects the matching newer executable", GitHubUpdaterSelectsMatchingNewerExecutable);
        Run("GitHub updater ignores the installed release", GitHubUpdaterIgnoresInstalledRelease);
        Run("GitHub updater verifies the downloaded executable digest", GitHubUpdaterVerifiesDownloadedDigest);
        Run("GitHub updater rejects corrupt downloads without replacing existing files", GitHubUpdaterRejectsWrongDigest);
        Run("Update cache cleanup does not follow junctions", UpdateCacheCannotFollowJunctions);
        Run("Update cache cleanup preserves unknown and active files", UpdateCachePreservesUnknownAndActiveFiles);
        RunInstallerAuditTests();

        Run("Recorded and synthetic modes stay separate", CompanionAudioModesStaySeparate);
        Run("Companion settings preserve paths and mode", CompanionSettingsPersist);
        Run("Companion startup chooses audio or installation from installed required packages", CompanionStartupUsesInstalledInventory);
        Run("Companion UI has valid translation keys and PNG controls", CompanionUiHasValidResources);
        Run("Companion and installer share one control theme", CompanionUsesOneControlTheme);
        Run("Contextual audio/settings details stay inside the shell", CompanionDetailsStayInShell);
        Run("Simulator connection controls use a reserved shell row and compact buttons", CompanionConnectionControlsStayVisible);
        Run("Recorded library controls remain separate from synthetic imports", RecordedLibraryControlsAreSeparate);
        Run("Companion assembly embeds the complete runtime payload", CompanionAssemblyEmbedsRuntime);
        Run("Bundled SimConnect is the pinned native x64 redistributable", BundledSimConnectIsThePinnedX64Redistributable);
        Run("Debug cleanup preserves user files and verifies its backup", DebugCleanupPreservesUserFiles);

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"All {_passedTests} tests passed.");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"{Failures.Count} test(s) failed:");
        foreach (var failure in Failures)
        {
            Console.WriteLine($" - {failure}");
        }
        Console.ResetColor();
        return 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passedTests++;
            Console.WriteLine($"PASS  {name}");
        }
        catch (Exception exception)
        {
            Failures.Add($"{name}: {exception}");
            Console.WriteLine($"FAIL  {name}");
        }
    }

    private static void InstallationReviewMatchesPlan()
    {
        var community = Path.Combine(Path.GetTempPath(), "review-only-community");
        var definition = new PackageDefinition("fixture", "Fixture package", true, 1, ".*",
            new HashSet<SimulatorType> { SimulatorType.Msfs2024 }, string.Empty);
        var found = new PackageScanResult(definition, PackageState.Found, "fixture.zip",
            [new DetectedPackageRoot("wrapper/root", "actual-package")], null);
        var installed = new AndrewPackageInfo(true, Path.Combine(community, "primary"), true,
            Path.Combine(community, "primary", "missions", "everywhere_all.json"));
        var cumulative = new AndrewUpdateInspection(AndrewUpdateKind.Cumulative, "input.json", null, null,
            new AndrewUpdateVersion("0.99", "1.3", "BETA"));
        var rows = InstallationReview.Build(community, [found], cumulative, installed, false);
        Equal(3, rows.Count);
        Equal(ReviewOperation.Merge, rows[0].Operation);
        Equal(Path.Combine(community, "actual-package"), rows[0].Destination);
        Equal(ReviewOperation.Cumulative, rows[1].Operation);
        Equal(installed.CumulativeTargetPath, rows[1].Destination);
        Equal(ReviewOperation.Metadata, rows[2].Operation);
        var stable = cumulative with { Kind = AndrewUpdateKind.Stable };
        var stableRows = InstallationReview.Build(community, [found], stable, installed, true);
        Equal(2, stableRows.Count);
        Equal(ReviewOperation.Replace, stableRows[0].Operation);
        Equal(installed.PackagePath, stableRows[0].Destination);
        Equal(0, InstallationReview.Build(community, [found with { State = PackageState.Installed }], null, installed, false).Count);
        var fresh = InstallationReview.Build(community, [], cumulative, null, false);
        True(fresh[0].Destination.EndsWith(Path.Combine("hpgmission", "RANDOM AND EVERYWHERE", "everywhere_all.json"), StringComparison.Ordinal),
            "Fresh primary package uses the wrong cumulative target");
    }

    private static void CompanionRoutesExcludeOnboarding()
    {
        foreach (var mode in new[] { InstallerMode.FullInstallation, InstallerMode.AndrewUpdateOnly })
        {
            var state = new WizardState();
            state.SetMode(mode);
            state.BeginCompanionWorkflow();
            Equal(WizardStep.ZipFolder, state.CurrentStep);
            Equal(1, state.GetDisplayNumber(WizardStep.ZipFolder));
            False(state.MoveBack(), "Files must be the first embedded step.");
            foreach (var excluded in new[] { WizardStep.Disclaimer, WizardStep.Language, WizardStep.Simulator })
                False(state.NavigateTo(excluded), "Repeated onboarding is reachable from the companion.");
            False(state.NavigateTo(WizardStep.Installation), "Installation must not bypass input review.");
            var expected = mode == InstallerMode.FullInstallation
                ? new[] { WizardStep.ZipFolder, WizardStep.PackageCheck, WizardStep.Installation, WizardStep.Completed }
                : new[] { WizardStep.ZipFolder, WizardStep.Installation, WizardStep.Completed };
            Equal(string.Join(',', expected), string.Join(',', state.Route));
            foreach (var step in expected.Skip(1))
            {
                True(state.MoveNext(), "Embedded route cannot advance.");
                Equal(step, state.CurrentStep);
            }
            False(state.MoveNext(), "Embedded route advanced beyond completion.");
            True(state.NavigateTo(WizardStep.ZipFolder), "Previously visited files step should remain reachable.");
            False(state.MoveBack(), "Returning to files exposed onboarding.");
        }
    }

    private static void WizardStartsAtDisclaimer()
    {
        var state = new WizardState();
        Equal(WizardStep.Disclaimer, state.CurrentStep);
        False(state.CanMoveBack, "Back must be disabled on the first step.");
        True(state.CanMoveNext, "Next must be enabled on the first step.");
    }

    private static void WizardBlocksFutureNavigation()
    {
        var state = new WizardState();
        False(state.CanNavigateTo(WizardStep.Language), "Unreached future step should be locked.");
        False(state.NavigateTo(WizardStep.Completed), "Direct jump to Completed must fail.");
        Equal(WizardStep.Disclaimer, state.CurrentStep);
    }

    private static void WizardTransitionsThroughEveryStep()
    {
        var state = new WizardState();
        foreach (var expected in state.Route.Skip(1))
        {
            True(state.MoveNext(), $"MoveNext failed before step {expected}.");
            Equal(expected, state.CurrentStep);
        }

        False(state.MoveNext(), "MoveNext must fail after Completed.");
        False(state.CanMoveNext, "CanMoveNext must be false after Completed.");
    }

    private static void WizardReadsCommunityBeforeArchives()
    {
        var state = new WizardState();
        var route = state.Route.ToArray();
        True(
            Array.IndexOf(route, WizardStep.Simulator) < Array.IndexOf(route, WizardStep.ZipFolder),
            "Simulator and Community selection must occur before archive collection.");
    }

    private static void WizardAllowsReachedStepNavigation()
    {
        var state = new WizardState();
        state.MoveNext();
        state.MoveNext();
        Equal(WizardStep.Simulator, state.CurrentStep);
        True(state.NavigateTo(WizardStep.Language), "Reached earlier step should be selectable.");
        Equal(WizardStep.Language, state.CurrentStep);
        True(state.CanNavigateTo(WizardStep.Simulator), "Previously reached step should remain selectable.");
        False(state.CanNavigateTo(WizardStep.ZipFolder), "Unreached future step should remain locked.");
    }

    private static void WizardAndrewUpdateUsesDedicatedRoute()
    {
        var state = new WizardState();
        True(state.SetMode(InstallerMode.AndrewUpdateOnly), "Andrew update mode should be selectable on the disclaimer step.");
        var expected = new[]
        {
            WizardStep.Disclaimer,
            WizardStep.Language,
            WizardStep.Simulator,
            WizardStep.ZipFolder,
            WizardStep.Installation,
            WizardStep.Completed
        };
        Equal(string.Join(',', expected), string.Join(',', state.Route));
        False(state.CanNavigateTo(WizardStep.PackageCheck), "Package check must not be part of the Andrew update route.");
    }

    private static void LocalizationFilesHaveMatchingKeys()
    {
        var files = LoadLocalizationDocuments();
        var expected = files["en-US"].RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (culture, document) in files)
        {
            var actual = document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            var missing = expected.Except(actual).Order().ToArray();
            var extra = actual.Except(expected).Order().ToArray();
            True(missing.Length == 0, $"{culture} missing keys: {string.Join(", ", missing)}");
            True(extra.Length == 0, $"{culture} extra keys: {string.Join(", ", extra)}");
        }
    }

    private static void LocalizationPlaceholdersMatchEnglish()
    {
        var files = LoadLocalizationDocuments();
        var expected = ReadValues(files["en-US"]);
        foreach (var (culture, document) in files.Where(item => item.Key != "en-US"))
        {
            var actual = ReadValues(document);
            foreach (var key in expected.Keys)
            {
                var expectedPlaceholders = PlaceholderRegex().Matches(expected[key]).Select(match => match.Value).ToHashSet();
                var actualPlaceholders = PlaceholderRegex().Matches(actual[key]).Select(match => match.Value).ToHashSet();
                True(expectedPlaceholders.SetEquals(actualPlaceholders), $"{culture}:{key} placeholder mismatch.");
            }
        }
    }

    private static void LocalizationFilesHaveNoDuplicateKeys()
    {
        foreach (var (culture, document) in LoadLocalizationDocuments())
        {
            var duplicates = document.RootElement.EnumerateObject()
                .GroupBy(property => property.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            True(duplicates.Length == 0, $"{culture} duplicate keys: {string.Join(", ", duplicates)}");
        }
    }

    private static void LocalizationSwitchesCultureAtRuntime()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Localization");
        var service = new JsonLocalizationService(culture => File.OpenRead(Path.Combine(directory, $"{culture}.json")));
        var eventCount = 0;
        service.CultureChanged += (_, _) => eventCount++;

        Equal("Ready to begin", service.GetText("Status.Ready"));
        service.SetCulture("it-IT");
        Equal("it-IT", service.CurrentCulture);
        Equal("Pronto per iniziare", service.GetText("Status.Ready"));
        Equal(1, eventCount);
    }

    private static void PackageScannerDetectsPackages()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            CreateZip(Path.Combine(root, "Pack vehicles FR v2_test.zip"), new Dictionary<string, string>
            {
                ["fr-package/manifest.json"] = "{}",
                ["fr-package/layout.json"] = "{}",
                ["fr-package/SimObjects/readme.txt"] = "test"
            });
            using var engine = new InstallerEngine();
            var results = engine.ScanPackagesAsync(Directory.EnumerateFiles(root, "*.zip").ToArray(), SimulatorType.Msfs2020, new Progress<InstallerProgress>(), CancellationToken.None).GetAwaiter().GetResult();
            Equal(PackageState.Found, results.Single(result => result.Definition.Id == "FR_VEHICLES").State);
            Equal(PackageState.RequiredMissing, results.Single(result => result.Definition.Id == "ANDREW_MISSION_PACK").State);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void CommunityInventoryDetectsInstalledPackages()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
            var french = Directory.CreateDirectory(Path.Combine(community, "br-mission-dynamicobjectsample")).FullName;
            File.WriteAllText(Path.Combine(french, "manifest.json"), "{\"title\":\"French emergency vehicles\"}");
            File.WriteAllText(Path.Combine(french, "layout.json"), "{}");
            var west = Directory.CreateDirectory(Path.Combine(community, "30west-hems-objects")).FullName;
            File.WriteAllText(Path.Combine(west, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(west, "layout.json"), "{}");
            var andrew = Directory.CreateDirectory(Path.Combine(community, "hpg-airbus-h145-z-andrew-mission-pack")).FullName;
            File.WriteAllText(Path.Combine(andrew, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(andrew, "layout.json"), "{}");
            var pony = Directory.CreateDirectory(Path.Combine(community, "68ponygt-actionpack-hems-construction")).FullName;
            File.WriteAllText(Path.Combine(pony, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(pony, "layout.json"), "{}");
            var unrelated = Directory.CreateDirectory(Path.Combine(community, "unrelated-user-package")).FullName;
            File.WriteAllText(Path.Combine(unrelated, "manifest.json"), "{\"title\":\"Keep me\"}");
            File.WriteAllText(Path.Combine(unrelated, "layout.json"), "{}");

            using var engine = new InstallerEngine();
            var inventory = engine.InspectCommunityPackages(community, SimulatorType.Msfs2020);

            foreach (var id in new[] { "FR_VEHICLES", "30WEST_HEMS", "ANDREW_MISSION_PACK", "H145_HEMS_OBJECTS" })
                Equal(PackageState.Installed, inventory.Single(result => result.Definition.Id == id).State);
            True(
                inventory.Where(result => result.Definition.Required).All(result => result.RequirementSatisfied),
                "Detected required Community packages must satisfy the pre-drop requirement check.");
            True(Directory.Exists(unrelated), "Community inventory must not modify unrelated folders.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void CommunityInventoryRemainsDistinctFromArchives()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
            var archive = Path.Combine(root, "US Action Pack Vehicles v1.2_RANDOM.zip");
            CreateZip(archive, new Dictionary<string, string>
            {
                ["68ponygt-actionpack-vehicles-us-lp/manifest.json"] = "{}",
                ["68ponygt-actionpack-vehicles-us-lp/layout.json"] = "{}"
            });

            using var engine = new InstallerEngine();
            var inventory = engine.InspectCommunityPackages(community, SimulatorType.Msfs2020);
            var archives = engine.ScanPackagesAsync(
                    [archive],
                    SimulatorType.Msfs2020,
                    new Progress<InstallerProgress>(),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Equal(
                PackageState.OptionalMissing,
                inventory.Single(result => result.Definition.Id == "US_VEHICLES").State);
            Equal(
                PackageState.Found,
                archives.Single(result => result.Definition.Id == "US_VEHICLES").State);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void CommunityInventoryRecognizesDiagnosticRoots()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
            foreach (var folder in new[]
                     {
                         "br-mission-dynamicobjectsample",
                         "30west-hems-objects",
                         "68ponygt-actionpack-vehicles-de-lp",
                         "68ponygt-actionpack-vehicles-it-lp",
                         "68ponygt-actionpack-vehicles-us-lp",
                         "68ponygt-actionpack-vehicles-nl-lp",
                         "68ponygt-actionpack-vehicles-uk-lp",
                         "hpg-airbus-h145-z-andrew-mission-pack",
                         "hpg-airbus-h145-z-kekkeru-voice-pack",
                         "hpg-airbus-h145-z-dustin-voice-pack",
                         "68ponygt-actionpack-hems-construction"
                     })
            {
                var package = Directory.CreateDirectory(Path.Combine(community, folder)).FullName;
                File.WriteAllText(Path.Combine(package, "manifest.json"), "{}");
                File.WriteAllText(Path.Combine(package, "layout.json"), "{\"content\":[]}");
            }

            using var engine = new InstallerEngine();
            var inventory = engine.InspectCommunityPackages(community, SimulatorType.Msfs2020);

            foreach (var id in new[]
                     {
                         "FR_VEHICLES",
                         "30WEST_HEMS",
                         "DE_VEHICLES",
                         "IT_VEHICLES",
                         "US_VEHICLES",
                         "NL_VEHICLES",
                         "UK_VEHICLES",
                         "ANDREW_MISSION_PACK",
                         "BUNDLE_VOICE_PACK",
                         "DUSTIN_VOICE_PACK",
                         "H145_HEMS_OBJECTS"
                     })
            {
                Equal(PackageState.Installed, inventory.Single(result => result.Definition.Id == id).State);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void CommunityInventoryUsesLayoutEvidence()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
            var dustin = Directory.CreateDirectory(Path.Combine(community, "renamed-public-package")).FullName;
            File.WriteAllText(Path.Combine(dustin, "manifest.json"), "{}");
            File.WriteAllText(
                Path.Combine(dustin, "layout.json"),
                "{\"content\":[{\"path\":\"Simobjects/Airplanes/hpg-airbus-h145-ems/Sound/DUSTIN.test.wav\"}]}");

            var voice = Directory.CreateDirectory(Path.Combine(community, "renamed-voice-package")).FullName;
            File.WriteAllText(Path.Combine(voice, "manifest.json"), "{}");
            File.WriteAllText(
                Path.Combine(voice, "layout.json"),
                "{\"content\":[{\"path\":\"Simobjects/Airplanes/hpg-airbus-h145-ems/Sound/KEKR.KEK.cpl.all.good.wav\"}]}");

            using var engine = new InstallerEngine();
            var inventory = engine.InspectCommunityPackages(community, SimulatorType.Msfs2020);

            Equal(
                PackageState.Installed,
                inventory.Single(result => result.Definition.Id == "DUSTIN_VOICE_PACK").State);
            Equal(
                PackageState.Installed,
                inventory.Single(result => result.Definition.Id == "BUNDLE_VOICE_PACK").State);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void PackageScannerRejectsTraversal()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            CreateZip(Path.Combine(root, "30west-hems-objects_test.zip"), new Dictionary<string, string>
            {
                ["hems/manifest.json"] = "{}",
                ["hems/layout.json"] = "{}",
                ["../escape.txt"] = "blocked"
            });
            using var engine = new InstallerEngine();
            var results = engine.ScanPackagesAsync(Directory.EnumerateFiles(root, "*.zip").ToArray(), SimulatorType.Msfs2020, new Progress<InstallerProgress>(), CancellationToken.None).GetAwaiter().GetResult();
            Equal(PackageState.Invalid, results.Single(result => result.Definition.Id == "30WEST_HEMS").State);
            False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escape.txt")), "Traversal entry must never be extracted.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void PackageScannerAcceptsCompressedTextures()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            CreateZip(Path.Combine(root, "H145 ActionPack HEMS Objects v1.1_RANDOM.zip"), new Dictionary<string, string>
            {
                ["68ponygt-actionpack-hems-construction/manifest.json"] = "{}",
                ["68ponygt-actionpack-hems-construction/layout.json"] = "{}",
                ["68ponygt-actionpack-hems-construction/SimObjects/Airplanes/68ponyGT_Forklift/texture/IMAGE_2.JPG.DDS"] = new('A', 2 * 1024 * 1024)
            });
            using var engine = new InstallerEngine();
            var results = engine.ScanPackagesAsync(Directory.EnumerateFiles(root).ToArray(), SimulatorType.Msfs2024, new Progress<InstallerProgress>(), CancellationToken.None).GetAwaiter().GetResult();
            Equal(PackageState.Found, results.Single(result => result.Definition.Id == "H145_HEMS_OBJECTS").State);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void PackageScannerAcceptsOverlayArchives()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            CreateZip(Path.Combine(root, "Pack vehicules FR v2_CT23F.rar"), new Dictionary<string, string>
            {
                ["download-wrapper/readme.txt"] = "documentation",
                ["download-wrapper/br-mission-dynamicobjectsample/SimObjects/vehicle.cfg"] = "payload",
                ["download-wrapper/br-mission-dynamicobjectsample/Effects/effect.fx"] = "payload"
            });
            CreateZip(Path.Combine(root, "US Action Pack Vehicles v9_NEW.zip"), new Dictionary<string, string>
            {
                ["Community/br-mission-dynamicobjectsample/SimObjects/us.cfg"] = "payload",
                ["Community/br-mission-dynamicobjectsample/Effects/us.fx"] = "payload"
            });
            CreateZip(Path.Combine(root, "30 west HEMS objects release_NEW.zip"), new Dictionary<string, string>
            {
                ["30west-hems-objects/SimObjects/object.cfg"] = "payload",
                ["30west-hems-objects/Effects/object.fx"] = "payload"
            });
            CreateZip(Path.Combine(root, "68ponyGT HEMS Objects final_NEW.zip"), new Dictionary<string, string>
            {
                ["pony-hems/SimObjects/object.cfg"] = "payload",
                ["pony-hems/Effects/object.fx"] = "payload"
            });
            CreateZip(Path.Combine(root, "hpg-airbus-h145-z-Dustin-voice-pack 0.32_pskoS.rar"), new Dictionary<string, string>
            {
                ["hpg-airbus-h145/Sound/voice.wav"] = "payload",
                ["hpg-airbus-h145/html_ui/voice.json"] = "payload"
            });

            using var engine = new InstallerEngine();
            var paths = Directory.EnumerateFiles(root).ToArray();
            var results = engine.ScanPackagesAsync(paths, SimulatorType.Msfs2024, new Progress<InstallerProgress>(), CancellationToken.None).GetAwaiter().GetResult();
            foreach (var id in new[] { "FR_VEHICLES", "US_VEHICLES", "30WEST_HEMS", "H145_HEMS_OBJECTS", "DUSTIN_VOICE_PACK" })
                Equal(PackageState.Found, results.Single(result => result.Definition.Id == id).State);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void PackageScannerAcceptsDifferentFolders()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
            var second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
            var fr = Path.Combine(first, "Pack vehicles FR v9_RANDOM.zip");
            var hems = Path.Combine(second, "30west HEMS objects release_CHANGED.zip");
            CreateZip(fr, new Dictionary<string, string> { ["fr/manifest.json"] = "{}", ["fr/layout.json"] = "{}" });
            CreateZip(hems, new Dictionary<string, string> { ["hems/manifest.json"] = "{}", ["hems/layout.json"] = "{}" });

            using var engine = new InstallerEngine();
            var results = engine.ScanPackagesAsync([fr, hems], SimulatorType.Msfs2020, new Progress<InstallerProgress>(), CancellationToken.None).GetAwaiter().GetResult();
            Equal(PackageState.Found, results.Single(result => result.Definition.Id == "FR_VEHICLES").State);
            Equal(PackageState.Found, results.Single(result => result.Definition.Id == "30WEST_HEMS").State);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void PackageCatalogMatchesOriginalFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var names = new[]
            {
                "Pack vehicles FR v2_CT23F.zip",
                "30west-hems-objects_Vers15_BYU6mb.zip",
                "30west-smoke_xqP4v.zip",
                "DE ActionPack Vehicles v1.1_bT4HQ.zip",
                "IT Action Pack Vehicles beta 1.1_4hkNC.zip",
                "US Action Pack Vehicles v1.2_7E1bc.zip",
                "NL Action Pack Vehicles v1.1_gtaeV.zip",
                "UK Action Pack Vehicles v1.2_oCF8r.zip",
                "hpg-airbus-h145-z-andrew-mission-pack_KJPiu.zip",
                "Bundle_Voice_Pack_0.4.2_95GZC.zip",
                "hpg-airbus-h145-z-Dustin-voice-pack-0.32_pskoS.zip",
                "H145 ActionPack HEMS Objects v1.1_wkBAT.zip"
            };

            for (var index = 0; index < names.Length; index++)
            {
                CreateZip(Path.Combine(root, names[index]), new Dictionary<string, string>
                {
                    [$"package-{index}/manifest.json"] = "{}",
                    [$"package-{index}/layout.json"] = "{}"
                });
            }

            using var engine = new InstallerEngine();
            var results = engine.ScanPackagesAsync(Directory.EnumerateFiles(root, "*.zip").ToArray(), SimulatorType.Msfs2024, new Progress<InstallerProgress>(), CancellationToken.None).GetAwaiter().GetResult();
            True(results.All(result => result.State == PackageState.Found), "Every supplied original filename must match exactly one package rule.");

            var required = results.Where(result => result.Definition.Required).Select(result => result.Definition.Id).ToArray();
            Equal("FR_VEHICLES,30WEST_HEMS,ANDREW_MISSION_PACK,H145_HEMS_OBJECTS", string.Join(',', required));
            True(results.Where(result => !result.Definition.Required).All(result => result.Definition.Id is not "FR_VEHICLES" and not "30WEST_HEMS" and not "ANDREW_MISSION_PACK" and not "H145_HEMS_OBJECTS"), "Optional packages must remain optional.");
            Equal(999, results.Single(result => result.Definition.Id == "H145_HEMS_OBJECTS").Definition.InstallOrder);
            Equal(21, results.Single(result => result.Definition.Id == "30WEST_SMOKE").Definition.InstallOrder);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void H145DetectionReportsActionPack()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
            var basePack = Directory.CreateDirectory(Path.Combine(community, "hpg-airbus-h145")).FullName;
            File.WriteAllText(Path.Combine(basePack, "Version_491.txt"), string.Empty);
            Directory.CreateDirectory(Path.Combine(community, "hpg-airbus-h145-ap"));
            using var engine = new InstallerEngine();
            var detected = engine.DetectH145(community);
            True(detected.Installed, "H145 Base Pack should be detected.");
            True(detected.ActionPackInstalled, "H145 Action Pack should be detected independently.");
            Equal(491, detected.Version);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void FullInstallationInitialScanDoesNotRequireArchives()
    {
        True(
            InstallationFlowPolicy.CanContinueFromArchiveStep(
                InstallerMode.FullInstallation,
                inputCount: 0,
                exitAvailable: false),
            "The initial Community scan must not block access to the archive review step.");
        False(
            InstallationFlowPolicy.CanContinueFromArchiveStep(
                InstallerMode.AndrewUpdateOnly,
                inputCount: 0,
                exitAvailable: false),
            "The dedicated Andrew updater still requires an update source.");
        True(
            InstallationFlowPolicy.CanContinueFromArchiveStep(
                InstallerMode.AndrewUpdateOnly,
                inputCount: 1,
                exitAvailable: false),
            "The dedicated Andrew updater may continue after a source is added.");
    }

    private static void AndrewUpdaterDetectsInputKinds()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cumulative = Path.Combine(root, "everywhere_all.json");
            File.WriteAllText(cumulative, CreateAndrewJson("0.99634", "1.2", "(BETA)"));
            var stable = Path.Combine(root, "random-everywhere-stable.zip");
            CreateZip(stable, new Dictionary<string, string>
            {
                ["wrapper/hpg-airbus-h145-z-andrew-mission-pack/manifest.json"] = "{}",
                ["wrapper/hpg-airbus-h145-z-andrew-mission-pack/layout.json"] = "{}",
                ["wrapper/hpg-airbus-h145-z-andrew-mission-pack/hpgmission/RANDOM AND EVERYWHERE/everywhere_all.json"] = CreateAndrewJson("0.99634", "1.2", "(BETA)")
            });

            using var engine = new InstallerEngine();
            var cumulativeResult = engine.InspectAndrewUpdate(cumulative);
            Equal(AndrewUpdateKind.Cumulative, cumulativeResult.Kind);
            Equal("0.99634 1.2 (BETA)", cumulativeResult.Version?.Display);
            var stableResult = engine.InspectAndrewUpdate(stable);
            Equal(AndrewUpdateKind.Stable, stableResult.Kind);
            Equal("hpg-airbus-h145-z-andrew-mission-pack", stableResult.StablePackageRoot?.FolderName);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void AndrewUpdaterSelectsNewestCumulativeJson()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var olderDirectory = Directory.CreateDirectory(Path.Combine(root, "older")).FullName;
            var newerDirectory = Directory.CreateDirectory(Path.Combine(root, "newer")).FullName;
            var older = Path.Combine(olderDirectory, "everywhere_all.json");
            var newer = Path.Combine(newerDirectory, "everywhere_all.json");
            File.WriteAllText(older, CreateAndrewJson("0.99634", "1.2", "(BETA)"));
            File.WriteAllText(newer, CreateAndrewJson("0.99634", "1.3", "(RELEASE CANDIDATE)"));

            using var engine = new InstallerEngine();
            var selected = engine.SelectNewestCumulativeUpdate([older, newer]);

            Equal(Path.GetFullPath(newer), selected.SourcePath);
            Equal("0.99634", selected.Version?.MissionVersion);
            Equal("1.3", selected.Version?.DevVersion);
            Equal("(RELEASE CANDIDATE)", selected.Version?.DevType);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void AndrewUpdaterAcceptsRenamedCumulativeJson()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cumulative = Path.Combine(root, "everywhere.json");
            File.WriteAllText(cumulative, CreateAndrewJson("0.99634", "1.3", "(BETA)"));

            using var engine = new InstallerEngine();
            var result = engine.InspectAndrewUpdate(cumulative);

            Equal(AndrewUpdateKind.Cumulative, result.Kind);
            Equal(Path.GetFullPath(cumulative), result.SourcePath);
            Equal("0.99634 1.3 (BETA)", result.Version?.Display);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void AndrewCumulativeUpdateRequiresPrimaryPackage()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
            var source = Path.Combine(root, "everywhere_all.json");
            File.WriteAllText(source, CreateAndrewJson("0.99634", "1.3", "(BETA)"));
            using var engine = new InstallerEngine();
            var update = engine.InspectAndrewUpdate(source);
            var blocked = false;

            try
            {
                _ = engine.InstallAndrewUpdateAsync(
                    new AndrewUpdateRequest(community, update),
                    new Progress<InstallerProgress>(),
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InstallerException exception)
            {
                blocked = exception.Code == "ANDREW-003";
            }

            True(blocked, "The standalone JSON must not be installed without the primary Random & Everywhere package.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void AndrewCumulativeUpdatePreventsDowngrade()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = CreateAndrewTestCommunity(root);
            var target = Path.Combine(
                community,
                "hpg-airbus-h145-z-andrew-mission-pack",
                "hpgmission",
                "RANDOM AND EVERYWHERE",
                "everywhere_all.json");
            var installedJson = CreateAndrewJson("0.99634", "1.3", "(BETA)");
            File.WriteAllText(target, installedJson);
            var source = Path.Combine(root, "everywhere_all.json");
            File.WriteAllText(source, CreateAndrewJson("0.99634", "1.2", "(ALPHA)"));
            using var engine = new InstallerEngine();
            var update = engine.InspectAndrewUpdate(source);
            var blocked = false;

            try
            {
                _ = engine.InstallAndrewUpdateAsync(
                    new AndrewUpdateRequest(community, update),
                    new Progress<InstallerProgress>(),
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InstallerException exception)
            {
                blocked = exception.Code == "ANDREW-006";
            }

            True(blocked, "An older cumulative JSON must not replace a newer installed version.");
            Equal(installedJson, File.ReadAllText(target));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void AndrewCumulativeUpdateReplacesJson()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = CreateAndrewTestCommunity(root);
            var package = Path.Combine(community, "hpg-airbus-h145-z-andrew-mission-pack");
            var target = Path.Combine(package, "hpgmission", "RANDOM AND EVERYWHERE", "everywhere_all.json");
            File.WriteAllText(target, CreateAndrewJson("0.99634", "1.2", "(BETA)"));
            File.WriteAllText(Path.Combine(package, "keep.txt"), "keep");
            var source = Path.Combine(root, "everywhere_all.json");
            var updatedJson = CreateAndrewJson("0.99634", "1.3", "(BETA)");
            File.WriteAllText(source, updatedJson);

            using var engine = new InstallerEngine();
            var update = engine.InspectAndrewUpdate(source);
            var result = engine.InstallAndrewUpdateAsync(
                new AndrewUpdateRequest(community, update),
                new Progress<InstallerProgress>(),
                CancellationToken.None).GetAwaiter().GetResult();

            True(result.Success, "Cumulative update should succeed.");
            Equal(updatedJson, File.ReadAllText(target));
            Equal("keep", File.ReadAllText(Path.Combine(package, "keep.txt")));
            AssertSessionsCleaned(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void FullInstallationAppliesJsonAfterPrimaryPackage()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
            var tools = Directory.CreateDirectory(Path.Combine(community, "hpg-airbus-h145", "Tools")).FullName;
            File.WriteAllText(
                Path.Combine(tools, "Update Mission Index.cmd"),
                "@echo {\"generated\":true}> \"..\\html_ui\\HPGH145-System\\GeneratedMissionIndex.json\"\r\n@exit /b 0");

            var archive = Path.Combine(sourceDirectory, "andrew-primary.zip");
            var bundledJson = CreateAndrewJson("0.99634", "1.2", "(BETA)");
            CreateZip(archive, new Dictionary<string, string>
            {
                ["hpg-airbus-h145-z-andrew-mission-pack/manifest.json"] = "{}",
                ["hpg-airbus-h145-z-andrew-mission-pack/layout.json"] = "{}",
                ["hpg-airbus-h145-z-andrew-mission-pack/hpgmission/RANDOM AND EVERYWHERE/everywhere_all.json"] = bundledJson
            });
            var standalone = Path.Combine(sourceDirectory, "everywhere_all.json");
            var newestJson = CreateAndrewJson("0.99634", "1.3", "(RELEASE CANDIDATE)");
            File.WriteAllText(standalone, newestJson);

            var definition = new PackageDefinition(
                "ANDREW_MISSION_PACK",
                "Andrew Mission Pack",
                true,
                90,
                ".*",
                new HashSet<SimulatorType> { SimulatorType.Msfs2020 },
                string.Empty);
            var scan = new PackageScanResult(
                definition,
                PackageState.Found,
                archive,
                [new DetectedPackageRoot(
                    "hpg-airbus-h145-z-andrew-mission-pack",
                    "hpg-airbus-h145-z-andrew-mission-pack")],
                null);

            using var engine = new InstallerEngine();
            var cumulative = engine.InspectAndrewUpdate(standalone);
            var result = engine.InstallAsync(
                new InstallationRequest(
                    SimulatorType.Msfs2020,
                    community,
                    [scan],
                    false,
                    cumulative),
                new Progress<InstallerProgress>(),
                CancellationToken.None).GetAwaiter().GetResult();

            True(result.Success, "The primary package and cumulative JSON should install in one transaction.");
            var installedJson = Path.Combine(
                community,
                "hpg-airbus-h145-z-andrew-mission-pack",
                "hpgmission",
                "RANDOM AND EVERYWHERE",
                "everywhere_all.json");
            Equal(newestJson, File.ReadAllText(installedJson));
            AssertSessionsCleaned(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void AndrewStableUpdateReplacesPackage()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = CreateAndrewTestCommunity(root);
            var package = Path.Combine(community, "hpg-airbus-h145-z-andrew-mission-pack");
            File.WriteAllText(Path.Combine(package, "obsolete.txt"), "remove-me");
            var stable = Path.Combine(root, "andrew-stable.zip");
            CreateZip(stable, new Dictionary<string, string>
            {
                ["release/hpg-airbus-h145-z-andrew-mission-pack/manifest.json"] = "{\"package_version\":\"2.0\"}",
                ["release/hpg-airbus-h145-z-andrew-mission-pack/layout.json"] = "{}",
                ["release/hpg-airbus-h145-z-andrew-mission-pack/hpgmission/RANDOM AND EVERYWHERE/everywhere_all.json"] = CreateAndrewJson("0.99634", "1.3", "(STABLE)"),
                ["release/hpg-airbus-h145-z-andrew-mission-pack/new-file.txt"] = "stable"
            });

            using var engine = new InstallerEngine();
            var update = engine.InspectAndrewUpdate(stable);
            var result = engine.InstallAndrewUpdateAsync(
                new AndrewUpdateRequest(community, update),
                new Progress<InstallerProgress>(),
                CancellationToken.None).GetAwaiter().GetResult();

            True(result.Success, "Stable update should succeed.");
            False(File.Exists(Path.Combine(package, "obsolete.txt")), "Stable replacement must remove files from the old package.");
            Equal("stable", File.ReadAllText(Path.Combine(package, "new-file.txt")));
            AssertSessionsCleaned(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void FullInstallationAppliesOnlyCumulativeJson()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = CreateAndrewTestCommunity(root);
            var target = Path.Combine(
                community,
                "hpg-airbus-h145-z-andrew-mission-pack",
                "hpgmission",
                "RANDOM AND EVERYWHERE",
                "everywhere_all.json");
            File.WriteAllText(target, CreateAndrewJson("0.99634", "1.2", "(BETA)"));
            var source = Path.Combine(root, "everywhere.json");
            var updatedJson = CreateAndrewJson("0.99634", "1.3", "(BETA)");
            File.WriteAllText(source, updatedJson);

            using var engine = new InstallerEngine();
            var update = engine.InspectAndrewUpdate(source);
            var result = engine.InstallAsync(
                new InstallationRequest(
                    SimulatorType.Msfs2020,
                    community,
                    [],
                    false,
                    update),
                new Progress<InstallerProgress>(),
                CancellationToken.None).GetAwaiter().GetResult();

            True(result.Success, "A cumulative JSON must be installable without adding a package archive when the primary package is installed.");
            Equal(0, result.InstalledPackages);
            Equal(updatedJson, File.ReadAllText(target));
            AssertSessionsCleaned(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void InstallationFlowExitsWhenRequiredPackagesAreInstalled()
    {
        var simulator = new HashSet<SimulatorType> { SimulatorType.Msfs2024 };
        var required = new PackageDefinition("REQ", "Required", true, 1, ".*", simulator, string.Empty);
        var optional = new PackageDefinition("OPT", "Optional", false, 2, ".*", simulator, string.Empty);
        PackageScanResult[] installedInventory =
        [
            new(required, PackageState.Installed, null, [new DetectedPackageRoot("required", "required")], null),
            new(optional, PackageState.OptionalMissing, null, [], null)
        ];

        True(
            InstallationFlowPolicy.ShouldExitBeforeAddingInputs(installedInventory, 0),
            "The archive step should offer Exit when every required package is already installed.");
        True(
            InstallationFlowPolicy.ShouldExitAfterScan(installedInventory, null, true),
            "The package-check step should offer Exit when a completed scan found no pending changes.");
        False(
            InstallationFlowPolicy.ShouldExitAfterScan(installedInventory, null, false),
            "A failed or incomplete scan must not be treated as a no-op success.");
    }

    private static string CreateAndrewTestCommunity(string root)
    {
        var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
        var h145 = Directory.CreateDirectory(Path.Combine(community, "hpg-airbus-h145")).FullName;
        File.WriteAllText(Path.Combine(h145, "Version_500.txt"), string.Empty);
        Directory.CreateDirectory(Path.Combine(community, "hpg-airbus-h145-ap"));
        var tools = Directory.CreateDirectory(Path.Combine(h145, "Tools")).FullName;
        File.WriteAllText(
            Path.Combine(tools, "Update Mission Index.cmd"),
            "@echo {\"generated\":true}> \"..\\html_ui\\HPGH145-System\\GeneratedMissionIndex.json\"\r\n@pause\r\n@exit /b 0");
        var package = Directory.CreateDirectory(Path.Combine(community, "hpg-airbus-h145-z-andrew-mission-pack")).FullName;
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(package, "layout.json"), "{}");
        Directory.CreateDirectory(Path.Combine(package, "hpgmission", "RANDOM AND EVERYWHERE"));
        return community;
    }

    private static void AssertSessionsCleaned(string root)
    {
        var sessions = Path.Combine(root, ".RandomAndEverywhereInstaller", "Sessions");
        True(!Directory.Exists(sessions) || !Directory.EnumerateDirectories(sessions).Any(), "Successful update must remove staging and backup data.");
    }

    private static void TransactionalInstallerInstallsPackage()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
            var tools = Directory.CreateDirectory(Path.Combine(community, "hpg-airbus-h145", "Tools")).FullName;
            File.WriteAllText(
                Path.Combine(tools, "Update Mission Index.cmd"),
                "@echo {\"generated\":true}> \"..\\html_ui\\HPGH145-System\\GeneratedMissionIndex.json\"\r\n@pause\r\n@exit /b 0");
            var archive = Path.Combine(source, "test.zip");
            CreateZip(archive, new Dictionary<string, string>
            {
                ["test-package/manifest.json"] = "{}",
                ["test-package/layout.json"] = "{}",
                ["test-package/data/file.txt"] = "installed"
            });
            var definition = new PackageDefinition("TEST", "Test Package", true, 1, ".*", new HashSet<SimulatorType> { SimulatorType.Msfs2020 }, string.Empty);
            var scan = new PackageScanResult(definition, PackageState.Found, archive, [new DetectedPackageRoot("test-package", "test-package")], null);
            var missingDefinition = new PackageDefinition("MISSING", "Missing Required Package", true, 2, ".*", new HashSet<SimulatorType> { SimulatorType.Msfs2020 }, string.Empty);
            var missing = new PackageScanResult(missingDefinition, PackageState.RequiredMissing, null, [], "missing");
            using var engine = new InstallerEngine();
            var result = engine.InstallAsync(new InstallationRequest(SimulatorType.Msfs2020, community, [scan, missing], true), new Progress<InstallerProgress>(), CancellationToken.None).GetAwaiter().GetResult();
            True(result.Success, "Installation should succeed.");
            True(result.Warnings.Any(warning => warning.Contains("Missing Required Package", StringComparison.Ordinal)), "Explicit missing-package override must be recorded as a warning.");
            True(File.Exists(Path.Combine(community, "test-package", "data", "file.txt")), "Installed file missing.");
            True(File.Exists(Path.Combine(community, "hpg-airbus-h145", "html_ui", "HPGH145-System", "GeneratedMissionIndex.json")), "Mission index destination must be created before running the updater.");
            var sessions = Path.Combine(root, ".RandomAndEverywhereInstaller", "Sessions");
            True(!Directory.Exists(sessions) || !Directory.EnumerateDirectories(sessions).Any(), "Successful installation must clean its temporary staging and backup session.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void PackageScannerResolvesNestedCommunityRoot()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var archive = Path.Combine(root, "Pack vehicules FR v2_RANDOM.zip");
            CreateZip(archive, new Dictionary<string, string>
            {
                ["download-wrapper/release/files/Community/br-mission-dynamicobjectsample/SimObjects/file.cfg"] = "content",
                ["download-wrapper/release/README.txt"] = "documentation"
            });
            using var engine = new InstallerEngine();
            var results = engine.ScanPackagesAsync([archive], SimulatorType.Msfs2020, new Progress<InstallerProgress>(), CancellationToken.None).GetAwaiter().GetResult();
            var package = results.Single(result => result.Definition.Id == "FR_VEHICLES");
            Equal(PackageState.Found, package.State);
            Equal("br-mission-dynamicobjectsample", package.Roots.Single().FolderName);
            Equal("download-wrapper/release/files/Community/br-mission-dynamicobjectsample", package.Roots.Single().RelativePath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void InstallerPreservesUnrelatedCommunityFolders()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
            var tools = Directory.CreateDirectory(Path.Combine(community, "hpg-airbus-h145", "Tools")).FullName;
            File.WriteAllText(
                Path.Combine(tools, "Update Mission Index.cmd"),
                "@echo {\"generated\":true}> \"..\\html_ui\\HPGH145-System\\GeneratedMissionIndex.json\"\r\n@exit /b 0");

            var unrelated = Directory.CreateDirectory(Path.Combine(community, "user-unrelated-addon")).FullName;
            var unrelatedNested = Directory.CreateDirectory(Path.Combine(unrelated, "UserData", "Profiles")).FullName;
            var unrelatedLayout = Path.Combine(unrelated, "layout.json");
            var unrelatedData = Path.Combine(unrelatedNested, "personal-settings.json");
            File.WriteAllText(unrelatedLayout, "{\"unrelated\":true}");
            File.WriteAllText(unrelatedData, "{\"keep\":\"exactly\"}");
            var dataTimestamp = File.GetLastWriteTimeUtc(unrelatedData);

            var archive = Path.Combine(source, "safe-package.zip");
            CreateZip(archive, new Dictionary<string, string>
            {
                ["safe-package/manifest.json"] = "{}",
                ["safe-package/layout.json"] = "{}",
                ["safe-package/data/file.txt"] = "installed"
            });
            var definition = new PackageDefinition(
                "SAFE",
                "Safe Package",
                true,
                1,
                ".*",
                new HashSet<SimulatorType> { SimulatorType.Msfs2020 },
                string.Empty);
            var scan = new PackageScanResult(
                definition,
                PackageState.Found,
                archive,
                [new DetectedPackageRoot("safe-package", "safe-package")],
                null);

            using var engine = new InstallerEngine();
            var result = engine.InstallAsync(
                    new InstallationRequest(SimulatorType.Msfs2020, community, [scan]),
                    new Progress<InstallerProgress>(),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            True(result.Success, "Installation should succeed.");
            Equal(1, result.UpdatedLayouts);
            Equal("{\"unrelated\":true}", File.ReadAllText(unrelatedLayout));
            Equal("{\"keep\":\"exactly\"}", File.ReadAllText(unrelatedData));
            Equal(dataTimestamp, File.GetLastWriteTimeUtc(unrelatedData));
            True(Directory.Exists(unrelatedNested), "Unrelated directory hierarchy must remain present.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void RegularInstallCannotOverwriteH145Core()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
            var h145 = Directory.CreateDirectory(Path.Combine(community, "hpg-airbus-h145")).FullName;
            var protectedFile = Path.Combine(h145, "user-owned.cfg");
            File.WriteAllText(protectedFile, "must-remain");
            var archive = Path.Combine(source, "unexpected-core.zip");
            CreateZip(archive, new Dictionary<string, string>
            {
                ["hpg-airbus-h145/manifest.json"] = "{}",
                ["hpg-airbus-h145/layout.json"] = "{}",
                ["hpg-airbus-h145/user-owned.cfg"] = "overwritten"
            });
            var definition = new PackageDefinition(
                "UNEXPECTED_CORE",
                "Unexpected Core",
                true,
                1,
                ".*",
                new HashSet<SimulatorType> { SimulatorType.Msfs2020 },
                string.Empty);
            var scan = new PackageScanResult(
                definition,
                PackageState.Found,
                archive,
                [new DetectedPackageRoot("hpg-airbus-h145", "hpg-airbus-h145")],
                null);

            using var engine = new InstallerEngine();
            var blocked = false;
            try
            {
                _ = engine.InstallAsync(
                        new InstallationRequest(SimulatorType.Msfs2020, community, [scan]),
                        new Progress<InstallerProgress>(),
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (InstallerException exception)
            {
                blocked = exception.Code == "PATH-003";
            }

            True(blocked, "A regular package archive must not be allowed to target H145 core folders.");
            Equal("must-remain", File.ReadAllText(protectedFile));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void TransactionalInstallerRollsBack()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
            var tools = Directory.CreateDirectory(Path.Combine(community, "hpg-airbus-h145", "Tools")).FullName;
            var indexDirectory = Directory.CreateDirectory(
                Path.Combine(community, "hpg-airbus-h145", "html_ui", "HPGH145-System")).FullName;
            var missionIndex = Path.Combine(indexDirectory, "GeneratedMissionIndex.json");
            File.WriteAllText(missionIndex, "{\"original\":true}");
            var invalidMission = Directory.CreateDirectory(Path.Combine(community, "unrelated", "hpgmission")).FullName;
            File.WriteAllText(Path.Combine(invalidMission, "invalid.json"), "{broken");
            File.WriteAllText(
                Path.Combine(tools, "Update Mission Index.cmd"),
                "@echo {\"modified\":true}> \"..\\html_ui\\HPGH145-System\\GeneratedMissionIndex.json\"\r\n@exit /b 7");
            var existing = Directory.CreateDirectory(Path.Combine(community, "rollback-package")).FullName;
            File.WriteAllText(Path.Combine(existing, "manifest.json"), "old-manifest");
            File.WriteAllText(Path.Combine(existing, "layout.json"), "old-layout");
            File.WriteAllText(Path.Combine(existing, "file.txt"), "old-content");
            File.WriteAllText(Path.Combine(existing, "untouched.bin"), "must-remain");
            var archive = Path.Combine(source, "test.zip");
            CreateZip(archive, new Dictionary<string, string>
            {
                ["rollback-package/manifest.json"] = "{}",
                ["rollback-package/layout.json"] = "{}",
                ["rollback-package/file.txt"] = "new-content",
                ["rollback-package/new-file.txt"] = "created"
            });
            var definition = new PackageDefinition("ROLLBACK", "Rollback Package", true, 1, ".*", new HashSet<SimulatorType> { SimulatorType.Msfs2020 }, string.Empty);
            var scan = new PackageScanResult(definition, PackageState.Found, archive, [new DetectedPackageRoot("rollback-package", "rollback-package")], null);
            using var engine = new InstallerEngine();
            var failed = false;
            try
            {
                _ = engine.InstallAsync(new InstallationRequest(SimulatorType.Msfs2020, community, [scan]), new Progress<InstallerProgress>(), CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InstallerException exception)
            {
                failed = exception.Code is "INDEX-JSON" or "INST-010";
            }
            True(failed, "Invalid mission input must fail native indexing and trigger rollback.");
            True(Directory.Exists(existing), "Pre-existing package must remain after rollback.");
            Equal("old-content", File.ReadAllText(Path.Combine(existing, "file.txt")));
            Equal("must-remain", File.ReadAllText(Path.Combine(existing, "untouched.bin")));
            Equal("{\"original\":true}", File.ReadAllText(missionIndex));
            False(File.Exists(Path.Combine(existing, "new-file.txt")), "Files created by the failed transaction must be removed.");
            var sessions = Path.Combine(root, ".RandomAndEverywhereInstaller", "Sessions");
            True(!Directory.Exists(sessions) || !Directory.EnumerateDirectories(sessions).Any(), "Successful rollback must clean its temporary session.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void BuiltInLayoutGeneratorCreatesVerifiedLayout()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var package = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
            var nested = Directory.CreateDirectory(Path.Combine(package, "SimObjects", "Airplanes", "Test Aircraft")).FullName;
            File.WriteAllText(
                Path.Combine(package, "manifest.json"),
                "{\"total_package_size\":\"00000000000000000000\",\"package_version\":\"1.0.0\"}");
            var layout = Path.Combine(package, "layout.json");
            File.WriteAllText(layout, "{}");
            var cfg = Path.Combine(nested, "aircraft.cfg");
            var texture = Path.Combine(nested, "texture ü.png");
            File.WriteAllText(cfg, "configuration");
            File.WriteAllBytes(texture, [1, 2, 3, 4, 5]);

            InstallerEngine.GenerateLayoutFile(layout);

            True(InstallerEngine.TryValidateGeneratedLayout(layout, out var reason), $"Generated layout must validate: {reason}");
            using var document = JsonDocument.Parse(File.ReadAllText(layout));
            var content = document.RootElement.GetProperty("content").EnumerateArray().ToArray();
            Equal(2, content.Length);
            True(content.Any(item => item.GetProperty("path").GetString() == "SimObjects/Airplanes/Test Aircraft/aircraft.cfg"), "Nested CFG path missing from layout.");
            True(content.Any(item => item.GetProperty("path").GetString() == "SimObjects/Airplanes/Test Aircraft/texture ü.png"), "Unicode texture path missing from layout.");
            var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "manifest.json")));
            True(
                manifest.RootElement.GetProperty("total_package_size").GetString() != "00000000000000000000",
                "Manifest total_package_size must be refreshed when the property exists.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void LayoutValidationRejectsStaleContent()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var package = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
            File.WriteAllText(Path.Combine(package, "manifest.json"), "{}");
            var layout = Path.Combine(package, "layout.json");
            File.WriteAllText(layout, "{\"content\":[]}");
            File.WriteAllText(Path.Combine(package, "new-file.txt"), "not represented by the stale layout");

            False(
                InstallerEngine.TryValidateGeneratedLayout(layout, out var reason),
                "A layout that omits an existing package file must be rejected.");
            True(reason.Contains("count", StringComparison.OrdinalIgnoreCase) || reason.Contains("missing", StringComparison.OrdinalIgnoreCase), "Validation should explain the stale layout.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void InstallationRegeneratesScopedLayoutsWithoutNetwork()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
            var existingPackage = Directory.CreateDirectory(Path.Combine(community, "unrelated-existing-package")).FullName;
            File.WriteAllText(Path.Combine(existingPackage, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(existingPackage, "layout.json"), "{\"content\":[]}");
            File.WriteAllText(Path.Combine(existingPackage, "existing-data.txt"), "must be refreshed too");
            var tools = Directory.CreateDirectory(Path.Combine(community, "hpg-airbus-h145", "Tools")).FullName;
            File.WriteAllText(
                Path.Combine(tools, "Update Mission Index.cmd"),
                "@echo {\"generated\":true}> \"..\\html_ui\\HPGH145-System\\GeneratedMissionIndex.json\"\r\n@exit /b 0");
            var archive = Path.Combine(source, "fallback-layout.zip");
            CreateZip(archive, new Dictionary<string, string>
            {
                ["fallback-package/manifest.json"] = "{}",
                ["fallback-package/layout.json"] = "{}",
                ["fallback-package/data/new-file.txt"] = "must appear in layout"
            });
            var definition = new PackageDefinition(
                "FALLBACK",
                "Fallback Package",
                true,
                1,
                ".*",
                new HashSet<SimulatorType> { SimulatorType.Msfs2020 },
                string.Empty);
            var scan = new PackageScanResult(
                definition,
                PackageState.Found,
                archive,
                [new DetectedPackageRoot("fallback-package", "fallback-package")],
                null);
            var requests = 0;
            using var handler = new StubHttpMessageHandler(request =>
            {
                requests++;
                return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { RequestMessage = request };
            });
            using var engine = new InstallerEngine(handler);

            var result = engine.InstallAsync(
                    new InstallationRequest(SimulatorType.Msfs2020, community, [scan]),
                    new Progress<InstallerProgress>(),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            var layout = Path.Combine(community, "fallback-package", "layout.json");

            True(result.Success, "Installation should succeed with the built-in layout generator.");
            Equal(1, result.UpdatedLayouts);
            Equal(0, requests);
            True(InstallerEngine.TryValidateGeneratedLayout(layout, out var reason), $"Installed package layout must validate: {reason}");
            True(File.ReadAllText(layout).Contains("data/new-file.txt", StringComparison.Ordinal), "Installed package layout must include the installed file.");
            var existingLayout = Path.Combine(existingPackage, "layout.json");
            Equal("{\"content\":[]}", File.ReadAllText(existingLayout));
            Equal("must be refreshed too", File.ReadAllText(Path.Combine(existingPackage, "existing-data.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }


    private static void CompanionAudioModesStaySeparate()
    {
        False(CompanionAudioPolicy.AllowsSynthesizedSpeech(CompanionAudioMode.Recorded), "Recorded mode must never synthesize or fall back to TTS.");
        True(CompanionAudioPolicy.AllowsSynthesizedSpeech(CompanionAudioMode.Synthetic), "Synthetic mode must permit speech.");
        False(CompanionAudioPolicy.ShouldPlayCounter(null, 42), "Initial counter values must establish a baseline.");
        False(CompanionAudioPolicy.ShouldPlayCounter(42, 42), "Unchanged counters must not replay.");
        False(CompanionAudioPolicy.ShouldPlayCounter(42, 0), "Counter resets must not play.");
        True(CompanionAudioPolicy.ShouldPlayCounter(42, 43), "An increment must trigger playback.");
    }

    private static void CompanionStartupUsesInstalledInventory()
    {
        var installed = PackageCatalog.All.Select(definition => new PackageScanResult(
            definition, definition.Required ? PackageState.Installed : PackageState.OptionalMissing,
            null, [], null)).ToArray();
        Equal(CompanionStartupDestination.Audio, CompanionStartupPolicy.Resolve(true, installed));
        Equal(CompanionStartupDestination.SelectCommunity, CompanionStartupPolicy.Resolve(false, installed));
        Equal(CompanionStartupDestination.SelectCommunity, CompanionStartupPolicy.Resolve(true, []));
        var requiredIndex = Array.FindIndex(installed, package => package.Definition.Required);
        var missing = installed.ToArray();
        missing[requiredIndex] = missing[requiredIndex] with { State = PackageState.RequiredMissing };
        Equal(CompanionStartupDestination.InstallPackages, CompanionStartupPolicy.Resolve(true, missing));
        missing[requiredIndex] = missing[requiredIndex] with { State = PackageState.Found, ArchivePath = "downloaded.zip" };
        Equal(CompanionStartupDestination.InstallPackages, CompanionStartupPolicy.Resolve(true, missing));
    }

    private static void CompanionSettingsPersist()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new CompanionSettingsStore(Path.Combine(root, "settings.json"));
            False(store.Load().WelcomeShown, "A new profile must show the startup notice.");
            store.Save(new CompanionSettings { Culture = "it-IT", CommunityPath = @"D:\Community", AudioStoragePath = @"E:\Audio", AudioMode = CompanionAudioMode.Synthetic, WelcomeShown = true });
            var restored = store.Load();
            Equal("it-IT", restored.Culture);
            Equal(@"D:\Community", restored.CommunityPath);
            Equal(@"E:\Audio", restored.AudioStoragePath);
            Equal(CompanionAudioMode.Synthetic, restored.AudioMode);
            True(restored.WelcomeShown, "The startup notice must remain dismissed across sessions.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RandomEverywhereInstaller.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void CompanionUiHasValidResources()
    {
        var root = FindRepositoryRoot();
        var app = Path.Combine(root, "src", "RandomEverywhereInstaller.App");
        var xaml = File.ReadAllText(Path.Combine(app, "CompanionWindow.xaml")) +
                   File.ReadAllText(Path.Combine(app, "CompanionOptionsView.xaml")) +
                   File.ReadAllText(Path.Combine(app, "WorldDataView.xaml"));
        var keys = ReadValues(LoadLocalizationDocuments()["en-US"]);
        foreach (Match match in Regex.Matches(xaml, @"L\[([^\]]+)\]"))
            True(keys.ContainsKey(match.Groups[1].Value), $"Unknown UI localization key: {match.Groups[1].Value}");
        foreach (Match match in Regex.Matches(xaml, "Source=\"(Assets/[^\"]+)\""))
            True(File.Exists(Path.Combine(app, match.Groups[1].Value)), $"Missing image: {match.Groups[1].Value}");
        True(xaml.Contains("Value=\"{Binding Progress, Mode=OneWay}\"", StringComparison.Ordinal), "Read-only progress must not use a TwoWay binding.");
    }

    private static void CompanionConnectionControlsStayVisible()
    {
        var app = Path.Combine(FindRepositoryRoot(), "src", "RandomEverywhereInstaller.App");
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(app, "CompanionWindow.xaml"));
        var shell = document.Root!.Element(ui + "Border")!.Element(ui + "Grid")!;
        var bar = shell.Elements(ui + "Border").Single(element => (string?)element.Attribute(x + "Name") == "SimulatorConnectionBar");
        Equal("2", (string)bar.Attribute("Grid.Row")!);
        var rows = shell.Element(ui + "Grid.RowDefinitions")!.Elements(ui + "RowDefinition").ToArray();
        Equal("Auto", (string)rows[2].Attribute("Height")!);
        foreach (var (name, command, label) in new[]
        {
            ("ConnectSimulatorButton", "StartCommand", "C.Connection.Connect"),
            ("DisconnectSimulatorButton", "StopCommand", "C.Connection.Disconnect")
        })
        {
            var button = bar.Descendants(ui + "Button").Single(element => (string?)element.Attribute(x + "Name") == name);
            Equal("{Binding " + command + "}", (string)button.Attribute("Command")!);
            Equal("{Binding L[" + label + "]}", (string)button.Attribute("Content")!);
            False(button.Ancestors().Any(element => element.Attribute("IsEnabled") is not null),
                "Session controls must not inherit the audio editor's disabled state.");
        }
        var theme = XDocument.Load(Path.Combine(app, "Themes", "Theme.xaml"));
        var baseButton = theme.Root!.Elements(ui + "Style").Single(element => (string?)element.Attribute(x + "Key") == "BaseButton");
        var alignment = baseButton.Elements(ui + "Setter").Single(element => (string?)element.Attribute("Property") == "VerticalAlignment");
        Equal("Center", (string)alignment.Attribute("Value")!);
    }

    private static void RecordedLibraryControlsAreSeparate()
    {
        var app = Path.Combine(FindRepositoryRoot(), "src", "RandomEverywhereInstaller.App");
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(app, "CompanionOptionsView.xaml"));
        var recorded = document.Descendants(ui + "Grid").Single(element => (string?)element.Attribute(x + "Name") == "RecordedOptions");
        Equal("{Binding IsRecordedMode, Converter={StaticResource BoolVisibility}}", (string)recorded.Element(ui + "Border")!.Attribute("Visibility")!);
        var commands = recorded.Descendants(ui + "Button").Select(element => (string?)element.Attribute("Command")).ToArray();
        foreach (var name in new[] { "RegisterRecordedCommand", "ReloadRecordedCommand", "ForgetRecordedCommand", "PreviewRecordedSoundCommand" })
            True(commands.Contains("{Binding " + name + "}"), $"Missing recorded library control: {name}");
        False(commands.Contains("{Binding ImportPackCommand}"), "Recorded packs must not use synthetic text-pack import.");
        var main = XDocument.Load(Path.Combine(app, "CompanionWindow.xaml"));
        var mute = main.Descendants(ui + "Button").Single(element => (string?)element.Attribute("Command") == "{Binding ToggleRecordedMuteCommand}");
        False(mute.Ancestors().Any(element => (string?)element.Attribute("IsEnabled") == "{Binding CanEditAudio}"),
            "Master mute must remain available during playback.");
        Equal("{Binding IsRecordedMode, Converter={StaticResource BoolVisibility}}", (string)mute.Attribute("Visibility")!);
    }

    private static void CompanionAssemblyEmbedsRuntime()
    {
        // Inspect the configuration under test, never a stale Release artifact.
        var configuration = typeof(Program).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            ?? throw new InvalidOperationException("The test assembly has no build configuration.");
        var assembly = Path.Combine(FindRepositoryRoot(), "src", "RandomEverywhereInstaller.App", "bin", configuration, "net10.0-windows", "RandomAndEverywhereCompanion.dll");
        using var input = File.OpenRead(assembly);
        using var pe = new PEReader(input);
        var metadata = pe.GetMetadataReader();
        var names = metadata.ManifestResources.Select(handle => metadata.GetString(metadata.GetManifestResource(handle).Name)).ToHashSet(StringComparer.Ordinal);
        foreach (var name in new[] { "CompanionRuntime.exe", "CompanionRuntime.exe.config", "NAudio.Core.dll", "NAudio.WinMM.dll", "NAudio.Wasapi.dll" })
            True(names.Contains("CompanionPayload." + name), $"The single-file build must embed {name}.");
        foreach (var culture in new[] { "en-US", "de-DE", "fr-FR", "es-ES", "it-IT" })
            True(names.Contains("RandomEverywhereInstaller.App.Localization." + culture + ".json"), $"Missing embedded language: {culture}");
        True(names.Contains("CompanionPayload.SimConnect.dll"), "The approved native SimConnect DLL must be bundled.");
        True(names.Contains("CompanionPayload.SIMCONNECT-NOTICE.md"), "SimConnect provenance and notices must accompany the payload.");
        var simResource = metadata.ManifestResources.Select(metadata.GetManifestResource)
            .Single(resource => metadata.GetString(resource.Name) == "CompanionPayload.SimConnect.dll");
        var data = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress).GetContent().AsSpan();
        var offset = checked((int)simResource.Offset);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        Equal("BD15E8A094762BBDF32320F549B174340B8F1E58B63624DCC93BC50507E7B619", Convert.ToHexString(SHA256.HashData(data.Slice(offset + 4, length))));
    }

    private static void DebugCleanupPreservesUserFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var work = Path.Combine(root, "work");
            Directory.CreateDirectory(work);
            File.WriteAllText(Path.Combine(work, "table_R&E_debug_sample.json"), "debug snapshot");
            foreach (var name in new[] { "global.json", "savedgame.json", "personal-notes.txt" })
                File.WriteAllText(Path.Combine(work, name), "user content");
            var result = CompanionWorkFolderService.CleanupDebug(work, Path.Combine(root, "backups"));
            Equal(1, result.DeletedFiles);
            foreach (var name in new[] { "global.json", "savedgame.json", "personal-notes.txt" })
                Equal("user content", File.ReadAllText(Path.Combine(work, name)));
            using var backup = ZipFile.OpenRead(result.BackupPath!);
            using var reader = new StreamReader(backup.GetEntry("table_R&E_debug_sample.json")!.Open());
            Equal("debug snapshot", reader.ReadToEnd());
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "rae-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void GitHubUpdaterSelectsMatchingNewerExecutable()
    {
        using var service = new GitHubReleaseUpdateService(new StubHttpMessageHandler(request =>
        {
            Equal("api.github.com", request.RequestUri!.Host);
            Equal("/repos/KeKKERUUU/RandomEverywhereCompanion/releases/latest", request.RequestUri.AbsolutePath);
            return JsonResponse(CreateReleaseJson("v1.2.0", "RandomAndEverywhereCompanion.exe", 42, new string('A', 64)));
        }));

        var update = service.CheckForUpdateAsync(
                new Version(1, 0, 0),
                "RandomAndEverywhereCompanion.exe",
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        True(update is not null, "A newer matching asset should be selected.");
        Equal(new Version(1, 2, 0), update!.Version);
        Equal("RandomAndEverywhereCompanion.exe", update.AssetName);
    }

    private static void GitHubUpdaterIgnoresInstalledRelease()
    {
        using var service = new GitHubReleaseUpdateService(new StubHttpMessageHandler(_ =>
            JsonResponse(CreateReleaseJson("v1.0.0", "RandomAndEverywhereInstaller.exe", 42, new string('B', 64)))));

        var update = service.CheckForUpdateAsync(
                new Version(1, 0, 0),
                "RandomAndEverywhereInstaller.exe",
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        True(update is null, "The installed release must not be downloaded again.");
    }

    private static void GitHubUpdaterVerifiesDownloadedDigest()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var payload = Encoding.UTF8.GetBytes("verified executable fixture");
            var hash = Convert.ToHexString(SHA256.HashData(payload));
            using var service = new GitHubReleaseUpdateService(new StubHttpMessageHandler(request =>
            {
                if (request.RequestUri!.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(CreateReleaseJson("v1.1.0", "RandomAndEverywhereCompanion.exe", payload.Length, hash));
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(payload)
                };
            }));

            var update = service.CheckForUpdateAsync(new Version(1, 0, 0), "RandomAndEverywhereCompanion.exe", CancellationToken.None)
                .GetAwaiter().GetResult()!;
            var path = service.DownloadAsync(update, root, null, CancellationToken.None)
                .GetAwaiter().GetResult();

            True(File.Exists(path), "The verified executable should be committed to disk.");
            True(File.ReadAllBytes(path).SequenceEqual(payload), "The downloaded executable bytes must be preserved.");
            False(File.Exists(path + ".partial"), "The partial file must be removed after verification.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateReleaseJson(string tag, string assetName, long size, string hash) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            prerelease = false,
            assets = new[]
            {
                new
                {
                    name = assetName,
                    size,
                    browser_download_url = $"https://github.com/KeKKERUUU/RandomEverywhereCompanion/releases/download/{tag}/{assetName}",
                    digest = $"sha256:{hash}"
                }
            }
        });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static void CreateZip(string path, IReadOnlyDictionary<string, string> entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var pair in entries)
        {
            var entry = archive.CreateEntry(pair.Key, CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(pair.Value);
        }
    }

    private static string CreateAndrewJson(string missionVersion, string devVersion, string devType) =>
        $$"""
        {
          "macros": {
            "version check": [
              { "set": { "local": "MISSION_VERSION" }, "value": "{{missionVersion}}" },
              { "set": { "local": "DEV_VERSION" }, "value": "{{devVersion}}" },
              { "set": { "local": "DEV_TYPE" }, "value": "{{devType}}" },
              { "set": { "local": "MINIMUM_VERSION" }, "value": 479 }
            ]
          }
        }
        """;

    private static Dictionary<string, JsonDocument> LoadLocalizationDocuments()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Localization");
        var files = Directory.GetFiles(directory, "*.json");
        Equal(5, files.Length);
        return files.ToDictionary(
            path => Path.GetFileNameWithoutExtension(path),
            path => JsonDocument.Parse(File.ReadAllText(path)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadValues(JsonDocument document) =>
        document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void False(bool value, string message) => True(!value, message);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    [GeneratedRegex(@"\{\d+(?:[^}]*)?\}")]
    private static partial Regex PlaceholderRegex();

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
