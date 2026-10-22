using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using RandomEverywhereInstaller.App.Infrastructure;
using RandomEverywhereInstaller.Infrastructure.WorldData;

namespace RandomEverywhereInstaller.App.ViewModels;

public sealed record WorldKind(string Id, string Name);
public sealed class WorldField(string key, string label, string value = "", bool flag = false) : ObservableObject
{
    private string _value = value;
    private bool _checked;
    private string _label = label;
    public string Key { get; } = key;
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    public bool IsFlag { get; } = flag;
    public bool IsText => !IsFlag;
    public string Value { get => _value; set => SetProperty(ref _value, value); }
    public bool Checked { get => _checked; set => SetProperty(ref _checked, value); }
}

public sealed partial class CompanionViewModel
{
    public ObservableCollection<WorldField> WorldFields { get; } = [];
    public ObservableCollection<WorldEntry> WorldEntries { get; } = [];
    private static readonly string[] WorldWaypointKinds = ["hangar_wps", "hospitals_wps"];
    public IReadOnlyList<WorldKind> WorldKinds => WorldWaypointKinds.Concat(WorldDataService.DatabaseTypes)
        .Select(k => new WorldKind(k, T("C.World.Kind." + k))).ToArray();
    private string _worldKind = "hangar_wps";
    private bool _worldRemoval;
    private WorldEntry? _worldSelected, _worldEditing;
    private string _worldReport = "";
    private CancellationTokenSource? _worldCancellation;
    private WorldDataService WorldService => new(Path.Combine(StoragePath, "WorldData"));
    private int WorldRevisionForSession()
    {
        try { return WorldService.ReadCounter(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            // Optional world-data corruption must not stop either audio pipeline.
            WorldReport = T("C.World.CounterUnavailable");
            System.Diagnostics.Trace.TraceWarning("World-data revision unavailable: {0}", error);
            return 0;
        }
    }
    public string WorldKindId { get => _worldKind; set { if (!string.IsNullOrEmpty(value) && SetProperty(ref _worldKind, value)) { _worldRemoval = false; OnPropertiesChanged(nameof(WorldRemoval), nameof(WorldCanRemove)); NewWorldEntry(); } } }
    public bool WorldCanRemove => WorldKindId is not ("hangar_wps" or "hospitals_wps");
    public bool WorldRemoval { get => _worldRemoval; set { if (SetProperty(ref _worldRemoval, value)) NewWorldEntry(); } }
    public WorldEntry? WorldSelected { get => _worldSelected; set { if (SetProperty(ref _worldSelected, value)) RefreshCommands(); } }
    public string WorldReport { get => _worldReport; private set => SetProperty(ref _worldReport, value); }
    public ICommand OpenWorldDataCommand { get; private set; } = null!;
    public ICommand SaveWorldCommand { get; private set; } = null!;
    public ICommand NewWorldCommand { get; private set; } = null!;
    public ICommand EditWorldCommand { get; private set; } = null!;
    public ICommand RemoveWorldCommand { get; private set; } = null!;
    public ICommand SyncWorldCommand { get; private set; } = null!;
    public ICommand WriteWorldCommand { get; private set; } = null!;
    public ICommand ApplyWorldCommand { get; private set; } = null!;
    public ICommand RestoreWorldCommand { get; private set; } = null!;
    public ICommand ShareWorldCommand { get; private set; } = null!;
    public ICommand CancelWorldCommand { get; private set; } = null!;

    private void InitializeWorldData()
    {
        OpenWorldDataCommand = new AsyncRelayCommand(() => ExecuteAsync(() =>
        {
            if (!TryLeaveAudioEdits()) return Task.CompletedTask;
            EnsureWorkFolder(); ReloadWorld(); if (WorldFields.Count == 0) NewWorldEntry();
            OptionsRequested?.Invoke("WorldData"); Section = "Options";
            return Task.CompletedTask;
        }), () => !IsBusy && !IsRunning);
        SaveWorldCommand = new AsyncRelayCommand(SaveWorldAsync, () => !IsBusy && !IsRunning);
        NewWorldCommand = new RelayCommand(NewWorldEntry, () => !IsBusy);
        EditWorldCommand = new RelayCommand(EditWorldEntry, () => !IsBusy && WorldSelected is not null);
        RemoveWorldCommand = new AsyncRelayCommand(() => ExecuteAsync(async () =>
        {
            var entry = WorldSelected!;
            if (!_dialogs.Confirm(AppEdition.ProductName, T("C.World.RemoveConfirm", entry.Label))) return;
            await WorldService.RemoveAsync(entry); ReloadWorld(); NewWorldEntry();
        }), () => !IsBusy && !IsRunning && WorldSelected is not null);
        SyncWorldCommand = new AsyncRelayCommand(() => SyncWorldAsync(true), () => !IsBusy && !IsRunning && Directory.Exists(WorkPath));
        WriteWorldCommand = new AsyncRelayCommand(() => SyncWorldAsync(false), () => !IsBusy && !IsRunning && Directory.Exists(WorkPath));
        ApplyWorldCommand = new AsyncRelayCommand(() => ApplyWorldAsync(false), () => !IsBusy && !IsRunning && HasCommunity);
        RestoreWorldCommand = new AsyncRelayCommand(() => ApplyWorldAsync(true), () => !IsBusy && !IsRunning && HasCommunity);
        ShareWorldCommand = new AsyncRelayCommand(() => ExecuteAsync(() =>
        {
            if (!_dialogs.Confirm(AppEdition.ProductName, T("C.World.ShareConfirm"))) return Task.CompletedTask;
            Clipboard.SetText(WorldService.ShareText());
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(WorldDataService.IssueUrl) { UseShellExecute = true });
            WorldReport = T("C.World.Shared"); return Task.CompletedTask;
        }), () => !IsBusy && WorldEntries.Count != 0);
        CancelWorldCommand = new RelayCommand(() => _worldCancellation?.Cancel(), () => _worldCancellation is not null);
    }

