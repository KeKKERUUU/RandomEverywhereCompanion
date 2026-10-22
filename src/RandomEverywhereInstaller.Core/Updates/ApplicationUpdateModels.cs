namespace RandomEverywhereInstaller.Core.Updates;

/// <summary>
/// Describes a verified application executable published in a GitHub release.
/// </summary>
public sealed record ApplicationUpdateInfo(
    Version Version,
    string TagName,
    string AssetName,
    Uri DownloadUri,
    long Size,
    string Sha256);

/// <summary>
/// Reports streaming download progress without buffering the executable in memory.
/// </summary>
public sealed record ApplicationUpdateProgress(long BytesReceived, long? TotalBytes)
{
    public double Percent => TotalBytes is > 0
        ? Math.Clamp(100d * BytesReceived / TotalBytes.Value, 0d, 100d)
        : 0d;
}

public interface IApplicationUpdateService
{
    Task<ApplicationUpdateInfo?> CheckForUpdateAsync(
        Version currentVersion,
        string assetName,
        CancellationToken cancellationToken);

    Task<string> DownloadAsync(
        ApplicationUpdateInfo update,
        string destinationDirectory,
        IProgress<ApplicationUpdateProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class ApplicationUpdateException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
