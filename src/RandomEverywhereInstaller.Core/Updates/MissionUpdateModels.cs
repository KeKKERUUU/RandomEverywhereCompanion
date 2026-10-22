using RandomEverywhereInstaller.Core.Installer;

namespace RandomEverywhereInstaller.Core.Updates;

public sealed record MissionUpdateInfo(AndrewUpdateVersion Version, string Tag, string Notes,
    Uri DownloadUri, long Size, string? Sha256, string Source);

public interface IMissionUpdateService
{
    Task<MissionUpdateInfo> CheckAsync(CancellationToken cancellationToken);
    Task<string> DownloadAsync(MissionUpdateInfo update, string destinationFile,
        IProgress<ApplicationUpdateProgress>? progress, CancellationToken cancellationToken);
}
