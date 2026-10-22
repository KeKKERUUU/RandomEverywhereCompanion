using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Core.Updates;

namespace RandomEverywhereInstaller.Infrastructure.Updates;

/// <summary>Upstream R&E publication channel. Checks never write to Community or download a full mission.</summary>
public sealed class MissionUpdateService : IMissionUpdateService, IDisposable
{
    internal const string Repository = "davierosoft/HEMS-random-everywhere-mission";
    internal const string Api = "https://api.github.com/repos/" + Repository + "/releases/latest";
    internal const string DriveFallback = "https://drive.google.com/uc?export=download&id=12A_yYb0hu7z7LrtUO-Cms2Ft4ua-BSij";
    internal const long MaximumMissionBytes = 256L * 1024 * 1024;
    private readonly HttpClient _client;

    public MissionUpdateService() : this(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None
    }) { }

    internal MissionUpdateService(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(5) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("RandomEverywhereCompanion-MissionUpdates/1.0");
    }

    public async Task<MissionUpdateInfo> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await GetAsync(new Uri(Api), false, null, cancellationToken).ConfigureAwait(false);
            var bytes = await ReadLimitedAsync(response, 1024 * 1024, false, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) throw Invalid("A draft is not a published mission release.");
            var tag = Text(root, "tag_name");
            var version = MissionReleaseVersion.FromTag(tag) ?? MissionReleaseVersion.FromTag(Text(root, "name"))
                ?? throw Invalid("The release has no supported mission revision.");
            var assets = root.GetProperty("assets").EnumerateArray().Where(a => Text(a, "name").Equals("everywhere_all.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (assets.Length != 1) throw Invalid("The release must contain exactly one everywhere_all.json asset.");
            var asset = assets[0];
            var size = asset.GetProperty("size").GetInt64();
            ValidateSize(size);
            var url = new Uri(Text(asset, "browser_download_url"), UriKind.Absolute);
            if (!IsInitialAsset(url)) throw Invalid("The mission asset is not from the configured repository.");
            var digest = Text(asset, "digest");
            string? hash = null;
            if (digest.Length > 0)
            {
                if (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    throw Invalid("The asset digest is invalid.");
                hash = digest[7..];
            }
            return new(version, tag, Text(root, "body"), url, size, hash, "GitHub release");
        }
        catch (HttpRequestException)
        {
            // Match upstream's configured public fallback, never an arbitrary link from metadata.
            using var response = await GetAsync(new Uri(DriveFallback), true, 4096, cancellationToken).ConfigureAwait(false);
            var head = Encoding.UTF8.GetString(await ReadLimitedAsync(response, 4096, true, cancellationToken).ConfigureAwait(false));
            var id = HeaderField(head, "id");
            var title = HeaderField(head, "title");
            var version = MissionReleaseVersion.FromTitle(title);
            if (id != MissionReleaseVersion.MissionId || version is null) throw Invalid("The configured fallback did not return the R&E mission.");
            var size = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength ?? 0;
            ValidateSize(size);
            return new(version, title, string.Empty, new Uri(DriveFallback), size, null, "Configured Google Drive source");
        }
    }

    public async Task<string> DownloadAsync(MissionUpdateInfo update, string destinationFile,
        IProgress<ApplicationUpdateProgress>? progress, CancellationToken cancellationToken)
    {
        ValidateSize(update.Size);
        var drive = update.DownloadUri.AbsoluteUri == DriveFallback;
        if (!drive && !IsInitialAsset(update.DownloadUri)) throw Invalid("Unsupported mission source.");
        var path = Path.GetFullPath(destinationFile);
        RequireUnlinkedParent(path);
        var created = false;
        try
        {
            using var response = await GetAsync(update.DownloadUri, drive, null, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != update.Size)
                throw Invalid("The published size changed; check for updates again.");
            string hash;
            long received = 0;
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                created = true;
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                var lastProgress = Environment.TickCount64;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += count;
                    if (received > update.Size || received > MaximumMissionBytes) throw Invalid("The mission exceeds its published size.");
                    digest.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    if (Environment.TickCount64 - lastProgress >= 100)
                    {
                        progress?.Report(new(received, update.Size));
                        lastProgress = Environment.TickCount64;
                    }
                }
                hash = Convert.ToHexString(digest.GetHashAndReset());
            }
            if (received != update.Size) throw Invalid("The mission download is incomplete.");
            if (update.Sha256 is { } expected && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw Invalid("The mission SHA-256 does not match its published digest.");
            await using var file = File.OpenRead(path);
            using var json = await JsonDocument.ParseAsync(file, new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 128 }, cancellationToken).ConfigureAwait(false);
            var version = MissionReleaseVersion.FromTitle(Text(json.RootElement, "title"));
            if (Text(json.RootElement, "id") != MissionReleaseVersion.MissionId || version is null || MissionReleaseVersion.Compare(version, update.Version) != 0)
                throw Invalid("The downloaded mission identity or revision differs from the announced release.");
            progress?.Report(new(received, update.Size));
            return hash;
        }
        catch
        {
            // Only this attempt's newly created file is owned; never remove a pre-existing target.
            if (created) { RequireUnlinkedParent(path); File.Delete(path); }
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri url, bool drive, int? headBytes, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!Allowed(url, drive)) throw Invalid("The mission source redirected to an untrusted host.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (headBytes is { } count) request.Headers.Range = new RangeHeaderValue(0, count - 1);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var target = response.Headers.Location;
                response.Dispose();
                if (target is null) throw Invalid("The source returned an empty redirect.");
                url = target.IsAbsoluteUri ? target : new Uri(url, target);
                continue;
            }
            if (!response.IsSuccessStatusCode) { var status = response.StatusCode; response.Dispose(); throw new HttpRequestException($"Mission source HTTP {(int)status}.", null, status); }
            return response;
        }
        throw Invalid("Too many mission download redirects.");
    }

    private static bool IsInitialAsset(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal);

    private static bool Allowed(Uri uri, bool drive) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (drive ? uri.Host is "drive.google.com" or "drive.usercontent.google.com" || uri.Host.EndsWith(".googleusercontent.com", StringComparison.Ordinal)
            : uri.Host is "api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com");

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int limit, bool prefixOnly, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (bytes.Length < limit)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - bytes.Length)), token).ConfigureAwait(false);
            if (count == 0) return bytes.ToArray();
            bytes.Write(buffer, 0, count);
        }
        if (!prefixOnly && await stream.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) > 0)
            throw Invalid("Release metadata exceeds the safety limit.");
        return bytes.ToArray();
    }

    internal static void RequireUnlinkedParent(string file)
    {
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(file)!); parent is not null; parent = parent.Parent)
            if (!parent.Exists || (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw Invalid("The mission cache path must exist and cannot traverse links.");
        if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw Invalid("The mission cache file cannot be a link.");
    }

    private static string HeaderField(string head, string name)
    {
        var match = Regex.Match(head, "\"" + name + "\"\\s*:\\s*(\"(?:\\\\.|[^\"\\\\])*\")", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return match.Success ? JsonSerializer.Deserialize<string>(match.Groups[1].Value) ?? string.Empty : string.Empty;
    }
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;
    private static void ValidateSize(long size) { if (size <= 0 || size > MaximumMissionBytes) throw Invalid("The published mission size is invalid."); }
    private static ApplicationUpdateException Invalid(string message) => new("MISSION-UPDATE", message);
    public void Dispose() => _client.Dispose();
}
