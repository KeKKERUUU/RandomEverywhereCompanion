using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RandomEverywhereInstaller.Infrastructure.WorldData;

namespace RandomEverywhereInstaller.Tests;

internal static partial class Program
{
    private static void RejectWorld(Action action)
    {
        try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or JsonException) { return; }
        throw new InvalidOperationException("Unsafe world-data operation was accepted.");
    }

    private sealed class WorldFixture : IDisposable
    {
        public string Root { get; } = CreateTemporaryDirectory();
        public string Storage => Path.Combine(Root, "storage");
        public string Community => Path.Combine(Root, "Community");
        public string Work => Path.Combine(Root, "work");
        public string Database => Path.Combine(Community, "hpg-airbus-h145", "html_ui", "HPGH145-System", "db", "airport.json");
        public string Layout => Path.Combine(Community, "hpg-airbus-h145", "layout.json");
        public string Manifest => Path.Combine(Community, "hpg-airbus-h145", "manifest.json");
        public WorldDataService Service { get; }
        public int ClosedChecks;
        public Action? OnClosedCheck;
        public WorldFixture(HttpClient? http = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Database)!); Directory.CreateDirectory(Work);
            File.WriteAllText(Database, "[[46.9,7.5,\"Old Airport\",\"OLD\",\"OD\"]]");
            File.WriteAllText(Layout, "{\"content\":[]}"); File.WriteAllText(Manifest, "{\"total_package_size\":\"0\"}");
            Service = new(Storage, http, () => { ClosedChecks++; OnClosedCheck?.Invoke(); });
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    private static WorldEntry AirportEntry => new("airport", false, "[47.2,8.3,\"New Airport\",\"NEW\",\"NW\"]", "New Airport");
    private static WorldEntry Hangar(string id, double lat = 46.5)
    {
        var record = WorldDb.BuildWaypoint(true, id, "test", lat.ToString(System.Globalization.CultureInfo.InvariantCulture), "7.5", "520", true, false,
            [], [], false, "", "", out var error);
        True(record is not null, error ?? "Waypoint build failed");
        return new("hangar_wps", false, JsonSerializer.Serialize(record), id);
    }

    private static void WorldCoordinates()
    {
        foreach (var text in new[] { "46.909643,7.504905", "46,909643;7,504905", "N46 54.57858 E007 30.2943", "46°54'34.7148\"N 7°30'17.658\"E", "4654.57858N00730.2943E", "https://www.openstreetmap.org/#map=17/46.909643/7.504905" })
        {
            True(WorldDb.ParseCoordinates(text, out var lat, out var lon), text);
            True(Math.Abs(lat - 46.909643) < 0.00001 && Math.Abs(lon - 7.504905) < 0.00001, "Coordinate accuracy lost.");
        }
        True(!WorldDb.ParseCoordinates("146,190", out _, out _), "Out of range accepted.");
        True(!WorldDb.ParseCoordinates("46.9N 7.5N", out _, out _), "Duplicate hemispheres accepted.");
        Equal("Dubendorf ss", WorldDb.Replace("Dübendorf ß"));
        Equal(1, WorldDb.Check("name", "A😀B").Count);
        Equal(0x1F600, WorldDb.Check("name", "A😀B")[0].CodePoint);
        True(WorldDb.BuildWaypoint(true, "gap", "", "46", "7", "520", false, false, ["", "46"], ["", "7"], false, "", "", out _) is null, "Waypoint gap accepted.");
    }

    private static void WorldLocalEditing()
    {
        using var f = new WorldFixture();
        f.Service.SaveAsync(AirportEntry).GetAwaiter().GetResult();
        f.Service.SaveAsync(Hangar("Hangar")).GetAwaiter().GetResult();
        Equal(2, f.Service.Entries().Count);
        RejectWorld(() => f.Service.SaveAsync(Hangar("HANGAR")).GetAwaiter().GetResult());
        RejectWorld(() => f.Service.SaveAsync(new("../bad", false, "[]", "bad")).GetAwaiter().GetResult());
        RejectWorld(() => f.Service.SaveAsync(AirportEntry with { Json = "[99,8,\"Bad\",\"BAD\",\"BD\"]" }).GetAwaiter().GetResult());
        RejectWorld(() => f.Service.SaveAsync(AirportEntry with { Json = "[46,8,\"Dübendorf\",\"BAD\",\"BD\"]" }).GetAwaiter().GetResult());
        var replacement = AirportEntry with { Json = "[48,9,\"Edited\",\"EDIT\",\"ED\"]" };
        f.Service.SaveAsync(replacement, AirportEntry).GetAwaiter().GetResult();
        True(File.Exists(Path.Combine(f.Storage, "local.json.bak")), "Local edit backup missing.");
        f.Service.RemoveAsync(replacement).GetAwaiter().GetResult();
        Equal(1, f.Service.Entries().Count);
        var shared = f.Service.ShareText();
        True(shared.Contains("local-waypoints.json") && shared.Contains("local-db.json") && shared.Contains("hangar_wps"), "Contribution contract missing.");
        Equal(0, f.ClosedChecks);
        True(!File.Exists(Path.Combine(f.Work, WorldDataService.TableName)), "Editing wrote mission data without explicit action.");
    }

    private sealed class WorldHttp : HttpMessageHandler
    {
        public readonly Dictionary<string, string> Files = [];
        public bool Offline;
        public bool Corrupt;
        public bool Traversal;
        public int Requests;
        public WorldHttp()
        {
            Files["waypoints.json"] = "{\"hangar_wps\":[" + Hangar("shared").Json + "],\"hospitals_wps\":[]}";
            Files["db/airport.json"] = "{\"add\":[[48.5,9.5,\"Shared Airport\",\"SHAR\",\"SH\"]],\"remove\":[]}";
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            True(request.RequestUri!.AbsoluteUri.StartsWith(WorldDataService.Repository, StringComparison.Ordinal), "Unexpected source.");
            if (Offline) throw new HttpRequestException("Fixture offline");
            var name = request.RequestUri.AbsoluteUri[WorldDataService.Repository.Length..];
            var text = name == "manifest.json" ? JsonSerializer.Serialize(new
            {
                schema = 1, revision = 3,
                files = Traversal ? new Dictionary<string, object> { ["../outside.json"] = new { sha256 = "bad", bytes = 3 } } :
                    Files.ToDictionary(kv => kv.Key, kv => (object)new { sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kv.Value))), bytes = Encoding.UTF8.GetByteCount(kv.Value) })
            }) : Corrupt ? "{}" : Files[name];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
        }
    }

    private static void WorldVerifiedSync()
    {
        using var handler = new WorldHttp(); using var http = new HttpClient(handler); using var f = new WorldFixture(http);
        f.Service.SaveAsync(Hangar("shared", 47.5)).GetAwaiter().GetResult();
        var result = f.Service.SyncAsync(f.Work, true, CancellationToken.None).GetAwaiter().GetResult();
        Equal(3, result.Revision); Equal(1, result.Counter); Equal(1, result.Hangars);
        var path = Path.Combine(f.Work, WorldDataService.TableName);
        using var table = JsonDocument.Parse(File.ReadAllText(path));
        Equal(47.5, table.RootElement.GetProperty("hangar_wps")[0].GetProperty("WPspawnlat").GetDouble());
        Equal(1, f.Service.SyncAsync(f.Work, false, CancellationToken.None).GetAwaiter().GetResult().Counter);
        var bytes = File.ReadAllBytes(path);
        handler.Corrupt = true;
        True(f.Service.SyncAsync(f.Work, true, CancellationToken.None).GetAwaiter().GetResult().FromCache, "Digest failure did not use verified cache.");
        True(bytes.SequenceEqual(File.ReadAllBytes(path)), "Bad download changed table.");
        handler.Offline = true;
        True(f.Service.SyncAsync(f.Work, true, CancellationToken.None).GetAwaiter().GetResult().FromCache, "Offline cache failed.");
        var cache = Path.Combine(f.Storage, "published.json"); File.WriteAllText(cache, "{}");
        RejectWorld(() => f.Service.SyncAsync(f.Work, true, CancellationToken.None).GetAwaiter().GetResult());
        True(bytes.SequenceEqual(File.ReadAllBytes(path)), "Corrupt cache changed table.");
    }

    private static void WorldSourceGuards()
    {
        using var handler = new WorldHttp { Traversal = true }; using var http = new HttpClient(handler); using var f = new WorldFixture(http);
        RejectWorld(() => f.Service.SyncAsync(f.Work, true, CancellationToken.None).GetAwaiter().GetResult());
        Equal(1, handler.Requests);
        True(!File.Exists(Path.Combine(f.Work, WorldDataService.TableName)), "Manifest traversal wrote a table.");
        RejectWorld(() => f.Service.SyncAsync(f.Work, false, new CancellationToken(true)).GetAwaiter().GetResult());
        Equal(0, f.Service.ReadCounter());
    }

    private static void WorldDatabaseRoundtrip()
    {
        using var f = new WorldFixture(); var original = File.ReadAllBytes(f.Database);
        var unrelated = Path.Combine(f.Community, "unrelated-package"); Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "user.json"), "user data");
        f.Service.SaveAsync(AirportEntry).GetAwaiter().GetResult();
        var plan = f.Service.Plan(f.Community, false);
        Equal(1, plan.Changes.Count);
        True(original.SequenceEqual(File.ReadAllBytes(f.Database)), "Preview modified a database.");
        f.Service.ApplyAsync(plan, CancellationToken.None).GetAwaiter().GetResult();
        using (var db = JsonDocument.Parse(File.ReadAllText(f.Database))) Equal(2, db.RootElement.GetArrayLength());
        using (var layout = JsonDocument.Parse(File.ReadAllText(f.Layout)))
        {
            var item = layout.RootElement.GetProperty("content").EnumerateArray().Single(x => x.GetProperty("path").GetString()!.EndsWith("airport.json", StringComparison.Ordinal));
            Equal(new FileInfo(f.Database).Length, item.GetProperty("size").GetInt64());
        }
        Equal(0, f.Service.Plan(f.Community, false).Changes.Count);
        f.Service.ApplyAsync(f.Service.Plan(f.Community, true), CancellationToken.None).GetAwaiter().GetResult();
        True(original.SequenceEqual(File.ReadAllBytes(f.Database)), "Restore changed original bytes.");
        Equal("user data", File.ReadAllText(Path.Combine(unrelated, "user.json")));
        True(!Directory.EnumerateFiles(Path.Combine(f.Storage, "recovery")).Any(), "Successful transaction retained recovery snapshot.");
    }

    private static void WorldDatabaseConflicts()
    {
        using var f = new WorldFixture(); f.Service.SaveAsync(AirportEntry).GetAwaiter().GetResult();
        var plan = f.Service.Plan(f.Community, false);
        const string updated = "[[49,9,\"External update\",\"EXT\",\"EX\"]]";
        File.WriteAllText(f.Database, updated);
        RejectWorld(() => f.Service.ApplyAsync(plan, CancellationToken.None).GetAwaiter().GetResult());
        Equal(updated, File.ReadAllText(f.Database));
        f.Service.ApplyAsync(f.Service.Plan(f.Community, false), CancellationToken.None).GetAwaiter().GetResult();
        File.WriteAllText(f.Database, updated);
        RejectWorld(() => f.Service.Plan(f.Community, true));
        Equal(updated, File.ReadAllText(f.Database));
        f.OnClosedCheck = () => throw new IOException("Simulator is running (fixture)");
        RejectWorld(() => f.Service.Plan(f.Community, false));
    }

    private static void WorldDatabaseRollback()
    {
        using var f = new WorldFixture();
        f.Service.SaveAsync(AirportEntry).GetAwaiter().GetResult();
        var otherDb = Path.Combine(Path.GetDirectoryName(f.Database)!, "apron.json"); File.WriteAllText(otherDb, "[]");
        f.Service.SaveAsync(new("apron", false, "[47,8,\"Apron\"]", "Apron")).GetAwaiter().GetResult();
        var before = new[] { f.Database, otherDb, f.Layout, f.Manifest }.ToDictionary(x => x, File.ReadAllBytes);
        var plan = f.Service.Plan(f.Community, false);
        f.OnClosedCheck = () => { if (f.ClosedChecks == 5) throw new IOException("Injected failure before second database commit"); };
        RejectWorld(() => f.Service.ApplyAsync(plan, CancellationToken.None).GetAwaiter().GetResult());
        foreach (var (path, bytes) in before) True(bytes.SequenceEqual(File.ReadAllBytes(path)), "Rollback failed for " + path);
        True(!Directory.EnumerateFiles(Path.Combine(f.Storage, "recovery")).Any(), "Completed rollback left a blocking recovery file.");
    }

    private static void WorldLinkGuard()
    {
        using var f = new WorldFixture(); var outside = Path.Combine(f.Root, "outside.json"); File.WriteAllText(outside, "[]");
        var link = f.Storage + "-link";
        CreateTestJunction(link, f.Work);
        try { RejectWorld(() => WorldDataService.Guard(Path.Combine(link, "table.json"))); }
        finally { Directory.Delete(link); }
        Equal("[]", File.ReadAllText(outside));
    }

    private static void WorldDatabaseCancellation()
    {
        using var f = new WorldFixture(); using var cancel = new CancellationTokenSource();
        f.Service.SaveAsync(AirportEntry).GetAwaiter().GetResult();
        var original = File.ReadAllBytes(f.Database);
        var plan = f.Service.Plan(f.Community, false);
        f.OnClosedCheck = () => { if (f.ClosedChecks == 4) cancel.Cancel(); };
        RejectWorld(() => f.Service.ApplyAsync(plan, cancel.Token).GetAwaiter().GetResult());
        True(original.SequenceEqual(File.ReadAllBytes(f.Database)), "Cancellation retained the committed database.");
        f.OnClosedCheck = null;
        Equal(1, f.Service.Plan(f.Community, false).Changes.Count);
    }

    private static void WorldRemovalAndBackup()
    {
        using var f = new WorldFixture();
        f.Service.SaveAsync(new("airport", true, "{\"lat\":46.9,\"lon\":7.5,\"radius_m\":30,\"name\":\"Old Airport\"}", "Remove old airport")).GetAwaiter().GetResult();
        f.Service.ApplyAsync(f.Service.Plan(f.Community, false), CancellationToken.None).GetAwaiter().GetResult();
        using (var db = JsonDocument.Parse(File.ReadAllText(f.Database))) Equal(0, db.RootElement.GetArrayLength());
        var path = Path.Combine(f.Storage, "database-state.json");
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        state[f.Database]!["Original"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("[]"));
        File.WriteAllText(path, state.ToJsonString());
        RejectWorld(() => f.Service.Plan(f.Community, true));
        using (var db = JsonDocument.Parse(File.ReadAllText(f.Database))) Equal(0, db.RootElement.GetArrayLength());
    }
}
