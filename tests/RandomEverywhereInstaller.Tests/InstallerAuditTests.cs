using System.Globalization;
using System.Text.Json;
using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Core.Models;
using RandomEverywhereInstaller.Infrastructure.Installer;

namespace RandomEverywhereInstaller.Tests;

internal static partial class Program
{
    // Registered by the main test harness.
    internal static int RunInstallerAuditTests()
    {
        var before = Failures.Count;
        RunInstallerResilienceTests();
        Run("Audit: final manifest size is independently correct and stable", AuditManifestSize);
        foreach (var workflow in new[] { "maintenance", "update", "install" })
        {
            Run($"Audit: {workflow} restores metadata byte for byte", () => AuditMetadataRollback(workflow));
        }
        foreach (var fullInstall in new[] { false, true })
        foreach (var replacement in new[] { "older", "invalid", "same-version" })
        {
            Run($"Audit: full={fullInstall} rejects changed {replacement} input", () => AuditChangedInput(fullInstall, replacement));
        }
        Run("Audit: changed archived cumulative input is rejected", AuditChangedArchive);
        Run("Audit: unchanged cumulative bytes install exactly", AuditUnchangedInput);
        Run("Audit: native index cancellation completes before rollback", () => AuditCancellationAsync().GetAwaiter().GetResult());
        foreach (var failure in Failures.Skip(before)) Console.WriteLine(failure);
        return Failures.Count - before;
    }

