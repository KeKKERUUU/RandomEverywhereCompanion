using System.IO.Compression;
using System.Net;
using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Infrastructure.Installer;

namespace RandomEverywhereInstaller.Tests;

internal static partial class Program
{
    private static void H145CurrentBuildOnlyRefreshesScopedLayouts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = MakeH145Fixture(root, 500, true);
            var untouched = File.ReadAllText(Path.Combine(community, "unrelated", "layout.json"));
            using var engine = new InstallerEngine(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Network must not be used for build 500.")));
            Equal(0, InstallerEngine.GetH145UpdateSourceUrls(engine.DetectH145(community)).Count);
            var result = engine.UpdateH145Async(community, new Progress<InstallerProgress>(), CancellationToken.None).GetAwaiter().GetResult();
            True(result.IsVersion500OrLater, "Current aircraft must remain current.");
            foreach (var folder in new[] { "hpg-airbus-h145", "hpg-airbus-h145-ap" })
                True(InstallerEngine.TryValidateGeneratedLayout(Path.Combine(community, folder, "layout.json"), out var reason), reason);
            Equal(untouched, File.ReadAllText(Path.Combine(community, "unrelated", "layout.json")));
            AssertNoH145Session(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void H145PatchRespectsActionPackPresence()
    {
        foreach (var actionPack in new[] { false, true })
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var community = MakeH145Fixture(root, 491, actionPack);
                var requested = new List<string>();
                using var engine = new InstallerEngine(new StubHttpMessageHandler(request =>
                {
                    var uri = request.RequestUri!;
                    requested.Add(uri.AbsoluteUri);
                    var isAction = uri.Query.Contains("1HZmW1", StringComparison.Ordinal);
                    return H145ArchiveResponse(isAction ? "hpg-airbus-h145-ap" : "hpg-airbus-h145");
                }));
                Equal(actionPack ? 2 : 1, InstallerEngine.GetH145UpdateSourceUrls(engine.DetectH145(community)).Count);
                var result = engine.UpdateH145Async(community, new Progress<InstallerProgress>(), CancellationToken.None).GetAwaiter().GetResult();
                Equal(500, result.Version);
                Equal(actionPack ? 2 : 1, requested.Count);
                Equal(actionPack, Directory.Exists(Path.Combine(community, "hpg-airbus-h145-ap")));
                foreach (var folder in actionPack ? new[] { "hpg-airbus-h145", "hpg-airbus-h145-ap" } : new[] { "hpg-airbus-h145" })
                {
                    True(InstallerEngine.TryValidateGeneratedLayout(Path.Combine(community, folder, "layout.json"), out var reason), reason);
                    var layout = File.ReadAllText(Path.Combine(community, folder, "layout.json"));
                    True(layout.Contains("patch-file.txt", StringComparison.Ordinal), "Layout must include files from the applied patch.");
                    Equal("user data", File.ReadAllText(Path.Combine(community, folder, "user-file.txt")));
                }
                Equal("untouched", File.ReadAllText(Path.Combine(community, "unrelated", "layout.json")));
                AssertNoH145Session(root);
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static void H145InvalidPatchRollsBack()
    {
        foreach (var originalLayout in new[] { false, true })
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var community = MakeH145Fixture(root, 491, false);
                var package = Path.Combine(community, "hpg-airbus-h145");
                if (!originalLayout) File.Delete(Path.Combine(package, "layout.json"));
                var manifest = File.ReadAllText(Path.Combine(package, "manifest.json"));
                using var engine = new InstallerEngine(new StubHttpMessageHandler(_ => H145ArchiveResponse("hpg-airbus-h145", includeMarker: false)));
                var failed = false;
                try { engine.UpdateH145Async(community, new Progress<InstallerProgress>(), CancellationToken.None).GetAwaiter().GetResult(); }
                catch (InstallerException error) when (error.Code == "H145-009") { failed = true; }
                True(failed, "An archive without build 500 must be rolled back.");
                Equal(491, engine.DetectH145(community).Version);
                Equal(manifest, File.ReadAllText(Path.Combine(package, "manifest.json")));
                Equal(originalLayout, File.Exists(Path.Combine(package, "layout.json")));
                if (originalLayout) Equal("old layout", File.ReadAllText(Path.Combine(package, "layout.json")));
                False(File.Exists(Path.Combine(package, "patch-file.txt")), "New files must be removed on rollback.");
                Equal("user data", File.ReadAllText(Path.Combine(package, "user-file.txt")));
                AssertNoH145Session(root);
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static void H145RejectsUnexpectedRootsAndRedirects()
    {
        foreach (var redirect in new[] { false, true })
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var community = MakeH145Fixture(root, 491, false);
                var calls = 0;
                using var engine = new InstallerEngine(new StubHttpMessageHandler(_ =>
                {
                    calls++;
                    if (!redirect) return H145ArchiveResponse("unrelated");
                    var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                    response.Headers.Location = new Uri("https://unapproved.invalid/patch.zip");
                    return response;
                }));
                var blocked = false;
                try { engine.UpdateH145Async(community, new Progress<InstallerProgress>(), CancellationToken.None).GetAwaiter().GetResult(); }
                catch (InstallerException error) when (error.Code == (redirect ? "DL-003" : "H145-006")) { blocked = true; }
                True(blocked, "Unexpected content or host must be rejected.");
                Equal(1, calls);
                Equal("old layout", File.ReadAllText(Path.Combine(community, "hpg-airbus-h145", "layout.json")));
                Equal("untouched", File.ReadAllText(Path.Combine(community, "unrelated", "layout.json")));
                AssertNoH145Session(root);
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static void H145CancelledLayoutRefreshRestoresMetadata()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = MakeH145Fixture(root, 500, true);
            var package = Path.Combine(community, "hpg-airbus-h145");
            File.Delete(Path.Combine(package, "layout.json"));
            var manifest = File.ReadAllText(Path.Combine(package, "manifest.json"));
            using var cancel = new CancellationTokenSource();
            using var engine = new InstallerEngine(new StubHttpMessageHandler(_ => throw new InvalidOperationException("No network.")));
            // Cancel after the first completed layout, before processing the next package.
            var progress = new SynchronousInstallerProgress(p =>
            {
                if (File.Exists(Path.Combine(package, "layout.json"))) cancel.Cancel();
            });
            var cancelled = false;
            try { engine.UpdateH145Async(community, progress, cancel.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            True(cancelled, "Cancellation must reach the caller.");
            False(File.Exists(Path.Combine(package, "layout.json")), "A newly created layout must be removed on cancellation.");
            Equal(manifest, File.ReadAllText(Path.Combine(package, "manifest.json")));
            Equal("old layout", File.ReadAllText(Path.Combine(community, "hpg-airbus-h145-ap", "layout.json")));
            AssertNoH145Session(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string MakeH145Fixture(string root, int version, bool actionPack)
    {
        var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
        foreach (var folder in actionPack ? new[] { "hpg-airbus-h145", "hpg-airbus-h145-ap" } : new[] { "hpg-airbus-h145" })
        {
            var package = Directory.CreateDirectory(Path.Combine(community, folder)).FullName;
            File.WriteAllText(Path.Combine(package, "manifest.json"), "{\"total_package_size\":\"0\",\"package_version\":\"1.0.0\"}");
            File.WriteAllText(Path.Combine(package, "layout.json"), "old layout");
            File.WriteAllText(Path.Combine(package, "user-file.txt"), "user data");
        }
        File.WriteAllText(Path.Combine(community, "hpg-airbus-h145", "Version_" + version + ".txt"), "");
        Directory.CreateDirectory(Path.Combine(community, "unrelated"));
        File.WriteAllText(Path.Combine(community, "unrelated", "layout.json"), "untouched");
        return community;
    }

    private static HttpResponseMessage H145ArchiveResponse(string folder, bool includeMarker = true)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entries = new Dictionary<string, string> { ["manifest.json"] = "{}", ["layout.json"] = "{}", ["patch-file.txt"] = new('x', 2048) };
            if (includeMarker) entries["Version_500.txt"] = "";
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(folder + "/" + path, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(memory.ToArray()) };
    }

    private static void AssertNoH145Session(string root) =>
        False(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Any(path => Directory.GetParent(path)?.Name == "Sessions"), "Downloads, staging and rollback files must be cleaned.");

    private sealed class SynchronousInstallerProgress(Action<InstallerProgress> report) : IProgress<InstallerProgress>
    {
        public void Report(InstallerProgress value) => report(value);
    }
}
