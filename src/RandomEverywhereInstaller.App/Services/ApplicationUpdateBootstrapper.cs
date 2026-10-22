using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using RandomEverywhereInstaller.Infrastructure.Updates;

namespace RandomEverywhereInstaller.App.Services;

/// <summary>
/// Applies a verified executable update from a short-lived copy after the main process exits.
/// </summary>
internal static class ApplicationUpdateBootstrapper
{
    private const string ApplySwitch = "--apply-update";
    public static string FailureLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppEdition.RuntimeDirectoryName,
        "Updates",
        "update-error.log");

    public static bool IsApplyRequest(IReadOnlyCollection<string> arguments) =>
        arguments.Contains(ApplySwitch, StringComparer.OrdinalIgnoreCase);

    public static void StartApply(string downloadedExecutable, string expectedSha256)
    {
        var target = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path is unavailable.");
        ValidateExecutablePath(downloadedExecutable, nameof(downloadedExecutable));
        ValidateExecutablePath(target, nameof(target));

        var startInfo = new ProcessStartInfo
        {
            FileName = downloadedExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(downloadedExecutable)!
        };
        startInfo.ArgumentList.Add(ApplySwitch);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(target);
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--expected-sha256");
        startInfo.ArgumentList.Add(expectedSha256);

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The verified update helper could not be started.");
    }

    public static int ApplyAndRestart(IReadOnlyList<string> arguments)
    {
        try
        {
            var target = ReadArgument(arguments, "--target");
            var pidText = ReadArgument(arguments, "--wait-pid");
            var expectedHash = ReadArgument(arguments, "--expected-sha256");
            var source = Environment.ProcessPath
                ?? throw new InvalidOperationException("The update helper path is unavailable.");

            ValidateExecutablePath(source, nameof(source));
            ValidateExecutablePath(target, nameof(target));
            if (!Path.GetFileName(source).Equals(AppEdition.ExecutableAssetName, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(target).Equals(AppEdition.ExecutableAssetName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The update executable does not match this application edition.");
            }
            ValidateHash(expectedHash);
            if (!ComputeSha256(source).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The update helper failed its final SHA-256 verification.");
            }

            if (!int.TryParse(pidText, out var processId) || processId <= 0)
            {
                throw new InvalidOperationException("The previous process identifier is invalid.");
            }
            WaitForProcessExit(processId, TimeSpan.FromSeconds(60));

            var targetDirectory = Path.GetDirectoryName(target)
                ?? throw new InvalidOperationException("The installation directory is invalid.");
            var staged = Path.Combine(targetDirectory, $".{Path.GetFileName(target)}.update-new");
            var backup = target + ".previous";
            File.Copy(source, staged, overwrite: true);

            if (File.Exists(target))
            {
                File.Delete(backup);
                File.Replace(staged, target, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(staged, target);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
                WorkingDirectory = targetDirectory
            };
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The updated application could not be restarted.");
            return 0;
        }
        catch (Exception exception)
        {
            WriteFailureLog(exception);
            return 1;
        }
    }

    public static void CleanupPreviousExecutable()
    {
        try
        {
            var current = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(current))
            {
                File.Delete(current + ".previous");
                File.Delete(Path.Combine(Path.GetDirectoryName(current)!, $".{Path.GetFileName(current)}.update-new"));
            }

            CleanupUpdateCache();
        }
        catch
        {
            // Cleanup is best-effort and must not prevent startup.
        }
    }

    private static string ReadArgument(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }
        throw new InvalidOperationException($"Missing update argument: {name}");
    }

    private static void WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                throw new TimeoutException("The previous application instance did not close in time.");
            }
        }
        catch (ArgumentException)
        {
            // The original process already exited.
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ValidateExecutablePath(string path, string argumentName)
    {
        if (!Path.IsPathFullyQualified(path) ||
            !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The executable path is invalid.", argumentName);
        }
    }

    private static void ValidateHash(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("The expected update hash is invalid.");
        }
    }

    private static void WriteFailureLog(Exception exception)
    {
        try
        {
            var directory = Path.GetDirectoryName(FailureLogPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                FailureLogPath,
                $"TimestampUtc: {DateTime.UtcNow:O}{Environment.NewLine}{exception}");
        }
        catch
        {
            // Failure reporting cannot recover from a non-writable profile.
        }
    }

    private static void CleanupUpdateCache()
    {
        UpdateCacheCleanup.Clean(Path.GetDirectoryName(FailureLogPath)!, AppEdition.ExecutableAssetName);
    }
}
