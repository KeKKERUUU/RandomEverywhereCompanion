using System.Text.Json;
using System.Runtime.InteropServices;

namespace RandomEverywhereInstaller.Infrastructure.Installer;

internal sealed class InstallerLog
{
    private readonly object _sync = new();
    private readonly string _textPath;
    private readonly string _jsonlPath;

    public InstallerLog(string runtimeDirectoryName)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8];
        var preferred = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            runtimeDirectoryName,
            "Logs",
            stamp);
        try
        {
            Directory.CreateDirectory(preferred);
            DirectoryPath = preferred;
        }
        catch (UnauthorizedAccessException)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), runtimeDirectoryName, "Logs", stamp);
            Directory.CreateDirectory(DirectoryPath);
        }
        _textPath = Path.Combine(DirectoryPath, "installer.log");
        _jsonlPath = Path.Combine(DirectoryPath, "operations.jsonl");
        Info("APP-ENVIRONMENT", "Diagnostic log initialized.", new
        {
            applicationVersion = typeof(InstallerLog).Assembly.GetName().Version?.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            uiCulture = System.Globalization.CultureInfo.CurrentUICulture.Name,
            timeZone = TimeZoneInfo.Local.Id,
            processId = Environment.ProcessId,
            logDirectory = DirectoryPath
        });
    }

    public string DirectoryPath { get; }

    public void Info(string code, string message, object? data = null) => Write("INFO", code, message, data);
    public void Warning(string code, string message, object? data = null) => Write("WARNING", code, message, data);
    public void Error(string code, string message, object? data = null) => Write("ERROR", code, message, data);
    public void Success(string code, string message, object? data = null) => Write("SUCCESS", code, message, data);

    private void Write(string level, string code, string message, object? data)
    {
        var timestamp = DateTimeOffset.Now;
        var serializedData = data is null ? null : JsonSerializer.Serialize(data);
        var text = $"{timestamp:O} [{level}] [{code}] {message}" +
                   (serializedData is null ? string.Empty : $" Data={serializedData}") +
                   Environment.NewLine;
        var json = JsonSerializer.Serialize(new { timestamp, level, code, message, data }) + Environment.NewLine;
        try
        {
            lock (_sync)
            {
                File.AppendAllText(_textPath, text);
                File.AppendAllText(_jsonlPath, json);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Diagnostics are best effort; an unavailable log must never prevent rollback.
            System.Diagnostics.Trace.TraceWarning("Installer diagnostic write failed: {0}", error.Message);
        }
    }
}
