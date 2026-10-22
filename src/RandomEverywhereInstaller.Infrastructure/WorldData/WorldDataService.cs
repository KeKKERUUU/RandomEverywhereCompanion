using System.Collections;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RandomEverywhereInstaller.Infrastructure.Installer;

namespace RandomEverywhereInstaller.Infrastructure.WorldData;

public sealed record WorldEntry(string Kind, bool Removal, string Json, string Label);
public sealed record WorldSyncResult(int Revision, int Counter, bool FromCache, int Hangars, int Hospitals);
public sealed record WorldFileChange(string Path, byte[] Before, byte[] After);
public sealed record WorldDatabasePlan(string Community, IReadOnlyList<WorldFileChange> Changes, string StateJson, string PreviousState, bool Restore);

/// <summary>Owns scoped persistence and explicit synchronization for Andrew's world-data contract.</summary>
public sealed class WorldDataService
{
    public const string Repository = "https://raw.githubusercontent.com/davierosoft/HEMS-random-everywhere-mission/main/shared-data/";
    public const string IssueUrl = "https://github.com/davierosoft/HEMS-random-everywhere-mission/issues/new";
    public const string TableName = "table_R&E_shared_waypoints.json";
    public static readonly string[] DatabaseTypes = ["airport", "airport-nocode", "airport-mil", "apron", "H_hospital", "H_civil", "runway"];
    private const int MaxBytes = 32 * 1024 * 1024;
    private readonly string _root;
    private readonly Action _ensureClosed;
    private readonly HttpClient? _http;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private string LocalPath => Path.Combine(_root, "local.json");
    private string CachePath => Path.Combine(_root, "published.json");
    private string StatePath => Path.Combine(_root, "database-state.json");
    private string CounterPath => Path.Combine(_root, "counter.json");
    public WorldDataService(string storage, HttpClient? http = null, Action? ensureClosed = null)
    {
        _root = Path.GetFullPath(storage);
        _http = http;
        _ensureClosed = ensureClosed ?? EnsureSimulatorClosed;
    }

    public int ReadCounter()
    {
        var value = File.Exists(CounterPath) ? JsonSerializer.Deserialize<int>(Read(CounterPath)) : 0;
        return value >= 0 ? value : throw new InvalidDataException("Invalid world-data revision counter.");
    }
    public IReadOnlyList<WorldEntry> Entries()
    {
        if (!File.Exists(LocalPath)) return [];
        var entries = JsonSerializer.Deserialize<List<WorldEntry>>(Read(LocalPath)) ?? throw new InvalidDataException("Invalid local world-data list.");
        foreach (var entry in entries) Validate(entry);
        return entries;
    }

    public async Task SaveAsync(WorldEntry entry, WorldEntry? replacing = null)
    {
        Validate(entry);
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = Entries().ToList();
            if (replacing is not null && !entries.Remove(replacing)) throw new IOException("The selected entry changed. Reload the list.");
            if (entry.Kind is "hangar_wps" or "hospitals_wps")
            {
                var id = Js.Str(Js.Obj(Js.Parse(entry.Json)), "id");
                if (entries.Any(e => e.Kind == entry.Kind && string.Equals(Js.Str(Js.Obj(Js.Parse(e.Json)), "id"), id, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("This waypoint name already exists. Select it and edit it instead.");
            }
            if (entries.Any(e => e.Kind == entry.Kind && e.Removal == entry.Removal && e.Json == entry.Json)) throw new IOException("This entry already exists.");
            entries.Add(entry);
            SaveLocal(entries);
        }
        finally { Gate.Release(); }
    }

    public async Task RemoveAsync(WorldEntry entry)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = Entries().ToList();
            if (!entries.Remove(entry)) throw new IOException("The selected entry changed. Reload the list.");
            SaveLocal(entries);
        }
        finally { Gate.Release(); }
    }

    private void SaveLocal(List<WorldEntry> entries)
    {
        if (File.Exists(LocalPath)) Atomic(LocalPath + ".bak", Read(LocalPath));
        Atomic(LocalPath, JsonSerializer.SerializeToUtf8Bytes(entries));
    }