    private static void AuditManifestSize()
    {
        using var fixture = new AuditFixture();
        InstallerEngine.GenerateLayoutFile(fixture.Layout);
        var first = File.ReadAllBytes(fixture.Manifest);
        using (var manifest = JsonDocument.Parse(first))
        {
            var declared = long.Parse(manifest.RootElement.GetProperty("total_package_size").GetString()!, CultureInfo.InvariantCulture);
            // Independent oracle: this fixture has no exclusions or temporary files.
            var actual = Directory.EnumerateFiles(fixture.Package, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            Equal(actual, declared);
        }
        InstallerEngine.GenerateLayoutFile(fixture.Layout);
        True(first.SequenceEqual(File.ReadAllBytes(fixture.Manifest)), "Repeated generation must be byte-stable.");
    }

    private static void AuditMetadataRollback(string workflow)
    {
        using var fixture = new AuditFixture();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Target)!, "invalid.json"), "{invalid");
        var snapshot = fixture.Snapshot();
        var inspection = fixture.Engine.InspectAndrewUpdate(fixture.Source);
        try
        {
            if (workflow == "maintenance")
                fixture.Engine.RefreshMissionFilesAsync(fixture.Community, AuditProgress, CancellationToken.None).GetAwaiter().GetResult();
            else
                fixture.Install(inspection, workflow == "install");
            throw new InvalidOperationException("Invalid mission JSON must fail the operation.");
        }
        catch (InstallerException error) when (error.Code == "INDEX-JSON") { }
        fixture.AssertSnapshot(snapshot);
        AssertSessionsCleaned(fixture.Root);
    }

    private static void AuditChangedInput(bool fullInstall, string replacement)
    {
        using var fixture = new AuditFixture();
        var inspection = fixture.Engine.InspectAndrewUpdate(fixture.Source);
        var snapshot = fixture.Snapshot();
        File.WriteAllText(fixture.Source, replacement switch
        {
            "older" => CreateAndrewJson("1", "1", "test"),
            "invalid" => "not-json",
            _ => CreateAndrewJson("1", "3", "test") + "\n "
        });
        var rejected = false;
        try { fixture.Install(inspection, fullInstall); }
        catch (InstallerException error) when (error.Code == "ANDREW-002") { rejected = true; }
        True(rejected, "Changed source bytes must be rejected against the approved inspection.");
        fixture.AssertSnapshot(snapshot);
        False(File.Exists(fixture.IndexRan), "Invalid cumulative bytes must be rejected before index execution.");
    }

    private static void AuditChangedArchive()
    {
        using var fixture = new AuditFixture();
        var archive = Path.Combine(fixture.Root, "cumulative.zip");
        CreateZip(archive, new Dictionary<string, string> { ["everywhere_all.json"] = File.ReadAllText(fixture.Source) });
        var inspection = fixture.Engine.InspectAndrewUpdate(archive);
        var snapshot = fixture.Snapshot();
        File.Delete(archive);
        CreateZip(archive, new Dictionary<string, string> { ["everywhere_all.json"] = CreateAndrewJson("1", "1", "test") });
        var rejected = false;
        try { fixture.Install(inspection, false); }
        catch (InstallerException error) when (error.Code == "ANDREW-002") { rejected = true; }
        True(rejected, "Archived cumulative bytes must also match the inspection.");
        fixture.AssertSnapshot(snapshot);
    }

    private static void AuditUnchangedInput()
    {
        foreach (var fullInstall in new[] { false, true })
        {
            using var fixture = new AuditFixture();
            var expected = File.ReadAllBytes(fixture.Source);
            var inspection = fixture.Engine.InspectAndrewUpdate(fixture.Source);
            fixture.Install(inspection, fullInstall);
            True(expected.SequenceEqual(File.ReadAllBytes(fixture.Target)), "Install must preserve the approved bytes exactly.");
            using var index = JsonDocument.Parse(File.ReadAllBytes(fixture.Index));
            var encoded = index.RootElement.GetProperty("hpgmission").GetProperty("RANDOM AND EVERYWHERE").GetProperty("*")[0].GetString()!;
            using var decoded = JsonDocument.Parse(Convert.FromBase64String(encoded));
            using var original = JsonDocument.Parse(expected);
            True(JsonElement.DeepEquals(original.RootElement, decoded.RootElement), "Final index must contain the newly installed mission, not its previous version.");
            False(File.Exists(fixture.IndexRan), "Installation must not execute the legacy index script.");
        }
    }

    private static async Task AuditCancellationAsync()
    {
        foreach (var workflow in new[] { "maintenance", "update", "install" })
            await AuditCancellationWorkflowAsync(workflow);
    }

    private static async Task AuditCancellationWorkflowAsync(string workflow)
    {
        using var fixture = new AuditFixture();
        var snapshot = fixture.Snapshot();
        using var cancellation = new CancellationTokenSource();
        var reachedIndex = false;
        var progress = new SynchronousInstallerProgress(p =>
        {
            if (p.Phase == "MissionIndex" && p.OverallPercent == 96)
            {
                reachedIndex = true;
                cancellation.Cancel();
            }
        });
        var inspection = fixture.Engine.InspectAndrewUpdate(fixture.Source);
        var cancelled = false;
        try
        {
            if (workflow == "maintenance")
                await fixture.Engine.RefreshMissionFilesAsync(fixture.Community, progress, cancellation.Token);
            else if (workflow == "update")
                await fixture.Engine.InstallAndrewUpdateAsync(new AndrewUpdateRequest(fixture.Community, inspection), progress, cancellation.Token);
            else
                await fixture.Engine.InstallAsync(new InstallationRequest(SimulatorType.Msfs2024, fixture.Community, [], CumulativeUpdate: inspection), progress, cancellation.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        True(reachedIndex && cancelled, "Native index cancellation must propagate after work actually starts.");
        fixture.AssertSnapshot(snapshot);
        await Task.Yield();
        fixture.AssertSnapshot(snapshot);
        False(File.Exists(fixture.IndexRan), "The legacy script must never execute.");
        False(Directory.EnumerateFiles(fixture.Community, ".mission-index-*.tmp", SearchOption.AllDirectories).Any(), "Cancellation left a partial index.");
        AssertSessionsCleaned(fixture.Root);
    }

    private static readonly IProgress<InstallerProgress> AuditProgress = new SynchronousInstallerProgress(_ => { });

    private sealed class AuditFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ia", Guid.NewGuid().ToString("N")[..8]);
        public string Community => Path.Combine(Root, "Community");
        public string Package => Path.Combine(Community, "hpg-airbus-h145-z-andrew-mission-pack");
        public string Target => Path.Combine(Package, "hpgmission", "RANDOM AND EVERYWHERE", "everywhere_all.json");
        public string Manifest => Path.Combine(Package, "manifest.json");
        public string Layout => Path.Combine(Package, "layout.json");
        public string Source => Path.Combine(Root, "incoming.json");
        public string Index => Path.Combine(Community, "hpg-airbus-h145", "html_ui", "HPGH145-System", "GeneratedMissionIndex.json");
        public string IndexRan => Path.Combine(Root, "index-ran.txt");
        public InstallerEngine Engine { get; } = new(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Audit tests forbid HTTP.")));

        public AuditFixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Index)!);
            File.WriteAllText(Target, CreateAndrewJson("1", "2", "test"));
            File.WriteAllText(Source, CreateAndrewJson("1", "3", "test"));
            File.WriteAllText(Manifest, "{\"total_package_size\":\"0\",\"title\":\"Random and Everywhere\"}\r\n");
            File.WriteAllText(Layout, "{\"content\":[]}\r\n");
            File.WriteAllText(Index, "{\"original\":true}\r\n");
            WriteScript($"@echo ran>\"{IndexRan}\"\r\n@echo {{\"updated\":true}}>\"{Index}\"\r\n@exit /b 0\r\n");
        }

        public void WriteScript(string text)
        {
            var tools = Directory.CreateDirectory(Path.Combine(Community, "hpg-airbus-h145", "Tools")).FullName;
            File.WriteAllText(Path.Combine(tools, "Update Mission Index.cmd"), text);
        }

        public Dictionary<string, byte[]> Snapshot() => Directory.EnumerateFiles(Package, "*", SearchOption.AllDirectories)
            .Append(Index).ToDictionary(path => path, File.ReadAllBytes);

        public void AssertSnapshot(Dictionary<string, byte[]> snapshot)
        {
            Equal(snapshot.Count, Snapshot().Count);
            foreach (var (path, bytes) in snapshot)
                True(File.Exists(path) && bytes.SequenceEqual(File.ReadAllBytes(path)), "Rollback changed " + Path.GetRelativePath(Root, path));
        }

        public void Install(AndrewUpdateInspection inspection, bool fullInstall)
        {
            if (fullInstall)
                Engine.InstallAsync(new InstallationRequest(SimulatorType.Msfs2024, Community, [], CumulativeUpdate: inspection), AuditProgress, CancellationToken.None).GetAwaiter().GetResult();
            else
                Engine.InstallAndrewUpdateAsync(new AndrewUpdateRequest(Community, inspection), AuditProgress, CancellationToken.None).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Engine.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
