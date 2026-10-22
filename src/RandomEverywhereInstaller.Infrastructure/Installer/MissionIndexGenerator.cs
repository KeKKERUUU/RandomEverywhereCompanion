using System.Text.Json;
using RandomEverywhereInstaller.Core.Installer;

namespace RandomEverywhereInstaller.Infrastructure.Installer;

/// <summary>HPG-compatible directory tree and Base64 mission payloads, without shell execution.</summary>
internal static class MissionIndexGenerator
{
    private const long MaximumMissionBytes = 256L * 1024 * 1024;
    private const long MaximumIndexBytes = 1024L * 1024 * 1024;
    private const int MaximumFiles = 10000;
    private sealed class Node
    {
        public SortedDictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Files { get; } = [];
    }

    internal static int Generate(string community, CancellationToken cancellationToken, Action<string, int>? fileReady = null)
    {
        community = Path.GetFullPath(community);
        RequireUnlinked(community);
        var aircraft = Path.Combine(community, "hpg-airbus-h145");
        if (!Directory.Exists(aircraft)) throw new InstallerException("INDEX-001", "The H145 base package is required to store its mission index.");
        var target = Path.Combine(aircraft, "html_ui", "HPGH145-System", "GeneratedMissionIndex.json");
        RequireUnlinked(target);
        var tree = new Node();
        var files = 0;
        foreach (var package in new DirectoryInfo(community).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((package.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            // Never follow package junctions to silently index outside the selected Community.
            RequireUnlinked(package.FullName);
            var missionRoot = Path.Combine(package.FullName, "hpgmission");
            if (!Directory.Exists(missionRoot)) continue;
            foreach (var file in Discover(missionRoot, 0, cancellationToken))
            {
                if (++files > MaximumFiles) throw new InstallerException("INDEX-LIMIT", "Too many mission files for one index.");
                var relative = Path.GetRelativePath(package.FullName, Path.GetDirectoryName(file)!);
                var node = tree;
                foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
                {
                    if (!node.Children.TryGetValue(segment, out var child)) node.Children[segment] = child = new Node();
                    node = child;
                }
                node.Files.Add(file);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        RequireUnlinked(target);
        var temporary = Path.Combine(directory, ".mission-index-" + Guid.NewGuid().ToString("N") + ".tmp");
        var processed = 0;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
                    WriteNode(writer, tree, file => { processed++; fileReady?.Invoke(file, processed); }, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            RequireUnlinked(target);
            File.Move(temporary, target, overwrite: true);
            return processed;
        }
        finally
        {
            RequireUnlinked(temporary);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static IEnumerable<string> Discover(string path, int depth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 64) throw new InstallerException("INDEX-LIMIT", "Mission directory nesting exceeds the safety limit.");
        RequireUnlinked(path);
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            RequireUnlinked(entry.FullName);
            if (entry is DirectoryInfo)
                foreach (var child in Discover(entry.FullName, depth + 1, token)) yield return child;
            else if (Path.GetExtension(entry.Name).Equals(".json", StringComparison.OrdinalIgnoreCase)) yield return entry.FullName;
        }
    }

    private static void WriteNode(Utf8JsonWriter writer, Node node, Action<string> ready, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        writer.WriteStartObject();
        foreach (var (name, child) in node.Children)
        {
            writer.WritePropertyName(name);
            WriteNode(writer, child, ready, token);
        }
        if (node.Files.Count > 0)
        {
            writer.WriteStartArray("*");
            foreach (var file in node.Files)
            {
                token.ThrowIfCancellationRequested();
                RequireUnlinked(file);
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > MaximumMissionBytes) throw new InstallerException("INDEX-LIMIT", $"Mission exceeds the size limit: {file}");
                try
                {
                    using var reader = new StreamReader(input, new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                    // UTF-8/UTF-16 BOMs are accepted; normalize to ASCII-safe JSON escapes so
                    // existing JavaScript atob + JSON.parse consumers retain non-ASCII text.
                    var text = new System.Text.StringBuilder();
                    var buffer = new char[32768];
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        text.Append(buffer, 0, read);
                    }
                    token.ThrowIfCancellationRequested();
                    using var document = JsonDocument.Parse(text.ToString(), new JsonDocumentOptions { MaxDepth = 128 });
                    token.ThrowIfCancellationRequested();
                    var normalized = JsonSerializer.SerializeToUtf8Bytes(document.RootElement);
                    if (writer.BytesCommitted + writer.BytesPending + ((long)normalized.Length + 2) / 3 * 4 > MaximumIndexBytes)
                        throw new InstallerException("INDEX-LIMIT", "Generated mission index exceeds the size limit.");
                    writer.WriteBase64StringValue(normalized);
                }
                catch (JsonException error) { throw new InstallerException("INDEX-JSON", $"Invalid mission JSON: {file}", error); }
                catch (System.Text.DecoderFallbackException error) { throw new InstallerException("INDEX-JSON", $"Invalid mission text encoding: {file}", error); }
                writer.Flush();
                ready(file);
                token.ThrowIfCancellationRequested();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static void RequireUnlinked(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InstallerException("INDEX-LINK", $"Mission indexing cannot traverse a linked path: {current}");
    }
}