    private static void Validate(WorldEntry e)
    {
        if (e is null || string.IsNullOrEmpty(e.Json) || string.IsNullOrEmpty(e.Kind)) throw new InvalidDataException("Incomplete world-data entry.");
        if (e.Json.Length > 32_768) throw new InvalidDataException("Entry too large.");
        var value = Js.Parse(e.Json);
        // Preserve upstream's printable-ASCII contract; conversion is never silent.
        using var doc = JsonDocument.Parse(e.Json);
        CheckText(doc.RootElement);
        if (e.Kind is "hangar_wps" or "hospitals_wps")
        {
            if (e.Removal || Js.Obj(value) is not { } obj) throw new InvalidDataException("Invalid waypoint.");
            var error = SharedData.CheckWaypoint(obj, e.Kind == "hangar_wps");
            if (error is not null) throw new InvalidDataException(error);
        }
        else
        {
            if (!DatabaseTypes.Contains(e.Kind)) throw new InvalidDataException("Unknown database type.");
            SharedData.AddDelta(new SharedData.Delta(), e.Kind,
                new() { [e.Removal ? "remove" : "add"] = new List<object> { value } }, "local", new());
        }
    }

    private static void CheckText(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String && WorldDb.Check("Text", e.GetString()).FirstOrDefault() is { } bad)
            throw new InvalidDataException(bad.Describe());
        if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) CheckText(x);
        if (e.ValueKind == JsonValueKind.Object) foreach (var x in e.EnumerateObject()) CheckText(x.Value);
    }

    public string ShareText()
    {
        var entries = Entries();
        foreach (var e in entries) Validate(e);
        var wp = new Dictionary<string, object>();
        foreach (var kind in new[] { "hangar_wps", "hospitals_wps" }) wp[kind] = entries.Where(e => e.Kind == kind).Select(e => Js.Parse(e.Json)).ToList();
        var db = new Dictionary<string, object>();
        foreach (var kind in DatabaseTypes) db[kind] = LocalDelta(entries, kind);
        return "World data contribution\n\nlocal-waypoints.json\n```json\n" + Js.Pretty(wp) + "\n```\n\nlocal-db.json\n```json\n" + Js.Pretty(db) + "\n```";
    }

    private static Dictionary<string, object> LocalDelta(IEnumerable<WorldEntry> entries, string kind) => new()
    {
        ["add"] = entries.Where(e => e.Kind == kind && !e.Removal).Select(e => Js.Parse(e.Json)).ToList(),
        ["remove"] = entries.Where(e => e.Kind == kind && e.Removal).Select(e => Js.Parse(e.Json)).ToList()
    };

    // A single checked envelope prevents mixing files from different published revisions.
    private sealed record Published(string Manifest, Dictionary<string, string> Files);
    private static string Normalize(string s) => s.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string TextHash(string text) => Hash(Encoding.UTF8.GetBytes(Normalize(text)));
    private static int ValidatePublished(Published p)
    {
        if (string.IsNullOrEmpty(p.Manifest) || p.Files is null) throw new InvalidDataException("Incomplete shared-data cache.");
        using var manifest = JsonDocument.Parse(p.Manifest);
        var m = manifest.RootElement;
        if (m.GetProperty("schema").GetInt32() != 1 || m.GetProperty("revision").GetInt32() < 1) throw new InvalidDataException("Unknown shared-data manifest.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var f in m.GetProperty("files").EnumerateObject())
        {
            if (!(f.Name == "waypoints.json" || DatabaseTypes.Any(t => f.Name == "db/" + t + ".json")) || !names.Add(f.Name)) throw new InvalidDataException("Unexpected shared-data file.");
            if (!p.Files.TryGetValue(f.Name, out var text) || !TextHash(text).Equals(f.Value.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Shared-data digest mismatch: " + f.Name);
            if (Encoding.UTF8.GetByteCount(Normalize(text)) != f.Value.GetProperty("bytes").GetInt64()) throw new InvalidDataException("Shared-data size mismatch: " + f.Name);
            using var parsed = JsonDocument.Parse(text);
            total = checked(total + Encoding.UTF8.GetByteCount(text));
            if (total > MaxBytes / 2) throw new InvalidDataException("Shared-data revision too large.");
            if (f.Name == "waypoints.json")
            {
                foreach (var kind in new[] { "hangar_wps", "hospitals_wps" })
                {
                    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in parsed.RootElement.GetProperty(kind).EnumerateArray())
                    {
                        Validate(new(kind, false, r.GetRawText(), ""));
                        if (!ids.Add(r.GetProperty("id").GetString()!)) throw new InvalidDataException("Duplicate published waypoint ID.");
                    }
                }
            }
            else
            {
                var kind = Path.GetFileNameWithoutExtension(f.Name);
                SharedData.AddDelta(new(), kind, Js.Obj(Js.Parse(text)), "published", new());
            }
        }
        if (!names.Contains("waypoints.json") || p.Files.Count != names.Count) throw new InvalidDataException("Incomplete shared-data revision.");
        return m.GetProperty("revision").GetInt32();
    }

    private Published? Cached()
    {
        if (!File.Exists(CachePath)) return null;
        var p = JsonSerializer.Deserialize<Published>(Read(CachePath)) ?? throw new InvalidDataException("Invalid cache.");
        ValidatePublished(p);
        return p;
    }

    private async Task<Published> DownloadAsync(CancellationToken token)
    {
        using var owned = _http is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) } : null;
        var client = _http ?? owned!;
        async Task<string> Get(string name)
        {
            using var response = await client.GetAsync(Repository + name, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("Shared-data download too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[32 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (bytes.Length + count > MaxBytes) throw new InvalidDataException("Shared-data download too large.");
                bytes.Write(buffer, 0, count);
            }
            return Normalize(new UTF8Encoding(false, true).GetString(bytes.ToArray()));
        }
        var manifest = await Get("manifest.json").ConfigureAwait(false);
        using var doc = JsonDocument.Parse(manifest);
        var files = new Dictionary<string, string>();
        foreach (var f in doc.RootElement.GetProperty("files").EnumerateObject())
        {
            if (!(f.Name == "waypoints.json" || DatabaseTypes.Any(t => f.Name == "db/" + t + ".json")) || files.Count >= 8) throw new InvalidDataException("Unexpected manifest path.");
            files.Add(f.Name, await Get(f.Name).ConfigureAwait(false));
        }
        var result = new Published(manifest, files);
        ValidatePublished(result);
        return result;
    }

    public async Task<WorldSyncResult> SyncAsync(string work, bool download, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Guard(work);
            if (!Directory.Exists(work) || !Path.GetFileName(Path.TrimEndingDirectorySeparator(work)).Equals("work", StringComparison.OrdinalIgnoreCase)) throw new IOException("Select an existing H145 mission work folder.");
            Published? published = null;
            var fallback = false;
            if (download)
            {
                try { published = await DownloadAsync(token).ConfigureAwait(false); }
                catch (Exception ex) when ((ex is HttpRequestException or IOException or InvalidDataException or JsonException or TaskCanceledException) && !token.IsCancellationRequested)
                { published = Cached(); if (published is null) throw; fallback = true; }
                token.ThrowIfCancellationRequested();
                if (!fallback) Atomic(CachePath, JsonSerializer.SerializeToUtf8Bytes(published));
            }
            else published = Cached();
            var revision = published is null ? 0 : ValidatePublished(published);
            var remote = published is null ? new Dictionary<string, object>() : Js.Obj(Js.Parse(published.Files["waypoints.json"]));
            var entries = Entries();
            foreach (var e in entries) Validate(e);
            var table = new Dictionary<string, object> { ["version"] = revision };
            foreach (var kind in new[] { "hangar_wps", "hospitals_wps" })
            {
                var records = entries.Where(e => e.Kind == kind).Select(e => Js.Obj(Js.Parse(e.Json))).ToList();
                var ids = records.Select(r => Js.Str(r, "id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (remote.TryGetValue(kind, out var list) && list is IList items)
                foreach (var item in items)
                {
                    var r = Js.Obj(item);
                    var error = r is null ? "Invalid published waypoint." : SharedData.CheckWaypoint(r, kind == "hangar_wps");
                    if (error is not null) throw new InvalidDataException(error);
                    if (ids.Add(Js.Str(r!, "id"))) records.Add(r!);
                }
                table[kind] = records;
            }
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(work, TableName);
            var bytes = Encoding.UTF8.GetBytes(Js.Pretty(table) + "\n");
            var counter = ReadCounter();
            if (!File.Exists(path) || !Read(path).AsSpan().SequenceEqual(bytes))
            {
                // Persist a new counter before replacing the table: an interrupted write can be retried.
                counter = checked(counter + 1);
                if (File.Exists(path)) Atomic(Path.Combine(_root, "waypoint-table.bak"), Read(path));
                Atomic(CounterPath, JsonSerializer.SerializeToUtf8Bytes(counter));
                Atomic(path, bytes);
            }
            return new(revision, counter, fallback, ((IList)table["hangar_wps"]).Count, ((IList)table["hospitals_wps"]).Count);
        }
        finally { Gate.Release(); }
    }

    private sealed record DatabaseState(string Original, string OriginalHash, string PatchedHash);
    public WorldDatabasePlan Plan(string community, bool restore)
    {
        _ensureClosed();
        Guard(community);
        if (!Directory.Exists(community)) throw new DirectoryNotFoundException(community);
        if (_root.Equals(Path.GetFullPath(community), StringComparison.OrdinalIgnoreCase) || _root.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(community)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("World-data storage must be outside the Community folder.");
        var recoveryRoot = Path.Combine(_root, "recovery");
        Guard(recoveryRoot);
        if (Directory.Exists(recoveryRoot) && Directory.EnumerateFiles(recoveryRoot, "*.json").Any())
            throw new IOException("An interrupted database operation requires recovery review: " + recoveryRoot);
        var previous = File.Exists(StatePath) ? Encoding.UTF8.GetString(Read(StatePath)) : "{}";
        var state = JsonSerializer.Deserialize<Dictionary<string, DatabaseState>>(previous)!;
        var published = restore ? null : Cached();
        var local = restore ? [] : Entries();
        foreach (var e in local) Validate(e);
        var changes = new List<WorldFileChange>();
        // Discover system folders only inside the two explicitly supported H145 packages.
        foreach (var directory in DatabaseDirectories(community))
        foreach (var kind in DatabaseTypes)
        {
            var path = Path.GetFullPath(Path.Combine(directory, kind + ".json"));
            if (!File.Exists(path)) continue;
            var current = Read(path);
            var hash = Hash(current);
            state.TryGetValue(path, out var old);
            var baseline = old is not null && old.PatchedHash == hash ? Convert.FromBase64String(old.Original) : current;
            if (old is not null && old.PatchedHash == hash && Hash(baseline) != old.OriginalHash)
                throw new InvalidDataException("The original database backup is damaged: " + path);
            if (restore)
            {
                if (old is not null && old.PatchedHash != hash) throw new IOException("Database changed outside Companion; refusing to restore: " + path);
                if (old is not null) { changes.Add(new(path, current, baseline)); state.Remove(path); }
                continue;
            }
            var delta = new SharedData.Delta();
            if (published?.Files.TryGetValue("db/" + kind + ".json", out var patch) == true) SharedData.AddDelta(delta, kind, Js.Obj(Js.Parse(patch)), "published", new());
            SharedData.AddDelta(delta, kind, LocalDelta(local, kind), "local", new());
            if (delta.Empty)
            {
                if (old is not null && hash == old.PatchedHash) changes.Add(new(path, current, baseline));
                state.Remove(path);
                continue;
            }
            var result = SharedData.ApplyDelta(SharedData.ParseRecords(baseline), delta, out _, out _);
            var after = Encoding.UTF8.GetBytes(Js.Records(result));
            if (current.AsSpan().SequenceEqual(after)) continue;
            changes.Add(new(path, current, after));
            state[path] = new(Convert.ToBase64String(baseline), Hash(baseline), Hash(after));
        }
        var stateJson = JsonSerializer.Serialize(state);
        if (Encoding.UTF8.GetByteCount(stateJson) > MaxBytes) throw new IOException("Database backup state exceeds the supported size; nothing was changed.");
        return new(Path.GetFullPath(community), changes, stateJson, previous, restore);
    }

    private static IEnumerable<string> DatabaseDirectories(string community)
    {
        foreach (var package in new[] { "hpg-airbus-h145", "hpg-airbus-h145-ap" })
        {
            var ui = Path.Combine(community, package, "html_ui"); Guard(ui);
            if (!Directory.Exists(ui)) continue;
            var count = 0;
            foreach (var system in Directory.EnumerateDirectories(ui).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (++count > 128) throw new InvalidDataException("Too many H145 system folders.");
                var db = Path.Combine(system, "db"); Guard(db);
                if (Directory.Exists(db)) yield return db;
            }
        }
    }

    public async Task ApplyAsync(WorldDatabasePlan plan, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _ensureClosed();
            // Re-plan under the gate; UI previews are not write authority for changed data.
            var fresh = Plan(plan.Community, plan.Restore);
            if (fresh.StateJson != plan.StateJson || fresh.PreviousState != plan.PreviousState || fresh.Changes.Count != plan.Changes.Count ||
                fresh.Changes.Zip(plan.Changes).Any(p => p.First.Path != p.Second.Path || !p.First.Before.AsSpan().SequenceEqual(p.Second.Before) || !p.First.After.AsSpan().SequenceEqual(p.Second.After)))
                throw new IOException("World data changed after preview. Review the operation again.");
            if (plan.Changes.Count == 0) return;
            var files = fresh.Changes.ToList();
            var layouts = files.Select(f => Directory.GetParent(Directory.GetParent(Directory.GetParent(Path.GetDirectoryName(f.Path)!)!.FullName)!.FullName)!.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(p => Path.Combine(p, "layout.json")).ToArray();
            var metadata = layouts.Concat(layouts.Select(p => Path.Combine(Path.GetDirectoryName(p)!, "manifest.json"))).ToArray();
            var layoutBefore = metadata.ToDictionary(p => p, p => File.Exists(p) ? Read(p) : null);
            foreach (var path in metadata)
            {
                Guard(path);
                if (File.Exists(path + ".rae-tmp")) throw new IOException("An existing metadata temporary file must be reviewed first: " + path);
            }
            // Durable rollback snapshots survive power loss; only touched files are included.
            var recovery = Path.Combine(_root, "recovery", Guid.NewGuid().ToString("N") + ".json");
            Atomic(recovery, JsonSerializer.SerializeToUtf8Bytes(new { fresh.Changes, Layouts = layoutBefore, fresh.PreviousState }));
            var written = new List<WorldFileChange>();
            var touchedMetadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in files)
                {
                    token.ThrowIfCancellationRequested(); _ensureClosed();
                    if (!Read(f.Path).AsSpan().SequenceEqual(f.Before)) throw new IOException("Database changed during commit: " + f.Path);
                    Atomic(f.Path, f.After);
                    written.Add(f);
                }
                foreach (var layout in layouts)
                {
                    token.ThrowIfCancellationRequested(); Guard(layout);
                    foreach (var path in new[] { layout, Path.Combine(Path.GetDirectoryName(layout)!, "manifest.json") })
                    {
                        var before = layoutBefore[path];
                        if (before is null ? File.Exists(path) : !File.Exists(path) || !Read(path).AsSpan().SequenceEqual(before))
                            throw new IOException("Package metadata changed during commit: " + path);
                        touchedMetadata.Add(path);
                    }
                    InstallerEngine.GenerateLayoutFile(layout);
                    if (!InstallerEngine.TryValidateGeneratedLayout(layout, out var reason)) throw new IOException("World-data layout validation failed: " + reason);
                }
                token.ThrowIfCancellationRequested();
                Atomic(StatePath, Encoding.UTF8.GetBytes(fresh.StateJson));
            }
            catch (Exception failure)
            {
                var errors = new List<Exception> { failure };
                foreach (var f in written) try { Atomic(f.Path, f.Before); } catch (Exception ex) { errors.Add(ex); }
                foreach (var p in touchedMetadata) try { var bytes = layoutBefore[p]; if (bytes is null) { Guard(p); File.Delete(p); } else Atomic(p, bytes); } catch (Exception ex) { errors.Add(ex); }
                try { Atomic(StatePath, Encoding.UTF8.GetBytes(fresh.PreviousState)); } catch (Exception ex) { errors.Add(ex); }
                if (errors.Count > 1) throw new AggregateException("Rollback incomplete; recovery snapshot: " + recovery, errors);
                Guard(recovery); File.Delete(recovery);
                throw;
            }
            Guard(recovery); File.Delete(recovery);
        }
        finally { Gate.Release(); }
    }

    private static byte[] Read(string path)
    {
        Guard(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxBytes) throw new InvalidDataException("World-data file exceeds the size limit.");
        using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }

    internal static void Guard(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked world-data paths are not allowed.");
        for (var d = new DirectoryInfo(full); d is not null; d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked world-data paths are not allowed.");
    }

    private static void Atomic(string path, byte[] bytes)
    {
        Guard(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            Guard(path); File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void EnsureSimulatorClosed()
    {
        foreach (var name in new[] { "FlightSimulator", "FlightSimulator2024" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) throw new IOException("Close Microsoft Flight Simulator before modifying offline databases."); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
}
