using RandomEverywhereInstaller.Core.Models;

namespace RandomEverywhereInstaller.Core.Companion;

public enum CompanionAudioMode { Recorded, Synthetic }

/// <summary>Shared paths and preferences; each audio mode retains its own choices.</summary>
public sealed class CompanionSettings
{
    public string Culture { get; set; } = "en-US";
    public SimulatorType Simulator { get; set; } = SimulatorType.Msfs2024;
    public string CommunityPath { get; set; } = string.Empty;
    public string AudioStoragePath { get; set; } = string.Empty;
    public CompanionAudioMode AudioMode { get; set; } = CompanionAudioMode.Recorded;
    public string WorkFolderPath { get; set; } = string.Empty;
    public string MainPage { get; set; } = "Audio";
    public bool WelcomeShown { get; set; }
}

public static class CompanionAudioPolicy
{
    public static bool ShouldPlayCounter(double? previous, double current) =>
        previous is double baseline && double.IsFinite(baseline) &&
        double.IsFinite(current) && current > baseline;

    public static bool AllowsSynthesizedSpeech(CompanionAudioMode mode) => mode == CompanionAudioMode.Synthetic;
}