    private void ReloadWorld()
    {
        WorldEntries.Clear(); foreach (var e in WorldService.Entries()) WorldEntries.Add(e);
        WorldSelected = null; RefreshCommands();
    }

    private void RelocalizeWorld()
    {
        OnPropertyChanged(nameof(WorldKinds));
        foreach (var field in WorldFields) field.Label = T("C.World.Field." + field.Key);
    }

    private void NewWorldEntry()
    {
        _worldEditing = null; WorldFields.Clear();
        void Add(string key, string value = "", bool flag = false) => WorldFields.Add(new(key, T("C.World.Field." + key), value, flag));
        Add("name"); Add("position");
        if (WorldRemoval) { Add("radius", "30"); return; }
        if (!WorldCanRemove)
        {
            Add("by");
            if (WorldKindId == "hangar_wps") Add("range", "520");
            Add("wp1"); Add("wp2"); Add("wp3"); Add("marshal");
            if (WorldKindId == "hangar_wps") { Add("boarding", flag: true); Add("deboarding", flag: true); }
        }
        else if (WorldKindId.StartsWith("airport", StringComparison.Ordinal)) { Add("icao"); Add("iata"); }
        else if (WorldKindId == "H_hospital") Add("pad");
    }

    private void EditWorldEntry()
    {
        var entry = WorldSelected!;
        WorldKindId = entry.Kind; WorldRemoval = entry.Removal; NewWorldEntry(); _worldEditing = entry;
        using var doc = JsonDocument.Parse(entry.Json);
        var r = doc.RootElement;
        void Set(string key, string value) { var f = WorldFields.FirstOrDefault(f => f.Key == key); if (f is not null) f.Value = value; }
        string Text(string key) => r.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
        string Pair(string lat, string lon) => Text(lat).Length == 0 ? "" : Text(lat) + ", " + Text(lon);
        if (r.ValueKind == JsonValueKind.Array)
        {
            Set("position", r[0] + ", " + r[1]); Set("name", r[entry.Kind == "H_hospital" ? 3 : 2].GetString() ?? "");
            if (entry.Kind == "H_hospital") Set("pad", r[2].GetString() ?? "");
            if (entry.Kind.StartsWith("airport", StringComparison.Ordinal)) { Set("icao", r[3].GetString() ?? ""); Set("iata", r[4].GetString() ?? ""); }
        }
        else if (entry.Removal) { Set("name", Text("name")); Set("position", Pair("lat", "lon")); Set("radius", Text("radius_m")); }
        else
        {
            Set("name", Text("id")); Set("by", Text("by")); Set("range", Text("max_rng_m"));
            Set("position", entry.Kind == "hangar_wps" ? Pair("WPspawnlat", "WPspawnlon") : Pair("WPdoorlat", "WPdoorlon"));
            for (var i = 1; i <= 3; i++) Set("wp" + i, Pair("WP" + i + "lat", "WP" + i + "lon"));
            if (Text("marshal_present") == "yes") Set("marshal", Pair("WPMarshalLAT", "WPMarshalLON"));
            foreach (var f in WorldFields.Where(f => f.IsFlag)) f.Checked = Text("technician_" + f.Key) == "yes";
        }
    }

