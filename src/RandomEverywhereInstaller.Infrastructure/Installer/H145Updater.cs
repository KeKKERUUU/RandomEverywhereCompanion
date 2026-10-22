using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Core.Models;

namespace RandomEverywhereInstaller.Infrastructure.Installer;

// Explicit, opt-in aircraft maintenance. Sources retained from the previous 500 updater.
public sealed partial class InstallerEngine
{
    private static readonly H145UpdateSource[] UpdateSources =
    [
        new("H145_BASE_UPDATE", "1eVbIdfYexv-wOG0MpMZTZUjo_HQ7OxlV", 90_642_696, false),
        new("H145_ACTION_PACK_UPDATE", "1HZmW1BVbF87vlg4tSDmv1S_LRNffyHYe", 71_748_458, true)
    ];


    private sealed record H145UpdateSource(string Id, string GoogleDriveFileId, long ExpectedDownloadBytes, bool RequiresInstalledActionPack);
    public static IReadOnlyList<string> GetH145UpdateSourceUrls(H145InstallationInfo installation) => installation.IsVersion500OrLater
        ? [] : UpdateSources.Where(source => !source.RequiresInstalledActionPack || installation.ActionPackInstalled)
            .Select(source => "https://drive.usercontent.google.com/download?id=" + source.GoogleDriveFileId + "&export=download").ToArray();
    public async Task<H145InstallationInfo> UpdateH145Async(string communityPath, IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
    {
        var community = ValidateCommunityPath(communityPath, requireWrite: true);
        EnsureSimulatorClosed();
        var installed = DetectH145(community);
        if (!installed.Installed) throw new InstallerException("H145-001", "Install the licensed H145 Base Pack before using this updater.");
        if (installed.IsVersion500OrLater) return RefreshH145LayoutOnly(community, installed, progress, cancellationToken);
        if (installed.Version is null) throw new InstallerException("H145-002", "No unambiguous H145 version marker was found; automatic patching is blocked.");
        var sources = UpdateSources.Where(source => !source.RequiresInstalledActionPack || installed.ActionPackInstalled).ToArray();
        EnsureH145UpdateDiskSpace(community, sources);
        cancellationToken.ThrowIfCancellationRequested();
        var session = CreateSession("h145-update", community, allowSystemDriveFallback: false);
        var transaction = new InstallTransaction(community, session.Backup, Log);
        var layoutBackups = new List<FileRollbackState>();
        using var httpClient = new HttpClient(_httpHandler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(30) };
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RandomEverywhereCompanion", "1.2"));
        H145InstallationInfo result;
        try
        {
            var packages = new List<PackageScanResult>();
            long expanded = 0, rollbackBytes = 0;
            for (var index = 0; index < sources.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[index];
                var archive = Path.Combine(session.Downloads, source.Id + ".zip");
                await DownloadGoogleDriveFileWithRetryAsync(httpClient, source.GoogleDriveFileId, archive, progress, index, sources.Length, cancellationToken).ConfigureAwait(false);
                var roots = ArchivePackageInspector.Inspect(archive);
                var expected = source.RequiresInstalledActionPack ? H145ActionPackFolder : H145BasePackageFolder;
                if (roots.Count != 1 || !roots[0].FolderName.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InstallerException("H145-006", "The patch contains unexpected package roots; no files were installed.");
                using (var inspection = ArchivePackageInspector.Open(archive))
                {
                    foreach (var entry in inspection.Entries.Where(entry => !entry.IsDirectory))
                    {
                        expanded = checked(expanded + entry.Size);
                        var path = ArchivePackageInspector.ValidateEntry(entry);
                        var prefix = roots[0].RelativePath;
                        if (prefix.Length > 0 && !path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) continue;
                        var relative = prefix.Length > 0 ? path[(prefix.Length + 1)..] : path;
                        var destination = GetSafeChildPath(Path.Combine(community, expected), relative);
                        EnsureNoReparsePointsInsideRoot(community, destination);
                        if (File.Exists(destination)) rollbackBytes = checked(rollbackBytes + new FileInfo(destination).Length);
                    }
                }
                packages.Add(new PackageScanResult(new PackageDefinition(source.Id, expected, true, index, ".*",
                    new HashSet<SimulatorType> { SimulatorType.Msfs2020, SimulatorType.Msfs2024 }, string.Empty), PackageState.Found, archive, roots, null));
            }
            var drive = new DriveInfo(Path.GetPathRoot(community)!);
            if (drive.AvailableFreeSpace < checked(expanded * 2 + rollbackBytes + 64L * 1024 * 1024))
                throw new InstallerException("PATH-008", "Insufficient space for the actual expanded H145 patch and targeted rollback.");
            var folders = sources.Select(source => source.RequiresInstalledActionPack ? H145ActionPackFolder : H145BasePackageFolder).ToArray();
            BackupH145Layouts(community, folders, Path.Combine(session.Backup, "Layouts"), layoutBackups);
            await InstallPackagesAsync(packages.ToArray(), session.Staging, transaction, progress, 35, 85, cancellationToken).ConfigureAwait(false);
            // Layouts describe the final merged files, never the pre-update package.
            RunBuiltInLayoutGenerator(community, progress, cancellationToken, 85, 99, folders, folders, createMissingLayouts: true);
            cancellationToken.ThrowIfCancellationRequested();
            result = DetectH145(community);
            if (!result.IsVersion500OrLater) throw new InstallerException("H145-009", "Version_500.txt was not found after the patch; restoring the previous files.");
            transaction.Commit();
        }
        catch (Exception error)
        {
            Log.Error("H145-UPDATE-FAIL", "H145 update failed; restoring only touched files.", CreateExceptionDiagnostic(error));
            try { transaction.Rollback(); foreach (var state in layoutBackups) RestoreFileState(community, state); CleanupSession(session); }
            catch (Exception recovery)
            {
                throw new InstallerException("H145-010", "H145 rollback incomplete; recovery files retained at " + session.Backup, new AggregateException(error, recovery));
            }
            throw;
        }
        FinishCommittedOperation(session, progress,
            new InstallerProgress("Completed", "H145 build 500 verified", result.PackagePath, 100, 1, 1));
        Log.Success("H145-UPDATE", "Build 500 patch and scoped post-update layouts verified.", new { result.Version, result.ActionPackInstalled });
        return result;
    }
    private static async Task<HttpResponseMessage> GetAllowedH145ResponseAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        for (var hop = 0; hop <= 5; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !IsAllowedGoogleHost(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
                throw new InstallerException("DL-003", "H145 download target is not an approved HTTPS source.");
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode < 300 || (int)response.StatusCode > 399) return response;
            var redirect = response.Headers.Location;
            response.Dispose();
            if (redirect is null) throw new InstallerException("DL-003", "Download redirect has no target.");
            uri = redirect.IsAbsoluteUri ? redirect : new Uri(uri, redirect);
        }
        throw new InstallerException("DL-003", "Too many H145 download redirects.");
    }

    // A current H145 installation needs no network or package merge; only its own layout is rebuilt.
    private H145InstallationInfo RefreshH145LayoutOnly(
        string community,
        H145InstallationInfo installedH145,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken)
    {
        Log.Info("H145-LAYOUT-ONLY-START", "H145 version 500 or later detected; patch downloads were skipped.", new
        {
            community,
            installedH145.Version,
            installedH145.VersionMarkers,
            package = H145BasePackageFolder
        });
        progress.Report(new InstallerProgress(
            "Layouts",
            "H145 version 500 detected; skipping patch download",
            installedH145.PackagePath,
            10,
            0,
            1));

        var layoutFolders = installedH145.ActionPackInstalled ? new[] { H145BasePackageFolder, H145ActionPackFolder } : new[] { H145BasePackageFolder };
        cancellationToken.ThrowIfCancellationRequested();
        var session = CreateSession("h145-layout-refresh", community, allowSystemDriveFallback: false);
        var layoutBackups = new List<FileRollbackState>();
        int updatedLayouts;
        H145InstallationInfo detected;
        try
        {
            BackupH145Layouts(
                community,
                layoutFolders,
                Path.Combine(session.Backup, "Layouts"),
                layoutBackups);
            updatedLayouts = RunBuiltInLayoutGenerator(
                community,
                progress,
                cancellationToken,
                20,
                95,
                layoutFolders,
                layoutFolders,
                createMissingLayouts: true);
            cancellationToken.ThrowIfCancellationRequested();
            detected = DetectH145(community);
            if (!detected.IsVersion500OrLater)
            {
                throw new InstallerException("H145-009", "Version_500.txt disappeared while refreshing the H145 layout.");
            }
        }
        catch (Exception exception)
        {
            Log.Error("H145-LAYOUT-ONLY-FAIL", "The H145 layout-only refresh failed; restoring the previous layout.", CreateExceptionDiagnostic(exception));
            try
            {
                foreach (var state in layoutBackups) RestoreFileState(community, state);
                CleanupSession(session);
            }
            catch (Exception rollbackException)
            {
                throw new InstallerException(
                    "H145-010",
                    $"The H145 layout refresh failed and its layout backup could not be restored. Logs: {Log.DirectoryPath}",
                    new AggregateException(exception, rollbackException));
            }

            throw;
        }
        FinishCommittedOperation(session, progress,
            new InstallerProgress("Completed", "H145 layout refreshed", detected.PackagePath, 100, 1, 1));
        Log.Success("H145-LAYOUT-ONLY-COMPLETE", "The H145 layout was regenerated without downloading patches.", new
        {
            detected.Version,
            updatedLayouts,
            package = H145BasePackageFolder
        });
        return detected;
    }

    // Record layout and manifest absence/content; generation may update total_package_size.
    private static void BackupH145Layouts(string community, IReadOnlyCollection<string> folders, string backupRoot, List<FileRollbackState> states)
    {
        Directory.CreateDirectory(backupRoot);
        foreach (var folder in folders)
        {
            foreach (var filename in new[] { "layout.json", "manifest.json" })
            {
                var target = Path.Combine(GetSafeCommunityPackagePath(community, folder), filename);
                EnsureNoReparsePointsInsideRoot(community, target);
                var existed = File.Exists(target);
                var backup = existed ? Path.Combine(backupRoot, folder + "." + filename) : null;
                if (backup is not null) File.Copy(target, backup, overwrite: false);
                states.Add(new FileRollbackState(target, backup, existed));
            }
        }
    }

    private async Task DownloadGoogleDriveFileAsync(
        HttpClient httpClient,
        string fileId,
        string outputPath,
        IProgress<InstallerProgress> progress,
        int sourceIndex,
        int sourceCount,
        CancellationToken cancellationToken)
    {
        var partial = outputPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var response = await GetGoogleDriveResponseAsync(httpClient, fileId, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InstallerException("DL-003", "Google Drive returned an HTML confirmation page instead of the update archive.");
        }

        var total = response.Content.Headers.ContentLength;
        long downloaded = 0;
        var progressClock = System.Diagnostics.Stopwatch.StartNew();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 128];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded = checked(downloaded + read);
                if (downloaded > 1024L * 1024 * 1024) throw new InstallerException("DL-006", "H145 patch exceeds the permitted download size.");
                var local = total > 0 ? 100d * downloaded / total.Value : 0;
                var overall = 30d * (sourceIndex + local / 100d) / sourceCount;
                if (progressClock.ElapsedMilliseconds >= 150 || downloaded == total)
                {
                    progress.Report(new InstallerProgress("Download", "Downloading H145 update", $"{downloaded / 1024 / 1024} MB", overall, downloaded, total ?? 0));
                    progressClock.Restart();
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (downloaded < 1024)
        {
            throw new InstallerException("DL-006", "Downloaded update file is unexpectedly small.");
        }

        File.Move(partial, outputPath, true);
        using var digestInput = File.OpenRead(outputPath);
        var sha256 = Convert.ToHexString(SHA256.HashData(digestInput));
        Log.Info("DL-COMPLETE", "Download completed.", new { fileId, outputPath, downloaded, sha256 });
    }

    // Retries restart from a clean partial file to prevent mixed or locked downloads.
    private async Task DownloadGoogleDriveFileWithRetryAsync(
        HttpClient httpClient,
        string fileId,
        string outputPath,
        IProgress<InstallerProgress> progress,
        int sourceIndex,
        int sourceCount,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadGoogleDriveFileAsync(httpClient, fileId, outputPath, progress, sourceIndex, sourceCount, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or IOException or TaskCanceledException)
            {
                lastError = exception;
                Log.Warning("DL-RETRY", $"Download attempt {attempt} failed.", new
                {
                    fileId,
                    exceptionType = exception.GetType().FullName,
                    exception.Message,
                    innerException = exception.InnerException?.Message
                });
                TryDelete(outputPath + ".partial");
                TryDelete(outputPath);
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InstallerException(
            "DL-001",
            $"H145 update download failed after three attempts. Last error: {lastError?.Message}",
            lastError!);
    }

    private static async Task<HttpResponseMessage> GetGoogleDriveResponseAsync(HttpClient httpClient, string fileId, CancellationToken cancellationToken)
    {
        // Google serves public binary downloads from drive.usercontent.google.com.
        // Starting from drive.google.com/uc adds an HTML/redirect hop that is frequently
        // rejected by proxies and has caused otherwise public files to fail repeatedly.
        var url = $"https://drive.usercontent.google.com/download?id={Uri.EscapeDataString(fileId)}&export=download&confirm=t";
        var response = await GetAllowedH145ResponseAsync(httpClient, new Uri(url), cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentType?.MediaType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) != true)
        {
            return response;
        }

        if (response.Content.Headers.ContentLength > 256 * 1024) { response.Dispose(); throw new InstallerException("DL-003", "Oversized download confirmation page."); }
        string html;
        try { await response.Content.LoadIntoBufferAsync(256 * 1024, cancellationToken).ConfigureAwait(false); html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); }
        catch { response.Dispose(); throw; }
        var form = DownloadFormRegex().Match(html);
        if (!form.Success)
        {
            return response;
        }

        var action = WebUtility.HtmlDecode(form.Groups[1].Value);
        if (!Uri.TryCreate(action, UriKind.Absolute, out var actionUri) || (actionUri.Scheme != Uri.UriSchemeHttps || !IsAllowedGoogleHost(actionUri.Host)))
        {
            response.Dispose();
            throw new InstallerException("DL-003", "Google Drive confirmation redirected to an unexpected host.");
        }

        var values = HiddenInputRegex().Matches(form.Groups[2].Value)
            .Select(match => new KeyValuePair<string, string>(WebUtility.HtmlDecode(match.Groups[1].Value), WebUtility.HtmlDecode(match.Groups[2].Value)))
            .ToArray();
        var query = string.Join("&", values.Select(value => $"{Uri.EscapeDataString(value.Key)}={Uri.EscapeDataString(value.Value)}"));
        response.Dispose();
        return await GetAllowedH145ResponseAsync(httpClient, new Uri(action + (action.Contains('?') ? "&" : "?") + query), cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureH145UpdateDiskSpace(string community, IReadOnlyCollection<H145UpdateSource> sources)
    {
        var downloadBytes = sources.Sum(source => source.ExpectedDownloadBytes);
        var installedBytes = GetDirectorySizeSafe(Path.Combine(community, H145BasePackageFolder));
        var requirements = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        AddDriveRequirement(requirements, community, Math.Max(1024L * 1024 * 1024, downloadBytes * 4 + Math.Min(installedBytes, downloadBytes * 4)));

        foreach (var requirement in requirements)
        {
            var drive = new DriveInfo(requirement.Key);
            if (drive.AvailableFreeSpace >= requirement.Value) continue;

            throw new InstallerException(
                "PATH-008",
                $"Insufficient free space before the H145 update. Drive {drive.Name} has {drive.AvailableFreeSpace / 1024 / 1024} MB available; " +
                $"the update safety budget requires {requirement.Value / 1024 / 1024} MB. Free space and try again.");
        }
    }

    private static void AddDriveRequirement(Dictionary<string, long> requirements, string path, long bytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new InstallerException("PATH-008", $"Unable to determine the drive for: {path}");
        requirements[root] = requirements.TryGetValue(root, out var current) ? checked(current + bytes) : bytes;
    }

    private static long GetDirectorySizeSafe(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var file in Directory.EnumerateFiles(current)) total = checked(total + new FileInfo(file).Length);
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) pending.Enqueue(directory);
            }
        }
        return total;
    }

    private static bool IsAllowedGoogleHost(string host) =>
        host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("drive.usercontent.google.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase);
    [GeneratedRegex("<form[^>]+action=\\\"([^\\\"]+)\\\"[^>]*>(.*?)</form>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DownloadFormRegex();
    [GeneratedRegex("<input[^>]+name=\\\"([^\\\"]+)\\\"[^>]+value=\\\"([^\\\"]*)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInputRegex();
}
