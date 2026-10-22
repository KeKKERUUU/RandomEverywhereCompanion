namespace RandomEverywhereInstaller.Core.Progress;

public enum ProgressSeverity
{
    Idle,
    Information,
    Success,
    Warning,
    Error
}

public sealed record OperationProgress(
    string MessageKey,
    string DetailKey,
    double? OverallPercent,
    long? CurrentItem,
    long? TotalItems,
    ProgressSeverity Severity);