    private Task SaveWorldAsync() => ExecuteAsync(async () =>
    {
        string Value(string key) => WorldFields.FirstOrDefault(f => f.Key == key)?.Value.Trim() ?? "";
        (string Lat, string Lon) Pair(string key, bool optional = false)
        {
            var value = Value(key);
            if (value.Length == 0 && optional) return ("", "");
            if (!WorldDb.ParseCoordinates(value, out var lat, out var lon)) throw new InvalidDataException(T("C.World.InvalidCoordinates", T("C.World.Field." + key)));
            return (lat.ToString("R", CultureInfo.InvariantCulture), lon.ToString("R", CultureInfo.InvariantCulture));
        }
        var textFields = WorldFields.Where(f => f.Key is "name" or "by" or "icao" or "iata" or "pad").ToArray();
        var bad = textFields.SelectMany(f => WorldDb.Check(f.Label, f.Value)).ToArray();
        if (bad.Length > 0)
        {
            var description = string.Join("\n", bad.Select(b => b.Describe()));
            if (bad.Any(b => b.Suggest.Length == 0)) throw new InvalidDataException(description);
            if (!_dialogs.Confirm(AppEdition.ProductName, T("C.World.AsciiConfirm") + "\n" + description)) return;
            foreach (var f in textFields) f.Value = WorldDb.Replace(f.Value);
        }
        var p = Pair("position"); object? record; string error;
        if (WorldRemoval) record = WorldDb.BuildRemoval(p.Lat, p.Lon, Value("radius"), Value("name"), out error);
        else if (!WorldCanRemove)
        {
            var points = Enumerable.Range(1, 3).Select(i => Pair("wp" + i, true)).ToArray(); var marshal = Pair("marshal", true);
            record = WorldDb.BuildWaypoint(WorldKindId == "hangar_wps", Value("name"), Value("by"), p.Lat, p.Lon, Value("range"),
                WorldFields.FirstOrDefault(f => f.Key == "boarding")?.Checked == true, WorldFields.FirstOrDefault(f => f.Key == "deboarding")?.Checked == true,
                points.Select(x => x.Lat).ToArray(), points.Select(x => x.Lon).ToArray(), marshal.Lat.Length > 0, marshal.Lat, marshal.Lon, out error);
        }
        else record = WorldDb.BuildDbRecord(WorldKindId, p.Lat, p.Lon, Value("name"), Value("icao"), Value("iata"), Value("pad"), out error);
        if (record is null) throw new InvalidDataException(error);
        var entry = new WorldEntry(WorldKindId, WorldRemoval, JsonSerializer.Serialize(record), Value("name") + " · " + WorldKindId + (WorldRemoval ? " −" : " +"));
        await WorldService.SaveAsync(entry, _worldEditing); ReloadWorld(); NewWorldEntry(); WorldReport = T("C.World.Saved");
    });

    private async Task SyncWorldAsync(bool download)
    {
        if (!_dialogs.Confirm(AppEdition.ProductName, T(download ? "C.World.SyncConfirm" : "C.World.WriteConfirm", WorkPath))) return;
        using var cancel = new CancellationTokenSource(); _worldCancellation = cancel; RefreshCommands();
        try { await ExecuteAsync(async () =>
        {
            var result = await Task.Run(() => WorldService.SyncAsync(WorkPath, download, cancel.Token));
            WorldReport = T(result.FromCache ? "C.World.Cached" : "C.World.Synced", result.Revision, result.Hangars, result.Hospitals);
            if (result.FromCache) StatusColor = "#E2BD7D";
        }); }
        finally { _worldCancellation = null; RefreshCommands(); }
    }

    private async Task ApplyWorldAsync(bool restore)
    {
        using var cancel = new CancellationTokenSource(); _worldCancellation = cancel; RefreshCommands();
        try { await ExecuteAsync(async () =>
        {
            var service = WorldService;
            var plan = await Task.Run(() => service.Plan(CommunityPath, restore), cancel.Token);
            WorldReport = plan.Changes.Count == 0 ? T("C.World.NoChanges") : string.Join("\n", plan.Changes.Select(f =>
            {
                using var before = JsonDocument.Parse(f.Before); using var after = JsonDocument.Parse(f.After);
                return T("C.World.FileChange", Path.GetRelativePath(CommunityPath, f.Path), before.RootElement.GetArrayLength(), after.RootElement.GetArrayLength());
            }));
            if (plan.Changes.Count == 0 || !_dialogs.Confirm(AppEdition.ProductName, T(restore ? "C.World.RestoreConfirm" : "C.World.ApplyConfirm") + "\n\n" + WorldReport)) return;
            await Task.Run(() => service.ApplyAsync(plan, cancel.Token)); WorldReport = T("C.World.Applied", plan.Changes.Count);
        }); }
        finally { _worldCancellation = null; RefreshCommands(); }
    }
}
