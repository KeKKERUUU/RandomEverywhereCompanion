// Headless voice runtime. The WPF companion owns navigation, consent and installation.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("CompanionRuntime.Tests")]

namespace HPGMissionCompanion
{
    internal sealed class RuntimePreferences
    {
        public string Mode = "recorded";
        public string CommunityPath = "";
        public string RecordedPack = "";
        public string RecordedDevice = "";
        public int RecordedVolume = 80;
        public bool RecordedMasterMuted;
        public List<string> RecordedMuted = new List<string>();
        public RecordedPackRegistry RecordedLibrary = new RecordedPackRegistry();
    }

    internal static class Worker
    {
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        static readonly Queue<string> logs = new Queue<string>();
        static readonly object logSync = new object();
        static RuntimePreferences preferences;
        static Server server;
        static bool running;
        static bool shutdown;
        static List<CatalogVoice> catalog;
        static StreamWriter reply;
        static DateTime lastProgress;
        static string PreferencePath { get { return Path.Combine(Ctx.BaseDir, "runtime-preferences.json"); } }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length != 3 || !args[0].StartsWith("RECompanion-", StringComparison.Ordinal)) return 2;
            int parentId;
            if (!int.TryParse(args[2], out parentId)) return 2;
            Ctx.BaseDir = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Ctx.BaseDir);
            Ctx.Log = Log;
            Ctx.Settings = Settings.Load(Ctx.SettingsPath);
            preferences = File.Exists(PreferencePath) ? json.Deserialize<RuntimePreferences>(File.ReadAllText(PreferencePath)) : new RuntimePreferences();
            preferences = preferences ?? new RuntimePreferences();
            preferences.RecordedLibrary = preferences.RecordedLibrary ?? new RecordedPackRegistry();
            preferences.RecordedLibrary.Files = preferences.RecordedLibrary.Files ?? new List<string>();
            preferences.RecordedLibrary.Hidden = preferences.RecordedLibrary.Hidden ?? new List<string>();
            preferences.RecordedMuted = preferences.RecordedMuted ?? new List<string>();
            Ctx.AudioDir = string.IsNullOrEmpty(Ctx.Settings.AudioFolder) ? Path.Combine(Ctx.BaseDir, "audio") : Ctx.Settings.AudioFolder;
            Directory.CreateDirectory(Ctx.AudioDir);
            EnsureBasePack();
            VoicePacks.Refresh();
            VoiceLibrary.Refresh();
            Ctx.Player = new Player();
            var watchdog = new Thread(delegate ()
            {
                while (!shutdown)
                {
                    try { using (var parent = Process.GetProcessById(parentId)) if (parent.HasExited) break; }
                    catch (ArgumentException) { break; }
                    Thread.Sleep(2000);
                }
                if (!shutdown) { Stop(); PiperEngine.StopAll(); Environment.Exit(0); }
            });
            watchdog.IsBackground = true; watchdog.Start();
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            try
            {
                using (var pipe = new NamedPipeServerStream(args[0], PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 8192, 8192, security))
                {
                    pipe.WaitForConnection();
                    using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8192, true))
                    using (reply = new StreamWriter(pipe, new UTF8Encoding(false), 8192, true) { AutoFlush = true })
                    {
                        while (!shutdown)
                        {
                            var line = reader.ReadLine();
                            if (line == null) break;
                            if (line.Length > 1024 * 1024) throw new InvalidDataException("Runtime command too large.");
                            try
                            {
                                var command = json.Deserialize<Dictionary<string, object>>(line);
                                var data = Dispatch(Convert.ToString(command["Command"]), command.ContainsKey("Payload") ? command["Payload"] as Dictionary<string, object> : null);
                                reply.WriteLine(json.Serialize(new { Type = "result", Success = true, Data = data }));
                            }
                            catch (Exception ex) { Log("[ERROR] " + ex); reply.WriteLine(json.Serialize(new { Type = "result", Success = false, Error = ex.Message })); }
                        }
                    }
                }
                return 0;
            }
            catch (Exception ex) { Log("[ERROR] IPC: " + ex.Message); return 1; }
            finally { shutdown = true; Stop(); PiperEngine.StopAll(); }
        }

        static void EnsureBasePack()
        {
            Directory.CreateDirectory(VoicePacks.Dir);
            var path = Path.Combine(VoicePacks.Dir, "EN.xml");
            var old = File.Exists(path) ? VoicePacks.Load(path) : null;
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("VoicePacks.EN.xml"))
            {
                var temporary = path + ".new";
                using (var output = File.Create(temporary)) resource.CopyTo(output);
                var incoming = VoicePacks.Load(temporary);
                if (old == null || !old.Usable || incoming.Version > old.Version)
                {
                    if (File.Exists(path))
                    {
                        Directory.CreateDirectory(VoicePacks.ObsoleteDir);
                        File.Copy(path, Path.Combine(VoicePacks.ObsoleteDir, "EN-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".xml"));
                        File.Delete(path);
                    }
                    File.Move(temporary, path);
                }
                else File.Delete(temporary);
            }
        }

        static string Text(Dictionary<string, object> payload, string key, string fallback)
        { return payload != null && payload.ContainsKey(key) ? Convert.ToString(payload[key]) : fallback; }
        static int Number(Dictionary<string, object> payload, string key, int fallback)
        { return payload != null && payload.ContainsKey(key) ? Convert.ToInt32(payload[key]) : fallback; }
        static bool Flag(Dictionary<string, object> payload, string key, bool fallback)
        { return payload != null && payload.ContainsKey(key) ? Convert.ToBoolean(payload[key]) : fallback; }

        static object Dispatch(string command, Dictionary<string, object> payload)
        {
            switch (command)
            {
                case "state": return Snapshot(false);
                case "scan":
                    if (running) throw new InvalidOperationException("Stop the audio session before scanning packs.");
                    preferences.CommunityPath = Text(payload, "CommunityPath", preferences.CommunityPath);
                    ReloadRecorded();
                    VoicePacks.Refresh(); Save(); return Snapshot();
                case "configure":
                    var nextMode = Text(payload, "Mode", preferences.Mode);
                    if (nextMode != "recorded" && nextMode != "synthetic") throw new InvalidDataException("Unknown audio mode.");
                    // Preview playback also belongs to the previous configuration.
                    Stop();
                    preferences.Mode = nextMode;
                    // Shared waypoint revision is independent of recorded/synthetic audio mode.
                    SimLink.Publish("L:HMC_DATA_REV", Math.Max(0, Number(payload, "WorldDataRevision", 0)));
                    preferences.RecordedVolume = Math.Max(0, Math.Min(100, Number(payload, "RecordedVolume", preferences.RecordedVolume)));
                    preferences.RecordedMasterMuted = Flag(payload, "RecordedMasterMuted", preferences.RecordedMasterMuted);
                    preferences.RecordedDevice = Text(payload, "RecordedDevice", preferences.RecordedDevice);
                    preferences.RecordedPack = Text(payload, "RecordedPack", preferences.RecordedPack);
                    if (payload.ContainsKey("RecordedMuted"))
                        preferences.RecordedMuted = RuntimePayload.Array(payload, "RecordedMuted").Select(Convert.ToString).ToList();
                    RecordedAudio.Muted.Clear();
                    foreach (var muted in preferences.RecordedMuted) RecordedAudio.Muted.Add(muted);
                    RecordedAudio.Selected = RecordedAudio.Packs.FirstOrDefault(p => p.File == preferences.RecordedPack);
                    RecordedAudio.Device = WaveOutDevices.Resolve(preferences.RecordedDevice);
                    RecordedAudio.SetVolume(preferences.RecordedVolume);
                    RecordedAudio.SetMuted(preferences.RecordedMasterMuted);
                    Ctx.Settings.MasterVolume = Number(payload, "SyntheticVolume", Ctx.Settings.MasterVolume);
                    Ctx.Settings.DefaultLanguage = Text(payload, "Language", Ctx.Settings.DefaultLanguage);
                    Ctx.Settings.BridgePack = Text(payload, "TextPack", Ctx.Settings.BridgePack);
                    Ctx.Settings.AutoAudioInhibit = Flag(payload, "AudioInhibit", Ctx.Settings.AutoAudioInhibit);
                    Ctx.Settings.AudioOut.HeadsetDevice = Text(payload, "HeadsetDevice", Ctx.Settings.AudioOut.HeadsetDevice);
                    Ctx.Settings.AudioOut.ExternalDevice = Text(payload, "ExternalDevice", Ctx.Settings.AudioOut.ExternalDevice);
                    Ctx.Settings.AudioOut.HeadsetLevel = Number(payload, "HeadsetLevel", Ctx.Settings.AudioOut.HeadsetLevel);
                    Ctx.Settings.AudioOut.ExternalLevel = Number(payload, "ExternalLevel", Ctx.Settings.AudioOut.ExternalLevel);
                    Ctx.Settings.AudioOut.NotifyLevel = Number(payload, "NotifyLevel", Ctx.Settings.AudioOut.NotifyLevel);
                    Ctx.Settings.UseMissionVolumes = Flag(payload, "MissionVolumes", Ctx.Settings.UseMissionVolumes);
                    Ctx.Settings.Radio.Quality = Text(payload, "RadioQuality", Ctx.Settings.Radio.Quality);
                    Ctx.Settings.Radio.Beep = Text(payload, "RadioBeep", Ctx.Settings.Radio.Beep);
                    Ctx.Settings.Radio.NoiseLevel = Number(payload, "RadioNoise", Ctx.Settings.Radio.NoiseLevel);
                    Ctx.Settings.Radio.BeepLevel = Number(payload, "BeepLevel", Ctx.Settings.Radio.BeepLevel);
                    Ctx.Settings.Radio.Tail = Flag(payload, "RadioTail", Ctx.Settings.Radio.Tail);
                    Ctx.Settings.Variety = Text(payload, "Variety", Ctx.Settings.Variety);
                    Ctx.Settings.Names = Text(payload, "Names", Ctx.Settings.Names);
                    Ctx.Settings.CallSignMode = Text(payload, "CallSignMode", Ctx.Settings.CallSignMode);
                    Ctx.Settings.CallSign = Text(payload, "CallSign", Ctx.Settings.CallSign);
                    Ctx.Settings.Normalize(); Save();
                    if (preferences.Mode == "synthetic") Say.ApplySettings();
                    return Snapshot();
                case "recorded-volume":
                    preferences.RecordedVolume = Math.Max(0, Math.Min(100, Number(payload, "Volume", preferences.RecordedVolume)));
                    preferences.RecordedMasterMuted = Flag(payload, "Muted", preferences.RecordedMasterMuted);
                    RecordedAudio.SetVolume(preferences.RecordedVolume);
                    RecordedAudio.SetMuted(preferences.RecordedMasterMuted);
                    SavePreferences();
                    return new { Volume = preferences.RecordedVolume, Muted = preferences.RecordedMasterMuted };
                case "recorded-register":
                    RequireRecordedLibraryIdle();
                    var paths = RuntimePayload.Array(payload, "Paths");
                    if (paths.Length == 0) throw new InvalidDataException("Select at least one recorded XML pack.");
                    preferences.RecordedLibrary.Register(paths.Select(Convert.ToString));
                    preferences.RecordedPack = Path.GetFullPath(Convert.ToString(paths[0]));
                    ReloadRecorded(); SavePreferences(); return Snapshot();
                case "recorded-forget":
                    RequireRecordedLibraryIdle();
                    var forgotten = Text(payload, "Path", "");
                    if (!RecordedAudio.Packs.Any(p => p.File.Equals(forgotten, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Select a registered recorded pack first.");
                    preferences.RecordedLibrary.Forget(forgotten);
                    if (preferences.RecordedPack.Equals(forgotten, StringComparison.OrdinalIgnoreCase)) preferences.RecordedPack = "";
                    ReloadRecorded(); SavePreferences(); return Snapshot();
                case "recorded-reload":
                    RequireRecordedLibraryIdle();
                    ReloadRecorded(); SavePreferences(); return Snapshot();
                case "recorded-preview":
                    var selectedSound = RecordedAudio.ResolvePreview(preferences.Mode, RecordedAudio.Selected, Number(payload, "Id", -1));
                    RecordedAudio.Play(selectedSound, preview: true);
                    return Snapshot();
                case "actors":
                    var language = Text(payload, "Language", Ctx.Settings.DefaultLanguage);
                    if (LangInfo.ByCode(language) == null) throw new InvalidDataException("Unsupported voice language.");
                    var actors = RuntimePayload.Array(payload, "Actors");
                    foreach (var raw in actors)
                    {
                        var row = raw as Dictionary<string, object>;
                        var actor = Number(row, "Actor", 1);
                        if (actor < 1 || actor > Actors.Count) throw new InvalidDataException("Invalid actor.");
                        var setting = Ctx.Settings.Get(language, actor);
                        setting.Voice = Text(row, "Voice", "");
                        setting.Speed = Math.Max(0.5, Math.Min(3, Convert.ToDouble(row["Speed"])));
                        setting.Random = Actors.CanDraw(actor) && Flag(row, "Random", setting.Random);
                        if (row.ContainsKey("Pool")) setting.Pool = RuntimePayload.Array(row, "Pool").Select(Convert.ToString).Distinct().ToList();
                    }
                    VoiceDraw.Reset(); Save(); return Snapshot();
                case "start": Start(); return Snapshot();
                case "stop": Stop(); return Snapshot();
                case "test":
                    if (preferences.Mode == "recorded")
                    {
                        var pack = RecordedAudio.Selected;
                        if (pack == null || pack.Mappings.Count == 0) throw new InvalidOperationException("Select a valid recorded pack first.");
                        var mapping = RecordedAudio.FindTestMapping(pack);
                        RecordedAudio.Play(mapping, preview: true);
                    }
                    else Ctx.Player.EnqueueTest(Text(payload, "Text", "<actor1>Ready for the mission."));
                    return Snapshot();
                case "import-pack":
                    if (running) throw new InvalidOperationException("Stop the session before importing a pack.");
                    var message = VoicePacks.Install(Text(payload, "Path", ""), delegate (string question) { return Flag(payload, "Replace", false); });
                    Say.Reload(); Log("[PACK] " + message); return Snapshot();
                case "catalog":
                    var body = Net.GetString(PiperInstaller.CatalogUrl);
                    Directory.CreateDirectory(Path.GetDirectoryName(Ctx.CatalogPath));
                    File.WriteAllText(Ctx.CatalogPath, body, new UTF8Encoding(false));
                    catalog = PiperInstaller.LoadCatalog(body); catalog.AddRange(PiperInstaller.Community);
                    return catalog.Select(v => new { v.Key, v.Name, v.LangCode, v.Quality, v.OnnxSize, v.Source, v.License, v.Note, Installed = PiperInstaller.IsInstalled(v) }).ToArray();
                case "install-voice":
                    var key = Text(payload, "Key", "");
                    if (catalog == null) throw new InvalidOperationException("Load the voice catalog first.");
                    var voice = catalog.FirstOrDefault(v => v.Key == key);
                    if (voice == null) throw new InvalidDataException("Unknown voice.");
                    EnsureDownloadSpace(voice.Files.Sum(file => file.Size) + (Ctx.PiperInstalled ? 0 : PiperInstaller.EngineSize));
                    if (!Ctx.PiperInstalled) PiperInstaller.InstallEngine(Progress);
                    PiperInstaller.InstallVoice(voice, Progress); VoiceLibrary.Refresh(); return Snapshot();
                case "profile-plan":
                    var profile = Profiles.Recommended();
                    var needs = Profiles.Plan(profile, Log);
                    return new { profile.Name, needs.Bytes, Description = needs.Describe(), Unknown = needs.Unknown.ToArray() };
                case "profile-install":
                    if (running) throw new InvalidOperationException("Stop the session before applying a profile.");
                    var recommended = Profiles.Recommended();
                    var plan = Profiles.Plan(recommended, Log);
                    EnsureDownloadSpace(plan.Bytes);
                    if (!Profiles.Download(plan, Log, delegate (string label, long done, long total) { Progress(done, total); })) throw new IOException("Profile downloads were incomplete. Existing settings were kept.");
                    Profiles.BackupSettings(); Profiles.Apply(recommended, Ctx.Settings); Save(); VoiceLibrary.Refresh(); Say.ApplySettings(); return Snapshot();
                case "profile-export": return Profiles.ToJson(Profiles.FromSettings(Ctx.Settings, "My profile", "", true));
                case "profile-import":
                    if (running) throw new InvalidOperationException("Stop the session before applying a profile.");
                    var imported = Profiles.Parse(Text(payload, "Json", ""));
                    Profiles.BackupSettings(); Profiles.Apply(imported, Ctx.Settings); Save(); VoiceLibrary.Refresh(); Say.ApplySettings(); return Snapshot();
                case "shutdown": shutdown = true; Stop(); return new { Stopped = true };
                default: throw new InvalidDataException("Unsupported companion command: " + command);
            }
        }

        static void RequireRecordedLibraryIdle()
        {
            if (running || preferences.Mode != "recorded")
                throw new InvalidOperationException("Select recorded mode and stop the session before changing recorded packs.");
        }

        static void ReloadRecorded()
        {
            RecordedAudio.Stop();
            RecordedAudio.Scan(preferences.CommunityPath, preferences.RecordedLibrary);
            RecordedAudio.Selected = RecordedAudio.Packs.FirstOrDefault(p => p.File.Equals(preferences.RecordedPack, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(preferences.RecordedPack))
            {
                RecordedAudio.Selected = RecordedAudio.Packs.FirstOrDefault(p => p.Mappings.Count > 0);
                preferences.RecordedPack = RecordedAudio.Selected == null ? "" : RecordedAudio.Selected.File;
            }
            RecordedAudio.SetVolume(preferences.RecordedVolume);
            RecordedAudio.SetMuted(preferences.RecordedMasterMuted);
            RecordedAudio.Muted.Clear();
            foreach (var muted in preferences.RecordedMuted) RecordedAudio.Muted.Add(muted);
        }

        static void Start()
        {
            Stop();
            Mixer.Reset();
            Say.ResetSession();
            if (SimLink.FindDll() == null) throw new FileNotFoundException("Bundled SimConnect.dll is missing. Repair the application or select an SDK override in Settings.");
            if (preferences.Mode == "recorded")
            {
                if (RecordedAudio.Selected == null || RecordedAudio.Selected.Mappings.Count == 0) throw new InvalidOperationException("No playable recorded pack is selected.");
                RecordedAudio.Device = WaveOutDevices.Resolve(preferences.RecordedDevice);
                RecordedAudio.SetVolume(preferences.RecordedVolume);
                RecordedAudio.SetMuted(preferences.RecordedMasterMuted);
                RecordedAudio.Start();
                SimLink.OnWatch = RecordedAudio.OnWatch;
                SimLink.Publish(SimLink.VarStatus, 0);
                SimLink.Publish(SimLink.VarPackVersion, 0);
                SimLink.SetWatch(RecordedAudio.Selected.Mappings.Select(m => m.Variable).Concat(new[] { Mixer.VarCrew, Mixer.VarChk, Mixer.VarCamera }).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }
            else
            {
                VoiceLibrary.Refresh();
                if (Ctx.Voices.Count == 0) throw new InvalidOperationException("No speech voice is installed.");
                Say.ApplySettings();
                AppSounds.MissionSoundRoot = Path.Combine(preferences.CommunityPath, "hpg-airbus-h145-z-andrew-mission-pack", "SimObjects", "Airplanes", "hpg-airbus-h145-ems", "Sound");
                AppSounds.EnsureAll();
                server = new Server { AllowFileMessages = false };
                try { server.Start(5997, false); }
                catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
                {
                    server.Stop(); server = null;
                    throw new InvalidOperationException("Synthetic voice port 5997 is already in use. Close the other voice server before connecting; no second audio session was started.", ex);
                }
                catch { server.Stop(); server = null; throw; }
            }
            try { SimLink.Start(delegate { return Ctx.Settings.AutoAudioInhibit; }); }
            catch { Stop(); throw; }
            running = true; Log("[SESSION] Started " + preferences.Mode + " audio.");
        }

        static void Stop()
        {
            SimLink.Stop();
            if (server != null) { server.Stop(); server = null; }
            if (Ctx.Player != null) Ctx.Player.CancelAll();
            RecordedAudio.Stop();
            running = false;
        }

        static object Snapshot(bool includeLibrary = true)
        {
            string[] history; lock (logSync) history = logs.ToArray();
            // Periodic polling must not rebuild or transfer the full pack library.
            if (!includeLibrary) return new
            {
                Running = running,
                EngineVersion = AppInfo.Version,
                MissionId = preferences != null && preferences.Mode == "synthetic" ? Say.MissionId : "",
                SimConnected = running && SimLink.Connected,
                SimStatus = running ? SimLink.Status : "Stopped",
                RecordedLastPlayed = RecordedAudio.LastPlayed,
                Logs = history
            };
            return new
            {
                Running = running,
                EngineVersion = AppInfo.Version,
                MissionId = preferences.Mode == "synthetic" ? Say.MissionId : "",
                SimConnected = running && SimLink.Connected,
                Mode = preferences.Mode,
                SimStatus = running ? SimLink.Status : "Stopped",
                SimConnectAvailable = SimLink.FindDll() != null,
                Preferences = preferences,
                Settings = Ctx.Settings,
                EffectiveTextPack = VoicePacks.Preferred(Ctx.Settings, Say.Declared)?.Name ?? "",
                PiperInstalled = Ctx.PiperInstalled,
                Devices = new[] { "" }.Concat(WaveOutDevices.Names()).ToArray(),
                Voices = Ctx.Voices.Select(v => new { v.Id, v.Display, v.Family, v.Engine }).ToArray(),
                TextPacks = VoicePacks.Packs.Select(p => new { p.Name, p.Title, p.Language, p.Version, p.Usable, Messages = p.Messages.Count, Problems = p.Problems, Check = PackCheck.Compare(p, PackCheck.Reference()).Summary() }).ToArray(),
                RecordedPacks = RecordedAudio.Packs.Select(p => new { p.Name, p.File, Mappings = p.Mappings.Count, Warnings = p.Warnings, Sounds = p.AllMappings.Select(m => new { m.Id, m.Variable, m.DeclaredFiles, Channel = m.Channel.ToString(), Variants = m.Files.Count, Files = m.Files.Select(Path.GetFileName).ToArray(), Problems = m.Problems }).ToArray() }).ToArray(),
                RecordedDiscoveryWarnings = RecordedAudio.DiscoveryWarnings,
                RecordedLastPlayed = RecordedAudio.LastPlayed,
                Actors = Actors.Names,
                Logs = history
            };
        }

        static void Save()
        {
            Ctx.Settings.Save(Ctx.SettingsPath);
            SavePreferences();
        }
        static void SavePreferences()
        {
            File.WriteAllText(PreferencePath, json.Serialize(preferences), new UTF8Encoding(false));
        }
        static void EnsureDownloadSpace(long bytes)
        {
            var drive = new DriveInfo(Path.GetPathRoot(Ctx.BaseDir));
            var required = checked(bytes * 2 + 256L * 1024 * 1024);
            if (drive.AvailableFreeSpace < required)
                throw new IOException("Insufficient free space before the voice download. Required safety budget: " + ((required + 1048575) / 1048576) + " MB.");
        }
        static void Progress(long done, long total)
        {
            if ((DateTime.UtcNow - lastProgress).TotalMilliseconds < 250 && done != total) return;
            lastProgress = DateTime.UtcNow;
            if (reply != null) reply.WriteLine(json.Serialize(new { Type = "progress", Percent = total > 0 ? 100.0 * done / total : 0, Message = (done / 1048576) + " / " + (total / 1048576) + " MB" }));
        }
        static void Log(string text)
        {
            var entry = DateTime.Now.ToString("HH:mm:ss") + " " + text;
            lock (logSync)
            {
                logs.Enqueue(entry); while (logs.Count > 150) logs.Dequeue();
                var path = Path.Combine(Ctx.BaseDir, "voice-runtime.log");
                try { if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Delete(path); File.AppendAllText(path, entry + Environment.NewLine); } catch (IOException) { }
            }
        }
    }
}
