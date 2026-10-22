using System.Reflection;

namespace RandomEverywhereInstaller.App;

/// <summary>
/// Defines the companion identity and the exact GitHub release asset name.
/// </summary>
internal static class AppEdition
{
    public const string ProductName = "Random & Everywhere Companion";
    public const string RuntimeDirectoryName = "RandomAndEverywhereInstaller";
    public const string ExecutableAssetName = "RandomAndEverywhereCompanion.exe";


    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informational.Split('+', 2)[0];
    }
}
