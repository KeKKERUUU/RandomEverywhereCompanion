using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using RandomEverywhereInstaller.Core.Updates;

namespace RandomEverywhereInstaller.Infrastructure.Updates;

/// <summary>
/// Resolves public GitHub releases and downloads an exact, digest-verified EXE asset.
/// </summary>
public sealed class GitHubReleaseUpdateService : IApplicationUpdateService, IDisposable
{
    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/KeKKERUUU/RandomEverywhereCompanion/releases/latest");

    private readonly HttpClient _httpClient;

    public GitHubReleaseUpdateService()
        : this(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        })
    {
    }

    internal GitHubReleaseUpdateService(HttpMessageHandler handler)
    {
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RandomEverywhereInstaller-Updater/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");
    }

    public async Task<ApplicationUpdateInfo?> CheckForUpdateAsync(
        Version currentVersion,
        string assetName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);

        using var response = await _httpClient
            .GetAsync(LatestReleaseUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ApplicationUpdateException(
                "UPDATE-PRIVATE",
                "The release cannot be read anonymously. Publish releases from a public repository or public update repository.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ApplicationUpdateException(
                "UPDATE-CHECK",
                $"GitHub returned HTTP {(int)response.StatusCode} while checking for updates.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var tagName = root.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString()
            : null;

        if (!TryParseReleaseVersion(tagName, out var releaseVersion))
        {
            throw new ApplicationUpdateException("UPDATE-VERSION", "The latest GitHub release has an invalid version tag.");
        }

        if (releaseVersion <= currentVersion)
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new ApplicationUpdateException("UPDATE-ASSET", $"Release {tagName} has no downloadable assets.");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var urlText = asset.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString()
                : null;
            var digestText = asset.TryGetProperty("digest", out var digestElement)
                ? digestElement.GetString()
                : null;
            var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : 0L;

            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var downloadUri) ||
                !IsAllowedInitialDownloadUri(downloadUri))
            {
                throw new ApplicationUpdateException("UPDATE-ASSET", "The release asset has an invalid download URL.");
            }

            var sha256 = ParseSha256(digestText);
            return new ApplicationUpdateInfo(releaseVersion, tagName!, assetName, downloadUri, size, sha256);
        }

        throw new ApplicationUpdateException(
            "UPDATE-ASSET",
            $"Release {tagName} does not contain the required asset {assetName}.");
    }

    public async Task<string> DownloadAsync(
        ApplicationUpdateInfo update,
        string destinationDirectory,
        IProgress<ApplicationUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var finalPath = Path.Combine(destinationDirectory, update.AssetName);
        var partialPath = finalPath + ".partial";
        File.Delete(partialPath);

        try
        {
            using var response = await _httpClient
                .GetAsync(update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApplicationUpdateException(
                    "UPDATE-DOWNLOAD",
                    $"GitHub returned HTTP {(int)response.StatusCode} while downloading {update.AssetName}.");
            }

            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null || !IsAllowedRedirectUri(finalUri))
            {
                throw new ApplicationUpdateException("UPDATE-DOWNLOAD", "The release asset redirected to an untrusted host.");
            }

            var expectedLength = response.Content.Headers.ContentLength ?? (update.Size > 0 ? update.Size : null);
            long received = 0;
            string actualHash;

            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var destination = new FileStream(
                    partialPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                var lastProgressTimestamp = 0L;

                try
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        received += read;
                        var now = Stopwatch.GetTimestamp();
                        if (lastProgressTimestamp == 0 ||
                            now - lastProgressTimestamp >= Stopwatch.Frequency / 10 ||
                            received == expectedLength)
                        {
                            progress?.Report(new ApplicationUpdateProgress(received, expectedLength));
                            lastProgressTimestamp = now;
                        }
                    }
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    actualHash = Convert.ToHexString(hash.GetHashAndReset());
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            if (update.Size > 0 && received != update.Size)
            {
                throw new ApplicationUpdateException("UPDATE-SIZE", "The downloaded executable size does not match the release metadata.");
            }

            if (!actualHash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApplicationUpdateException("UPDATE-HASH", "The downloaded executable failed SHA-256 verification.");
            }

            File.Move(partialPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch
        {
            File.Delete(partialPath);
            throw;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static bool TryParseReleaseVersion(string? tagName, out Version version)
    {
        var value = tagName?.Trim();
        if (!string.IsNullOrEmpty(value) && value.StartsWith('v')) value = value[1..];
        value = value?.Split('-', 2)[0];
        return Version.TryParse(value, out version!);
    }

    private static string ParseSha256(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApplicationUpdateException("UPDATE-HASH", "The release asset does not publish a SHA-256 digest.");
        }

        var value = digest[prefix.Length..];
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ApplicationUpdateException("UPDATE-HASH", "The release asset publishes an invalid SHA-256 digest.");
        }
        return value.ToUpperInvariant();
    }

    private static bool IsAllowedInitialDownloadUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedRedirectUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
}
