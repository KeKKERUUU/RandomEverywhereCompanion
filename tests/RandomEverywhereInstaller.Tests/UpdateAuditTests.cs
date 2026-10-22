using System.Diagnostics;
using System.Net;
using System.Text;
using RandomEverywhereInstaller.Infrastructure.Updates;
using RandomEverywhereInstaller.Core.Updates;

namespace RandomEverywhereInstaller.Tests;

internal static partial class Program
{
    private static void UpdateCacheCannotFollowJunctions()
    {
        var root = CreateTemporaryDirectory();
        var cache = Path.Combine(root, "cache");
        var outside = Path.Combine(root, "outside");
        var link = Path.Combine(cache, "linked");
        var rootLink = Path.Combine(root, "root-link");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "RandomAndEverywhereCompanion.exe");
        File.WriteAllText(sentinel, "unrelated user file");
        try
        {
            CreateTestJunction(link, outside);
            CreateTestJunction(rootLink, outside);
            UpdateCacheCleanup.Clean(cache, "RandomAndEverywhereCompanion.exe");
            Equal("unrelated user file", File.ReadAllText(sentinel));
            UpdateCacheCleanup.Clean(rootLink, "RandomAndEverywhereCompanion.exe");
            Equal("unrelated user file", File.ReadAllText(sentinel));
            var nested = Directory.CreateDirectory(Path.Combine(outside, "nested")).FullName;
            var nestedSentinel = Path.Combine(nested, "RandomAndEverywhereCompanion.exe");
            File.WriteAllText(nestedSentinel, "linked ancestor");
            UpdateCacheCleanup.Clean(Path.Combine(rootLink, "nested"), "RandomAndEverywhereCompanion.exe");
            Equal("linked ancestor", File.ReadAllText(nestedSentinel));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(rootLink)) Directory.Delete(rootLink);
            Directory.Delete(root, true);
        }
    }

    private static void CreateTestJunction(string path, string target)
    {
        // Both paths are generated under the test's own unique temporary root.
        var start = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{path}\" \"{target}\"",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        True(process.ExitCode == 0, $"Junction fixture failed: {stdout} {stderr}");
    }

    private static void UpdateCachePreservesUnknownAndActiveFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var version = Directory.CreateDirectory(Path.Combine(root, "1.2.0")).FullName;
            var asset = Path.Combine(version, "RandomAndEverywhereCompanion.exe");
            File.WriteAllText(asset, "old helper");
            File.WriteAllText(asset + ".partial", "partial");
            File.WriteAllText(Path.Combine(root, "update-error.log"), "diagnostic");
            File.WriteAllText(Path.Combine(version, "personal.txt"), "preserve");
            using (var active = new FileStream(asset, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                UpdateCacheCleanup.Clean(root, "RandomAndEverywhereCompanion.exe");
                True(File.Exists(asset), "An active helper must remain intact.");
            }
            UpdateCacheCleanup.Clean(root, "RandomAndEverywhereCompanion.exe");
            False(File.Exists(asset), "An unlocked obsolete helper should be removed.");
            False(File.Exists(asset + ".partial"), "Partial downloads should be removed.");
            Equal("diagnostic", File.ReadAllText(Path.Combine(root, "update-error.log")));
            Equal("preserve", File.ReadAllText(Path.Combine(version, "personal.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void GitHubUpdaterRejectsWrongDigest()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var payload = Encoding.UTF8.GetBytes("corrupted download");
            var destination = Path.Combine(root, "RandomAndEverywhereCompanion.exe");
            File.WriteAllText(destination, "previous verified executable");
            using var service = new GitHubReleaseUpdateService(new StubHttpMessageHandler(request =>
                new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(payload) }));
            var update = new ApplicationUpdateInfo(new Version(1, 2, 0), "v1.2.0", Path.GetFileName(destination),
                new Uri("https://github.com/KeKKERUUU/RandomEverywhereCompanion/releases/download/v1.2.0/RandomAndEverywhereCompanion.exe"),
                payload.Length, new string('0', 64));
            try
            {
                service.DownloadAsync(update, root, null, CancellationToken.None).GetAwaiter().GetResult();
                throw new InvalidOperationException("An incorrect digest was accepted.");
            }
            catch (ApplicationUpdateException exception) { Equal("UPDATE-HASH", exception.Code); }
            Equal("previous verified executable", File.ReadAllText(destination));
            False(File.Exists(destination + ".partial"), "Rejected bytes must not remain staged.");
        }
        finally { Directory.Delete(root, true); }
    }
}
