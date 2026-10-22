// HPG Mission Companion - profiles of the settings (the user's request of 2026-10-12): a default profile for the setup in one click,
// save and load of the settings to a file, and the setup of what a profile needs (Piper engine, Piper voices) from the same code
// for the window (Service tab), the command line (--oneclick) and the installer.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace HPGMissionCompanion
{
    // What a profile holds: the choices that make the voices and the levels (the same names as the settings file, so a copy of
    // HPGMissionCompanion.json is a valid profile). It never holds what belongs to this PC: folders, the mission file, the audio devices,
    // the port, the update address and time. A field that is not in the file takes the value below.
    internal sealed class ProfileData
    {
        public int Schema = 1;
        public string Name = "";
        public string Description = "";
        public string DefaultLanguage = "EN";
        public int MasterVolume = 100;
        public RadioSettings Radio = new RadioSettings();
        public AudioOutSettings AudioOut = new AudioOutSettings();       // only the levels are taken; the devices stay the ones of this PC
        public bool UseMissionVolumes = true;
        public int AttenCamInside = 9;
        public int AttenCamOutside = 12;
        public string BridgePack = "";
        public string Variety = "random";
        public string Names = "functions";
        public string CallSignMode = "airline";          // airline | custom
        public string CallSign = "";                      // the custom name (kept only in a personal file)
        public Dictionary<string, string> CrewNames = new Dictionary<string, string>();     // empty: the names of this PC are kept
        public bool StartWithSim = false;
        public bool ExitWithSim = true;
        public bool StartMinimized = true;
        public bool ReadyAnnounce = true;
        public bool AutoAudioInhibit = true;
        public bool AutoIndex = true;
        public bool AutoUpdateCheck = true;
        public bool UpdatePack = true;
        public string UpdateWhat = "both";
        public bool ShowAdvanced = false;
        public Dictionary<string, LangSetting> Languages = new Dictionary<string, LangSetting>();   // only the languages that the profile sets
    }

    // What a profile needs that this PC does not have yet.
    internal sealed class ProfileNeeds
    {
        public bool Engine;                                                  // the Piper engine is missing and a voice of the profile needs it
        public List<CatalogVoice> Voices = new List<CatalogVoice>();         // Piper voices to download
        public List<string> Unknown = new List<string>();                    // Piper voices that are not in any list: they cannot be downloaded
        public long Bytes;

        public bool Any { get { return Engine || Voices.Count > 0; } }

        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            if (Engine) sb.Append("the Piper engine (" + (PiperInstaller.EngineSize / 1048576) + " MB)");
            foreach (CatalogVoice c in Voices)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append("the voice " + c.Key + " (" + (c.OnnxSize / 1048576) + " MB)");
            }
            if (sb.Length == 0) return "nothing to download";
            return sb.ToString() + ": about " + ((Bytes + 1048575) / 1048576) + " MB in all";
        }
    }

    internal static class Profiles
    {
        public const string ResourceName = "profile.Recommended.json";

        public static string ProfilesDir { get { return Path.Combine(Ctx.BaseDir, "profiles"); } }
        public static string SettingsBackupDir { get { return Path.Combine(Path.Combine(Ctx.BaseDir, "backups"), "settings"); } }

        // ---- the profile as a file ---------------------------------------------------------------
        public static ProfileData Recommended()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (s == null) throw new InvalidOperationException("the recommended profile is not part of this program");
                using (StreamReader r = new StreamReader(s, Encoding.UTF8)) return Parse(r.ReadToEnd());
            }
        }

        public static ProfileData Parse(string json)
        {
            if (json != null && json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> raw;
            try { raw = js.DeserializeObject(json) as Dictionary<string, object>; }
            catch (Exception ex) { throw new InvalidDataException("not a settings file (" + ex.Message + ")"); }
            if (raw == null || !(raw.ContainsKey("Schema") || raw.ContainsKey("Languages") || raw.ContainsKey("Radio")))
                throw new InvalidDataException("not a settings profile of the HPG Mission Companion");
            ProfileData p = js.Deserialize<ProfileData>(json);
            if (p.Schema > 1) throw new InvalidDataException("this profile was made by a newer version of the program");
            Normalize(p);
            return p;
        }

        static void Normalize(ProfileData p)
        {
            if (p.Name == null) p.Name = "";
            if (p.Description == null) p.Description = "";
            if (LangInfo.ByCode(p.DefaultLanguage) == null) p.DefaultLanguage = "EN";
            p.MasterVolume = Math.Max(0, Math.Min(100, p.MasterVolume));
            if (p.Radio == null) p.Radio = new RadioSettings();
            if (p.AudioOut == null) p.AudioOut = new AudioOutSettings();
            p.AudioOut.Normalize();
            if (p.CrewNames == null) p.CrewNames = new Dictionary<string, string>();
            if (p.Languages == null) p.Languages = new Dictionary<string, LangSetting>();
            if (p.Variety != "coherent") p.Variety = "random";
            if (p.Names != "random") p.Names = "functions";
            if (p.UpdateWhat != "mission" && p.UpdateWhat != "app") p.UpdateWhat = "both";
            if (p.BridgePack == null) p.BridgePack = "";
            p.AttenCamInside = Math.Max(0, Math.Min(30, p.AttenCamInside));
            p.AttenCamOutside = Math.Max(0, Math.Min(30, p.AttenCamOutside));
            List<string> drop = new List<string>();
            foreach (KeyValuePair<string, LangSetting> kv in p.Languages)
                if (kv.Value == null || LangInfo.ByCode(kv.Key) == null) drop.Add(kv.Key);
            foreach (string k in drop) p.Languages.Remove(k);
            foreach (LangSetting ls in p.Languages.Values)
            {
                if (ls.Actors == null) ls.Actors = new List<ActorSetting>();
                while (ls.Actors.Count < Actors.Count) ls.Actors.Add(new ActorSetting());
                if (ls.Actors.Count > Actors.Count) ls.Actors.RemoveRange(Actors.Count, ls.Actors.Count - Actors.Count);
                foreach (ActorSetting a in ls.Actors)
                {
                    if (a == null) throw new InvalidDataException("A settings profile cannot contain a null actor entry.");
                    if (a.Speed < 0.5 || a.Speed > 3.0) a.Speed = 1.0;
                }
            }
        }

        public static string ToJson(ProfileData p)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return Indent(js.Serialize(p));
        }

        // the profile files are meant to be read and edited: one value on each line
        static string Indent(string json)
        {
            StringBuilder sb = new StringBuilder();
            int level = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length) { sb.Append(json[++i]); continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(c).Append(json[++i]); break; }
                        level++; sb.Append(c).Append("\r\n").Append(' ', level * 2); break;
                    case '}':
                    case ']': level--; sb.Append("\r\n").Append(' ', level * 2).Append(c); break;
                    case ',': sb.Append(c).Append("\r\n").Append(' ', level * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // the current settings as a profile; personal = also the crew names typed by the user
        public static ProfileData FromSettings(Settings s, string name, string description, bool personal)
        {
            ProfileData p = new ProfileData();
            p.Name = name; p.Description = description;
            p.DefaultLanguage = s.DefaultLanguage; p.MasterVolume = s.MasterVolume;
            p.Radio = new RadioSettings();
            p.Radio.NoiseLevel = s.Radio.NoiseLevel; p.Radio.Beep = s.Radio.Beep; p.Radio.BeepLevel = s.Radio.BeepLevel; p.Radio.Tail = s.Radio.Tail; p.Radio.Quality = s.Radio.Quality;
            p.AudioOut = new AudioOutSettings();       // the devices are left empty: they belong to this PC
            p.AudioOut.HeadsetLevel = s.AudioOut.HeadsetLevel; p.AudioOut.ExternalLevel = s.AudioOut.ExternalLevel; p.AudioOut.NotifyLevel = s.AudioOut.NotifyLevel;
            p.UseMissionVolumes = s.UseMissionVolumes; p.AttenCamInside = s.AttenCamInside; p.AttenCamOutside = s.AttenCamOutside;
            p.BridgePack = s.BridgePack; p.Variety = s.Variety; p.Names = s.Names;
            p.CallSignMode = s.CallSignMode; p.CallSign = personal ? s.CallSign : "";
            p.CrewNames = new Dictionary<string, string>();
            if (personal) foreach (KeyValuePair<string, string> kv in s.CrewNames) p.CrewNames[kv.Key] = kv.Value;
            p.StartWithSim = s.StartWithSim; p.ExitWithSim = s.ExitWithSim; p.StartMinimized = s.StartMinimized; p.ReadyAnnounce = s.ReadyAnnounce;
            p.AutoAudioInhibit = s.AutoAudioInhibit; p.AutoIndex = s.AutoIndex; p.AutoUpdateCheck = s.AutoUpdateCheck; p.UpdatePack = s.UpdatePack; p.UpdateWhat = s.UpdateWhat;
            p.ShowAdvanced = s.ShowAdvanced;
            p.Languages = new Dictionary<string, LangSetting>();
            foreach (KeyValuePair<string, LangSetting> kv in s.Languages)
            {
                LangSetting ls = new LangSetting();
                foreach (ActorSetting a in kv.Value.Actors) ls.Actors.Add(a.Copy());
                p.Languages[kv.Key] = ls;
            }
            Normalize(p);
            return p;
        }

        // copies the profile into the settings (not the paths, the devices, the port and the update data of this PC); returns what it did
        public static List<string> Apply(ProfileData p, Settings s)
        {
            // Validate even programmatically supplied profiles before changing live settings.
            Normalize(p);
            List<string> notes = new List<string>();
            s.DefaultLanguage = p.DefaultLanguage; s.MasterVolume = p.MasterVolume;
            s.Radio.NoiseLevel = p.Radio.NoiseLevel; s.Radio.Beep = p.Radio.Beep; s.Radio.BeepLevel = p.Radio.BeepLevel; s.Radio.Tail = p.Radio.Tail; s.Radio.Quality = p.Radio.Quality;
            s.AudioOut.HeadsetLevel = p.AudioOut.HeadsetLevel; s.AudioOut.ExternalLevel = p.AudioOut.ExternalLevel; s.AudioOut.NotifyLevel = p.AudioOut.NotifyLevel;
            s.UseMissionVolumes = p.UseMissionVolumes; s.AttenCamInside = p.AttenCamInside; s.AttenCamOutside = p.AttenCamOutside;
            s.BridgePack = p.BridgePack; s.Variety = p.Variety; s.Names = p.Names;
            s.CallSignMode = p.CallSignMode == "custom" ? "custom" : "airline";
            if (!string.IsNullOrEmpty(p.CallSign)) s.CallSign = p.CallSign;
            if (p.CrewNames.Count > 0)
            {
                s.CrewNames = new Dictionary<string, string>();
                foreach (KeyValuePair<string, string> kv in p.CrewNames) s.CrewNames[kv.Key] = kv.Value;
                notes.Add("crew names: " + p.CrewNames.Count);
            }
            s.StartWithSim = p.StartWithSim; s.ExitWithSim = p.ExitWithSim; s.StartMinimized = p.StartMinimized; s.ReadyAnnounce = p.ReadyAnnounce;
            s.AutoAudioInhibit = p.AutoAudioInhibit; s.AutoIndex = p.AutoIndex; s.AutoUpdateCheck = p.AutoUpdateCheck; s.UpdatePack = p.UpdatePack; s.UpdateWhat = p.UpdateWhat;
            s.ShowAdvanced = p.ShowAdvanced;
            foreach (KeyValuePair<string, LangSetting> kv in p.Languages)
            {
                LangSetting ls = new LangSetting();
                foreach (ActorSetting a in kv.Value.Actors) ls.Actors.Add(a.Copy());
                s.Languages[kv.Key] = ls;
                notes.Add("voices and speeds of the language " + kv.Key);
            }
            s.Normalize();
            return notes;
        }

        // ---- what the profile needs --------------------------------------------------------------
        // "piper:en_US-ryan-high" or "piper:en_GB-aru-medium:02" -> the key of the voice
        public static List<string> PiperKeys(ProfileData p)
        {
            List<string> keys = new List<string>();
            foreach (LangSetting ls in p.Languages.Values)
                foreach (ActorSetting a in ls.Actors)
                {
                    if (a == null) continue;
                    List<string> ids = new List<string>();
                    if (a.Voice != null) ids.Add(a.Voice);
                    if (a.Pool != null) ids.AddRange(a.Pool);
                    foreach (string id in ids)
                    {
                        if (id == null || !id.StartsWith("piper:", StringComparison.Ordinal)) continue;
                        string key = id.Substring(6);
                        int colon = key.IndexOf(':');
                        if (colon > 0) key = key.Substring(0, colon);
                        if (!keys.Contains(key)) keys.Add(key);
                    }
                }
            return keys;
        }

        static List<CatalogVoice> Catalog(Action<string> log)
        {
            string json = null;
            if (File.Exists(Ctx.CatalogPath)) json = File.ReadAllText(Ctx.CatalogPath, Encoding.UTF8);
            else
            {
                log("Getting the list of Piper voices");
                json = Net.GetString(PiperInstaller.CatalogUrl);
                Directory.CreateDirectory(Path.GetDirectoryName(Ctx.CatalogPath));
                File.WriteAllText(Ctx.CatalogPath, json, new UTF8Encoding(false));
            }
            return PiperInstaller.LoadCatalog(json);
        }

        public static ProfileNeeds Plan(ProfileData p, Action<string> log)
        {
            ProfileNeeds n = new ProfileNeeds();
            List<string> keys = PiperKeys(p);
            if (keys.Count == 0) return n;
            List<CatalogVoice> cat = Catalog(log);
            foreach (string key in keys)
            {
                CatalogVoice found = null;
                foreach (CatalogVoice c in cat) if (c.Key == key) found = c;
                if (found == null) foreach (CatalogVoice c in PiperInstaller.Community) if (c.Key == key) found = c;
                if (found == null) { n.Unknown.Add(key); continue; }
                if (PiperInstaller.IsInstalled(found)) continue;
                n.Voices.Add(found);
                n.Bytes += found.OnnxSize;
            }
            if (!Ctx.PiperInstalled && (n.Voices.Count > 0 || keys.Count > 0)) { n.Engine = true; n.Bytes += PiperInstaller.EngineSize; }
            return n;
        }

        // ---- the setup ---------------------------------------------------------------------------
        // report(step, done, total): the download that is running (total 0 = no size)
        public static bool Download(ProfileNeeds needs, Action<string> log, Action<string, long, long> report)
        {
            bool ok = true;
            int step = 0;
            int steps = (needs.Engine ? 1 : 0) + needs.Voices.Count;
            if (needs.Engine)
            {
                step++;
                string label = "Piper engine (" + step + " of " + steps + ")";
                log("Downloading the " + label);
                try { PiperInstaller.InstallEngine(delegate (long d, long t) { report(label, d, t); }); log("Piper engine installed"); }
                catch (Exception ex) { log("ERROR the Piper engine: " + ex.Message); ok = false; }
            }
            foreach (CatalogVoice c in needs.Voices)
            {
                step++;
                string label = "voice " + c.Key + " (" + step + " of " + steps + ")";
                log("Downloading the " + label);
                try { PiperInstaller.InstallVoice(c, delegate (long d, long t) { report(label, d, t); }); log("Voice " + c.Key + " installed"); }
                catch (Exception ex) { log("ERROR the voice " + c.Key + ": " + ex.Message); ok = false; }
            }
            return ok;
        }

        public static string BackupSettings()
        {
            if (!File.Exists(Ctx.SettingsPath)) return null;
            Directory.CreateDirectory(SettingsBackupDir);
            string dest = Path.Combine(SettingsBackupDir, "HPGMissionCompanion-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            File.Copy(Ctx.SettingsPath, dest, true);
            return dest;
        }

    }
}
