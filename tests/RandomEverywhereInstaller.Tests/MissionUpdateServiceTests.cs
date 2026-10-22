using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Core.Updates;
using RandomEverywhereInstaller.Infrastructure.Updates;

namespace RandomEverywhereInstaller.Tests;

internal static partial class Program
{
    private const string MissionAssetUrl = "https://github.com/davierosoft/HEMS-random-everywhere-mission/releases/download/v0.997-169.45/everywhere_all.json";
    private static byte[] PublishedMission(string id = "HEMSRandE", string revision = "169.45") => Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { id, title = "HEMS RANDOM AND EVERYWHERE MISSIONS 0.997 " + revision, padding = new string(' ', 5000) }));
    private static MissionUpdateInfo MissionInfo(byte[] bytes) => new(new("0.997", "169.45", null), "v0.997-169.45", "Release notes",
        new Uri(MissionAssetUrl), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "GitHub release");
    private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static void MissionUpdateMetadata()
    {
        var bytes = PublishedMission();
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "v0.997-169.45", body = "## Improvements\nDetails", draft = false,
            assets = new[] { new { name = "everywhere_all.json", size = bytes.Length, browser_download_url = MissionAssetUrl, digest = "sha256:" + MissionInfo(bytes).Sha256 } } });
        var calls = 0;
        using var service = new MissionUpdateService(new StubHttpMessageHandler(request =>
        {
            Equal(MissionUpdateService.Api, request.RequestUri!.AbsoluteUri); calls++;
            return BytesResponse(metadata);
        }));
        var update = service.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal("0.997 169.45", update.Version.Display);
        Equal("## Improvements\nDetails", update.Notes);
        Equal(1, calls);
        Equal(bytes.Length, (int)update.Size);
    }

    private static void MissionDownloadValidation()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var valid = PublishedMission();
            foreach (var (payload, update, accepted) in new[]
            {
                (valid, MissionInfo(valid), true),
                (valid, MissionInfo(valid) with { Sha256 = new string('0', 64) }, false),
                (PublishedMission("OTHER"), MissionInfo(PublishedMission("OTHER")), false),
                (PublishedMission(revision: "169.9"), MissionInfo(PublishedMission(revision: "169.9")), false),
                (Encoding.UTF8.GetBytes("{broken json"), MissionInfo(Encoding.UTF8.GetBytes("{broken json")), false),
                (valid, MissionInfo(valid) with { Size = valid.Length + 1 }, false)
            })
            {
                var path = Path.Combine(directory, Guid.NewGuid() + ".json");
                using var service = new MissionUpdateService(new StubHttpMessageHandler(_ => BytesResponse(payload)));
                var succeeded = false;
                try { service.DownloadAsync(update, path, null, CancellationToken.None).GetAwaiter().GetResult(); succeeded = true; }
                catch (Exception e) when (e is ApplicationUpdateException or JsonException) { }
                Equal(accepted, succeeded);
                Equal(accepted, File.Exists(path));
                if (accepted) True(File.ReadAllBytes(path).SequenceEqual(valid), "Verified mission bytes changed.");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void MissionRedirectGuard()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var calls = 0;
            using var service = new MissionUpdateService(new StubHttpMessageHandler(_ =>
            {
                calls++;
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://untrusted.example/mission.json"); return response;
            }));
            try { service.DownloadAsync(MissionInfo(PublishedMission()), Path.Combine(directory, "mission.json"), null, CancellationToken.None).GetAwaiter().GetResult(); throw new InvalidOperationException("Untrusted redirect accepted."); }
            catch (ApplicationUpdateException) { }
            Equal(1, calls);
            Equal(0, Directory.GetFiles(directory).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void MissionFallback()
    {
        var bytes = PublishedMission();
        var calls = 0;
        using var service = new MissionUpdateService(new StubHttpMessageHandler(request =>
        {
            calls++;
            if (request.RequestUri!.AbsoluteUri == MissionUpdateService.Api) return new(HttpStatusCode.ServiceUnavailable);
            Equal(MissionUpdateService.DriveFallback, request.RequestUri.AbsoluteUri);
            Equal("bytes=0-4095", request.Headers.Range!.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[..4096]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 4095, bytes.Length);
            return response;
        }));
        var result = service.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal(2, calls);
        Equal("0.997 169.45", result.Version.Display);
        True(result.Sha256 is null, "Fallback must not invent a publisher digest.");
        Equal(bytes.Length, (int)result.Size);
    }

    private static void MissionDownloadOwnership()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "existing.json");
            File.WriteAllText(path, "user data");
            var bytes = PublishedMission();
            using var service = new MissionUpdateService(new StubHttpMessageHandler(_ => BytesResponse(bytes)));
            try { service.DownloadAsync(MissionInfo(bytes), path, null, CancellationToken.None).GetAwaiter().GetResult(); throw new InvalidOperationException("Existing file replaced."); }
            catch (IOException) { }
            Equal("user data", File.ReadAllText(path));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var cancelledPath = Path.Combine(directory, "cancelled.json");
            try { service.DownloadAsync(MissionInfo(bytes), cancelledPath, null, cancellation.Token).GetAwaiter().GetResult(); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            False(File.Exists(cancelledPath), "Cancelled download left a file.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void MissionCacheOwnership()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
            var original = Path.Combine(community, "untouched.txt");
            File.WriteAllText(original, "original");
            var cache = MissionDownloadCache.Create(community, 100);
            False(cache.FilePath.StartsWith(community + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Cache was created inside Community.");
            var unexpected = Path.Combine(Path.GetDirectoryName(cache.FilePath)!, "preserve.txt");
            File.WriteAllText(unexpected, "not owned");
            File.WriteAllText(cache.FilePath, "downloaded fixture");
            cache.Dispose();
            False(File.Exists(cache.FilePath), "Owned download was not cleaned up.");
            Equal("not owned", File.ReadAllText(unexpected));
            Equal("original", File.ReadAllText(original));
            Equal(1, Directory.GetFileSystemEntries(community).Length);
        }
        finally { Directory.Delete(root, true); }
    }
}
