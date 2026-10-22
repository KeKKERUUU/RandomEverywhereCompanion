using System.IO;
using RandomEverywhereInstaller.Core.Installer;

namespace RandomEverywhereInstaller.App.ViewModels;

public sealed class PackageStatusViewModel
{
    public PackageStatusViewModel(PackageScanResult result, string stateText, string requirementText)
    {
        Result = result;
        Name = result.Definition.DisplayName;
        FileName = result.ArchivePath is null ? result.Detail ?? string.Empty : Path.GetFileName(result.ArchivePath);
        StateText = stateText;
        RequirementText = requirementText;
        Required = result.Definition.Required;
        DownloadUrl = result.Definition.DownloadUrl;
        Detail = result.Detail ?? string.Empty;
        ErrorDetail = result.State == PackageState.Found ? string.Empty : Detail;
        (StatusGlyph, StatusColor) = result.State switch
        {
            PackageState.Found => ("✓", "#38D985"),
            PackageState.Installed => ("✓", "#38D985"),
            PackageState.OptionalMissing => ("!", "#F3BF5B"),
            PackageState.RequiredMissing => ("×", "#F46D75"),
            PackageState.Ambiguous => ("?", "#FF923D"),
            _ => ("×", "#F46D75")
        };
    }

    public PackageScanResult Result { get; }
    public string Name { get; }
    public string FileName { get; }
    public string StateText { get; }
    public string RequirementText { get; }
    public string StatusGlyph { get; }
    public string StatusColor { get; }
    public bool Required { get; }
    public string DownloadUrl { get; }
    public bool ShowDownload => Result.State is not PackageState.Found and not PackageState.Installed &&
                                !string.IsNullOrWhiteSpace(DownloadUrl);
    public string Detail { get; }
    public string ErrorDetail { get; }
}
