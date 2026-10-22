using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Core.Models;
using RandomEverywhereInstaller.Infrastructure.Installer;

namespace RandomEverywhereInstaller.Tests;

internal static partial class Program
{
    private static void RunInstallerResilienceTests()
    {
        Run("Audit: Community validation preserves protected roots on the current platform", CommunityValidationHonorsPlatform);
        foreach (var version in new[] { 491, 500 })
            Run($"Audit: H145 build {version} remains committed if completion reporting fails", () => H145CompletionFailureKeepsCommittedFiles(version));
        Run("Audit: failed diagnostic writes cannot prevent cancellation rollback", H145LogFailureDoesNotPreventRollback);
        Run("Audit: archives reject Windows-equivalent path aliases before extraction", ArchiveRejectsWindowsAliases);
        Run("Audit: archive duplicate checks use normalized path separators", ArchiveRejectsCanonicalPathCollisions);
        foreach (var fullInstall in new[] { false, true })
            Run($"Audit: full={fullInstall} returns committed result despite completion failure", () => InstallationCompletionFailureKeepsCommittedFiles(fullInstall));
    }

    private static void CommunityValidationHonorsPlatform()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var engine = new InstallerEngine(new StubHttpMessageHandler(_ => throw new InvalidOperationException("No network allowed.")));
            False(engine.DetectH145(root).Installed, "An empty ordinary directory must remain a valid read-only Community candidate.");
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var protectedPaths = new List<string> { Path.GetPathRoot(root)! };
            if (windows.Length > 0)
            {
                protectedPaths.Add(windows);
                var system = Path.Combine(windows, "System32");
                if (Directory.Exists(system)) protectedPaths.Add(system);
            }
            foreach (var path in protectedPaths)
            {
                var rejected = false;
                try { engine.DetectH145(path); }
                catch (InstallerException error) when (error.Code is "PATH-002" or "PATH-003") { rejected = true; }
                True(rejected, "Protected path was accepted: " + path);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void H145CompletionFailureKeepsCommittedFiles(int version)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = MakeH145Fixture(root, version, true);
            using var engine = new InstallerEngine(new StubHttpMessageHandler(request => H145ArchiveResponse(
                request.RequestUri!.Query.Contains("1HZmW1", StringComparison.Ordinal) ? "hpg-airbus-h145-ap" : "hpg-airbus-h145")));
            var completed = false;
            var progress = new SynchronousInstallerProgress(value =>
            {
                if (value.Phase != "Completed") return;
                completed = true;
                throw new InvalidOperationException("Synthetic completion notification failure.");
            });
            var result = engine.UpdateH145Async(community, progress, CancellationToken.None).GetAwaiter().GetResult();
            True(completed, "The fixture must actually exercise the final notification.");
            Equal(500, result.Version);
            foreach (var folder in new[] { "hpg-airbus-h145", "hpg-airbus-h145-ap" })
            {
                True(InstallerEngine.TryValidateGeneratedLayout(Path.Combine(community, folder, "layout.json"), out var reason), reason);
                Equal("user data", File.ReadAllText(Path.Combine(community, folder, "user-file.txt")));
                if (version < 500) True(File.Exists(Path.Combine(community, folder, "patch-file.txt")), "Committed patch was removed.");
            }
            True(File.ReadAllText(Path.Combine(engine.LogDirectory, "installer.log")).Contains("POST-COMMIT-WARNING", StringComparison.Ordinal),
                "Notification failure must be diagnosed without reporting a failed installation.");
            AssertNoH145Session(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void H145LogFailureDoesNotPreventRollback()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = MakeH145Fixture(root, 500, true);
            var files = Directory.EnumerateFiles(community, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            using var cancellation = new CancellationTokenSource();
            using var engine = new InstallerEngine(new StubHttpMessageHandler(_ => throw new InvalidOperationException("No network allowed.")));
            var log = Path.Combine(engine.LogDirectory, "installer.log");
            var progress = new SynchronousInstallerProgress(value =>
            {
                if (value.Phase != "Layouts" || value.Current != 1) return;
                // A directory where the log file was makes subsequent appends fail even with elevated permissions.
                File.Delete(log);
                Directory.CreateDirectory(log);
                cancellation.Cancel();
            });
            var cancelled = false;
            try { engine.UpdateH145Async(community, progress, cancellation.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            True(cancelled, "A diagnostic failure must not replace the original cancellation.");
            Equal(files.Count, Directory.EnumerateFiles(community, "*", SearchOption.AllDirectories).Count());
            foreach (var (path, bytes) in files)
                True(bytes.SequenceEqual(File.ReadAllBytes(path)), "Diagnostic failure prevented rollback of " + path);
            AssertNoH145Session(root);
            Directory.Delete(log);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ArchiveRejectsWindowsAliases()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            foreach (var relative in new[] { "file.txt.", "file.txt ", "folder./file.txt", "folder /file.txt", "bad?.txt", "bad|name.txt", "bad\u0001name.txt" })
            {
                var archive = Path.Combine(root, Guid.NewGuid() + ".zip");
                CreateZip(archive, new Dictionary<string, string>
                {
                    ["test-package/manifest.json"] = "{}", ["test-package/layout.json"] = "{}",
                    ["test-package/file.txt"] = "ordinary", ["test-package/" + relative] = "alias"
                });
                var rejected = false;
                try { ArchivePackageInspector.Inspect(archive); }
                catch (InstallerException error) when (error.Code == "ARCHIVE-005") { rejected = true; }
                True(rejected, "Unsafe Windows archive segment was accepted: " + relative);
            }
            var safe = Path.Combine(root, "safe.zip");
            CreateZip(safe, new Dictionary<string, string>
            {
                ["test-package/manifest.json"] = "{}", ["test-package/layout.json"] = "{}",
                ["test-package/normal folder/voice.v2.txt"] = "safe"
            });
            Equal("test-package", ArchivePackageInspector.Inspect(safe).Single().FolderName);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ArchiveRejectsCanonicalPathCollisions()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var archive = Path.Combine(root, "duplicate.zip");
            CreateZip(archive, new Dictionary<string, string>
            {
                ["test-package/manifest.json"] = "{}", ["test-package/layout.json"] = "{}",
                ["test-package/data/file.txt"] = "original", ["test-package/data//file.txt"] = "alias"
            });
            var rejected = false;
            try { ArchivePackageInspector.Inspect(archive); }
            catch (InstallerException error) when (error.Code == "ARCHIVE-013") { rejected = true; }
            True(rejected, "Redundant separators must not bypass duplicate destination detection.");

            var safe = Path.Combine(root, "single.zip");
            CreateZip(safe, new Dictionary<string, string>
            {
                ["test-package/manifest.json"] = "{}", ["test-package/layout.json"] = "{}",
                ["test-package/data//file.txt"] = "safe"
            });
            Equal("test-package", ArchivePackageInspector.Inspect(safe).Single().FolderName);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void InstallationCompletionFailureKeepsCommittedFiles(bool fullInstall)
    {
        using var fixture = new AuditFixture();
        var inspection = fixture.Engine.InspectAndrewUpdate(fixture.Source);
        var expected = File.ReadAllBytes(fixture.Source);
        var completed = false;
        var progress = new SynchronousInstallerProgress(value =>
        {
            if (value.Phase != "Completed") return;
            completed = true;
            throw new InvalidOperationException("Synthetic completion notification failure.");
        });
        IReadOnlyList<string> warnings;
        if (fullInstall)
        {
            var result = fixture.Engine.InstallAsync(new InstallationRequest(SimulatorType.Msfs2024, fixture.Community, [], CumulativeUpdate: inspection),
                progress, CancellationToken.None).GetAwaiter().GetResult();
            True(result.Success && !result.RolledBack, "Committed installation must return success.");
            warnings = result.Warnings;
        }
        else
        {
            var result = fixture.Engine.InstallAndrewUpdateAsync(new AndrewUpdateRequest(fixture.Community, inspection), progress, CancellationToken.None).GetAwaiter().GetResult();
            True(result.Success && !result.RolledBack, "Committed update must return success.");
            warnings = result.Warnings;
        }
        True(completed, "The fixture must actually exercise the final notification.");
        True(warnings.Any(value => value.Contains("completion", StringComparison.OrdinalIgnoreCase)), "The committed result must retain the reporting warning.");
        True(expected.SequenceEqual(File.ReadAllBytes(fixture.Target)), "Committed mission bytes changed.");
        True(InstallerEngine.TryValidateGeneratedLayout(fixture.Layout, out var reason), reason);
        using var index = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(fixture.Index));
        var encoded = index.RootElement.GetProperty("hpgmission").GetProperty("RANDOM AND EVERYWHERE").GetProperty("*")[0].GetString()!;
        using var decoded = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(encoded));
        using var source = System.Text.Json.JsonDocument.Parse(expected);
        True(System.Text.Json.JsonElement.DeepEquals(source.RootElement, decoded.RootElement), "Completion failure restored the old mission index.");
        False(File.Exists(fixture.IndexRan), "Completion handling must not execute a legacy index script.");
        AssertSessionsCleaned(fixture.Root);
    }
}
