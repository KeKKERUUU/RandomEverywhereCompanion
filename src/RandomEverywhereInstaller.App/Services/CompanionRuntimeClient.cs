using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RandomEverywhereInstaller.App.Services;

public sealed record RuntimeProgress(double Percent, string Message);

/// <summary>Starts the embedded voice worker lazily and serializes IPC requests.</summary>
public sealed class CompanionRuntimeClient(string storagePath) : IAsyncDisposable
{
    private readonly SemaphoreSlim _requests = new(1, 1);
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    public bool IsStarted => _process is { HasExited: false } && _pipe?.IsConnected == true;

    public async Task<JsonElement> RequestAsync(string command, object? payload = null, IProgress<RuntimeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            await EnsureStartedAsync(timeout.Token).ConfigureAwait(false);
            await _writer!.WriteLineAsync(JsonSerializer.Serialize(new { Command = command, Payload = payload }).AsMemory(), timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var line = await _reader!.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                    ?? throw new IOException("The voice runtime disconnected.");
                using var response = JsonDocument.Parse(line);
                var root = response.RootElement;
                if (root.GetProperty("Type").GetString() == "progress")
                {
                    progress?.Report(new RuntimeProgress(root.GetProperty("Percent").GetDouble(), root.GetProperty("Message").GetString() ?? string.Empty));
                    continue;
                }
                if (!root.GetProperty("Success").GetBoolean())
                    throw new InvalidOperationException(root.GetProperty("Error").GetString());
                return root.GetProperty("Data").Clone();
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            TerminateWorker();
            throw;
        }
        finally { _requests.Release(); }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (IsStarted) return;
        TerminateWorker();
        Directory.CreateDirectory(storagePath);
        var executable = await Task.Run(ExtractRuntime, cancellationToken).ConfigureAwait(false);
        var pipeName = "RECompanion-" + Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add(storagePath);
        info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _process = Process.Start(info) ?? throw new IOException("The voice runtime could not start.");
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await _pipe.ConnectAsync(20000, cancellationToken).ConfigureAwait(false);
        _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
    }

    private string ExtractRuntime()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string prefix = "CompanionPayload.";
        var directory = Path.Combine(storagePath, "Runtime", AppEdition.Version);
        Directory.CreateDirectory(directory);
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var fileName = name[prefix.Length..];
            if (Path.GetFileName(fileName) != fileName) throw new InvalidDataException("Invalid embedded runtime path.");
            var destination = Path.Combine(directory, fileName);
            using var source = assembly.GetManifestResourceStream(name)!;
            var hash = SHA256.HashData(source);
            if (File.Exists(destination))
            {
                using var existing = File.OpenRead(destination);
                if (CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(existing))) continue;
            }
            source.Position = 0;
            var temporary = destination + ".new";
            using (var output = File.Create(temporary)) source.CopyTo(output);
            File.Move(temporary, destination, overwrite: true);
        }
        var result = Path.Combine(directory, "CompanionRuntime.exe");
        if (!File.Exists(result)) throw new FileNotFoundException("The embedded voice runtime is missing.", result);
        return result;
    }

    private void TerminateWorker()
    {
        _reader?.Dispose(); _writer?.Dispose(); _pipe?.Dispose();
        _reader = null; _writer = null; _pipe = null;
        if (_process is not null)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            _process.Dispose(); _process = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsStarted)
        {
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _ = await RequestAsync("shutdown", cancellationToken: cancellation.Token).ConfigureAwait(false);
                if (_process is not null) await _process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException) { }
        }
        TerminateWorker(); _requests.Dispose();
    }
}
