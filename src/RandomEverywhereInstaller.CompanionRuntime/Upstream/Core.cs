// HPG Mission Companion - core: languages, actors, settings, tags, voice library, voice resolution.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HPGMissionCompanion
{
    internal static class AppInfo
    {
        public const string Name = "HPG Mission Companion";
        public const string Version = "0.28";            // 0.1, 0.2, ... one step per delivered build
        public const string Author = "Andrea Daviero";
        public const string DiscordInvite = "https://discord.gg/eUqz49jXzT";
        public const string DiscordChannel = "https://discord.com/channels/1285277168103981197/1295684278084173885";
        public const string GitHubApp = "https://github.com/davierosoft/HPG-Mission-Companion";
        public const string GitHubMission = "https://github.com/davierosoft/HEMS-random-everywhere-mission";
        public static string Title { get { return Name + " " + Version; } }
    }

    internal sealed class RadioSettings
    {
        public int NoiseLevel = 20;        // 0-100, hiss under the speech
        public string Beep = "Beep";       // None, Click, Beep, Double beep, Chirp
        public int BeepLevel = 50;         // 0-100
        public bool Tail = true;           // squelch burst when the transmission ends
        public string Quality = "Aviation"; // Clean, Aviation, Rough
    }

    // The output of the sounds (advanced options of the Volume tab, the user's request of 2026-10-11): the device and the level of each category.
    // Headset = intercom, radio calls, external radio calls and the notifications (also with the helicopter off); external = voices heard in the air
    // (technicians, ground unit, patient, crew outside); the notifications have their own level. Default: the Windows default device and every level at 100.
    internal sealed class AudioOutSettings
    {
        public string HeadsetDevice = "";      // "" = the Windows default device; otherwise the name of the device as the list of the Volume tab shows it
        public string ExternalDevice = "";
        public int HeadsetLevel = 100;         // 0-100, multiplies the other volumes (tag, master, mission, view)
        public int ExternalLevel = 100;
        public int NotifyLevel = 100;          // the sound files (ringtones, new message tone, tablet alarm)

        public void Normalize()
        {
            if (HeadsetDevice == null) HeadsetDevice = "";
            if (ExternalDevice == null) ExternalDevice = "";
            HeadsetLevel = Math.Max(0, Math.Min(100, HeadsetLevel));
            ExternalLevel = Math.Max(0, Math.Min(100, ExternalLevel));
            NotifyLevel = Math.Max(0, Math.Min(100, NotifyLevel));
        }
    }

    // The sound rules of the mission communications (the user's rules of 2026-10-11); every number is here and nowhere else.
    // The intercom (ICS) has the hiss chosen by the user; a radio call has more, an external radio call (dispatcher, hospital) more again.
    internal static class RadioRules
    {
        public const int RadioHissExtra = 10;           // radio call: the hiss of the user + 10
        public const int ExternalHissExtra = 20;        // external radio call: the hiss of the user + 20
        public const string IcsBeep = "Click";          // the intercom has a click, no beep
        public const string ExternalBeep = "Double beep";
        public const double FarVolume = 0.1;            // -20 dB: a voice heard from inside that comes from 30 to 100 m from the helicopter
        public const int ZoneNear = 1;                  // L:HMC_ZONE_*: up to 5 m from the helicopter (next to it)
        public const int ZoneVoice = 2;                 // up to 30 m: heard by voice at the normal level
        public const int ZoneFar = 3;                   // up to 100 m: heard very low
        public const int ZoneOut = 4;                   // beyond 100 m: not heard from inside
    }

    internal sealed class LangInfo
    {
        public string Code;
        public string Name;
        public string Family;
        public string Sample;

        public LangInfo(string code, string name, string family, string sample)
        {
            Code = code; Name = name; Family = family; Sample = sample;
        }

        public override string ToString() { return Name; }

        public static readonly LangInfo[] All = new LangInfo[]
        {
            new LangInfo("EN", "English", "en", "Hello, this is {0}. Ready for the mission."),
            new LangInfo("FR", "Francais", "fr", "Bonjour, ici {0}. Pret pour la mission."),
            new LangInfo("IT", "Italiano", "it", "Ciao, sono {0}. Pronto per la missione."),
            new LangInfo("DE", "Deutsch", "de", "Hallo, hier ist {0}. Bereit fuer die Mission.")
        };

        public static LangInfo ByCode(string code)
        {
            if (code == null) return null;
            foreach (LangInfo l in All) if (string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) return l;
            return null;
        }

        // "en-US", "en_GB", "EN" -> "en"
        public static string FamilyOf(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "";
            int i = 0;
            while (i < tag.Length && char.IsLetter(tag[i])) i++;
            return tag.Substring(0, i).ToLowerInvariant();
        }

        // language tag of a mission text ("EN", "us", "gb") -> language code, or the upper-case text when it is not supported
        public static string Normalize(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string t = tag.Trim().ToUpperInvariant();
            if (t == "US" || t == "GB" || t == "UK" || t == "EN" || t == "AU") return "EN";
            return t;
        }
    }

    internal static class Actors
    {
        public const int Count = 12;
        public const int Checklist = 11;        // the voice of the checklists follows L:VOLUME_CHK; actor 12 (the heli rescuers and their base) came later and follows the crew volume
        public const int HeliRescuer = 12;
        // the actors whose voice can be drawn at random (the user's request of 2026-10-16): dispatcher, ground unit and hospital staff, heli rescuer
        public static bool CanDraw(int actor) { return actor == 8 || actor == 9 || actor == 12; }
        public static readonly string[] Names = new string[]
        {
            "Pilot", "Copilot", "Doctor", "HEMS1", "HEMS2",
            "Technician male", "Technician female", "Dispatcher", "Ground unit", "Patient / extra", "Checklist", "Heli rescuer"
        };
    }

    internal sealed class ActorSetting
    {
        public string Voice;
        public double Speed = 1.0;
        public bool Random;                 // the voice is drawn (VoiceDraw): only for the actors in Actors.CanDraw
        public List<string> Pool;           // the voices the draw may use (ids as in Voice); null or empty = every voice of the language

        public ActorSetting Copy()
        {
            ActorSetting c = new ActorSetting();
            c.Voice = Voice; c.Speed = Speed; c.Random = Random;
            c.Pool = Pool == null ? null : new List<string>(Pool);
            return c;
        }
    }

    internal sealed class LangSetting
    {
        public List<ActorSetting> Actors = new List<ActorSetting>();
    }

    internal sealed class Settings
    {
        public string DefaultLanguage = "EN";
        public int Port = 5997;
        public bool ListenAll = false;          // false: the service listens on 127.0.0.1 only (the mission connects to localhost, and Windows Firewall never asks); true: on every network interface
        public string AudioFolder = "";
        public bool StartWithSim = false;
        public bool ExitWithSim = true;
        public bool StartMinimized = true;      // with the simulator: hidden in the notification area
        public int MasterVolume = 100;          // 0-100, applies to every spoken text
        public RadioSettings Radio = new RadioSettings();
        public AudioOutSettings AudioOut = new AudioOutSettings();     // device and level for the headset, the external voices and the notifications (advanced options of the Volume tab)
        public string WorkFolder = "";
        public string IndexTool = "";
        public bool AutoIndex = true;           // run the mission index update when the app starts
        public string UpdateWhat = "both";      // what the Updates tab installs: both | mission | app (the user's request of 2026-10-11)
        public bool UpdatePack = true;          // the Updates tab also installs a newer voice pack EN (the XML only, published apart from the program) when the program itself is not updated
        public bool ReadyAnnounce = true;       // started minimized with the simulator running: the pilot says that the program is ready
        public bool AutoUpdateCheck = true;     // check for updates when the program starts (the user's request of 2026-10-11): only reads the published versions and tells; nothing is downloaded without the question
        public string LastUpdateCheck = "";     // UTC time of the last check (ISO), for information: the automatic check runs at every start
        public string MissionFile = "";         // the installed R&E mission file to update ("" = the one found first)
        public bool AutoAudioInhibit = true;    // keep L:AUDIOINHIBIT at 1 in the simulator (SimConnect, checked every 5 s)
        public bool ShowAdvanced = false;       // the "Show advanced options" box of the window: false hides the advanced groups (the user's request of 2026-10-11)
        public string BridgePack = "";          // active pack used by the mission: the pack whose texts are spoken when the mission lines (the LVAR counters, voice code APP) rise; "" = the first pack
        public string Variety = "random";       // random | coherent: how the text option of a message is chosen
        public string Names = "functions";      // functions | random: names of the crew in the texts (functions: the job roles, or the names typed in the Actors tab)
        public string CallSignMode = "airline"; // airline: the atc_airline of the aircraft.cfg of the livery (Pelikan 3) | custom: the name typed in CallSign
        public string CallSign = "";            // the custom call sign of the helicopter in the texts ({@HEMS}); the mode custom with an empty name says HEMS
        public Dictionary<string, string> CrewNames = new Dictionary<string, string>();   // role (PLT, CPL, PAX3, PAX1, PAX2, TECHM, TECHF) -> name typed by the user; used when Names is "functions"
        public bool UseMissionVolumes = true;   // follow L:VOLUME_CREW and L:VOLUME_CHK of the mission
        public int AttenCamInside = 9;          // dB less for a voice from outside heard while the view is inside the helicopter (0 = off)
        public int AttenCamOutside = 12;        // dB less for a voice from inside heard while the view is outside (0 = off)
        public Dictionary<string, LangSetting> Languages = new Dictionary<string, LangSetting>();

        public static Settings Load(string path)
        {
            Settings s = null;
            try
            {
                if (File.Exists(path))
                {
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    js.MaxJsonLength = int.MaxValue;
                    s = js.Deserialize<Settings>(File.ReadAllText(path, Encoding.UTF8));
                }
            }
            catch (Exception) { s = null; }
            if (s == null) s = new Settings();
            s.Normalize();
            return s;
        }

        public void Save(string path)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            File.WriteAllText(path, js.Serialize(this), new UTF8Encoding(false));
        }

        public void Normalize()
        {
            if (Languages == null) Languages = new Dictionary<string, LangSetting>();
            if (LangInfo.ByCode(DefaultLanguage) == null) DefaultLanguage = "EN";
            if (Port < 1 || Port > 65535) Port = 5997;
            if (AudioFolder == null) AudioFolder = "";
            if (WorkFolder == null) WorkFolder = "";
            if (IndexTool == null) IndexTool = "";
            if (MissionFile == null) MissionFile = "";
            if (UpdateWhat != "mission" && UpdateWhat != "app") UpdateWhat = "both";
            MasterVolume = Math.Max(0, Math.Min(100, MasterVolume));
            if (AudioOut == null) AudioOut = new AudioOutSettings();
            AudioOut.Normalize();
            if (Radio == null) Radio = new RadioSettings();
            Radio.NoiseLevel = Math.Max(0, Math.Min(100, Radio.NoiseLevel));
            Radio.BeepLevel = Math.Max(0, Math.Min(100, Radio.BeepLevel));
            if (string.IsNullOrEmpty(Radio.Beep)) Radio.Beep = "Beep";
            if (string.IsNullOrEmpty(Radio.Quality)) Radio.Quality = "Aviation";
            if (BridgePack == null) BridgePack = "";
            if (Variety != "coherent") Variety = "random";
            if (Names != "random") Names = "functions";
            if (CallSignMode != "custom") CallSignMode = "airline";
            if (CallSign == null) CallSign = "";
            if (CrewNames == null) CrewNames = new Dictionary<string, string>();
            AttenCamInside = Math.Max(0, Math.Min(30, AttenCamInside));
            AttenCamOutside = Math.Max(0, Math.Min(30, AttenCamOutside));
            foreach (LangInfo l in LangInfo.All)
            {
                LangSetting ls;
                if (!Languages.TryGetValue(l.Code, out ls) || ls == null) { ls = new LangSetting(); Languages[l.Code] = ls; }
                if (ls.Actors == null) ls.Actors = new List<ActorSetting>();
                while (ls.Actors.Count < Actors.Count) ls.Actors.Add(new ActorSetting());
                // Recover persisted null entries without shifting the actor/channel assignments.
                for (int ai = 0; ai < ls.Actors.Count; ai++)
                    if (ls.Actors[ai] == null) ls.Actors[ai] = new ActorSetting();
                foreach (ActorSetting a in ls.Actors)
                {
                    if (a.Speed < 0.5 || a.Speed > 3.0) a.Speed = 1.0;
                    if (a.Pool != null && a.Pool.Count == 0) a.Pool = null;
                }
                for (int ai = 0; ai < ls.Actors.Count; ai++) if (!Actors.CanDraw(ai + 1)) ls.Actors[ai].Random = false;
            }
        }

        // actor is 1..10
        public ActorSetting Get(string lang, int actor)
        {
            LangSetting ls;
            if (!Languages.TryGetValue(lang, out ls)) { Normalize(); ls = Languages[lang]; }
            return ls.Actors[Math.Max(1, Math.Min(Actors.Count, actor)) - 1];
        }
    }

    // ---- tags in the mission text -------------------------------------------------------------
    // <actor3>  actor 1..11         <Microsoft David> / <ryan>  voice by name        <speed:1.2>  speed
    // <vol:70>  volume 0-100 of this text (the mission volume_crew can be passed with the speak params)   <radio>  radio effect (<radio:off> ends it)
    // <EN> <FR> <IT> <DE>  language (also <language:us>). Square brackets work like the angle brackets: [actor3] [speed:1.2].
    // The tags apply to the text that follows them, until the next tag of the same kind.
    internal sealed class Segment
    {
        public string Text;
        public int Actor;          // 0 = none
        public string Voice;       // null = none
        public string Lang;        // null = none
        public double Speed;       // 0 = none
        public double Volume = -1;  // 0-100, -1 = none
        public int Radio = -1;      // 1 on, 0 off, -1 none
        public string Beep;        // beep type of the radio effect (None, Click, Beep, Double beep, Chirp), null = the type chosen in the program
        public string Fx;          // with Radio = 1: null = radio call, "ics" = intercom, "ext" = external radio call
        public bool Far;           // <zone:far>: a voice heard from inside that comes from 30 to 100 m away (RadioRules.FarVolume)
    }

    internal static class TagParser
    {
        static readonly Regex TagRx = new Regex("[<\\[]([^<>\\[\\]]{1,60})[>\\]]", RegexOptions.Compiled);
        static readonly Regex ActorRx = new Regex("^actor\\s*(\\d{1,2})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex SpeedRx = new Regex("^(?:speed|rate)\\s*[:=]\\s*([0-9]+(?:[.,][0-9]+)?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex LangRx = new Regex("^(?:lang|language)\\s*[:=]\\s*([A-Za-z]{2})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex VolRx = new Regex("^(?:vol|volume)\\s*[:=]\\s*([0-9]+(?:[.,][0-9]+)?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex RadioRx = new Regex("^radio(?:\\s*[:=]\\s*(on|off|0|1))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex IcsRx = new Regex("^ics(?:\\s*[:=]\\s*(on|off|0|1))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex RadioExtRx = new Regex("^radio\\s*[:=]\\s*ext$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ZoneRx = new Regex("^zone\\s*[:=]\\s*(far|near)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex RadioBeepRx = new Regex("^radio\\s*[:=]\\s*([A-Za-z0-9 ]+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex BeepRx = new Regex("^beep\\s*[:=]\\s*([A-Za-z0-9 ]+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex VoiceRx = new Regex("^voice\\s*[:=]\\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex TwoLettersRx = new Regex("^[A-Za-z]{2}$", RegexOptions.Compiled);
        static readonly Regex HtmlRx = new Regex("^/?(?:br|b|i|u|p|em|strong|span|font)\\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<Segment> Parse(string input, List<string> notes)
        {
            List<Segment> result = new List<Segment>();
            if (input == null) return result;
            int actor = 0; string voice = null; string lang = null; double speed = 0; double vol = -1; int radio = -1; string beep = null; string fx = null; bool far = false;
            int pos = 0;
            foreach (Match m in TagRx.Matches(input))
            {
                Add(result, input.Substring(pos, m.Index - pos), actor, voice, lang, speed, vol, radio, beep, fx, far);
                pos = m.Index + m.Length;
                string tag = m.Groups[1].Value.Trim();
                Match g;
                if (HtmlRx.IsMatch(tag)) continue;
                if ((g = ActorRx.Match(tag)).Success)
                {
                    int n = int.Parse(g.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (n >= 1 && n <= Actors.Count) actor = n;
                    else if (notes != null) notes.Add("actor " + n + " does not exist (1-" + Actors.Count + ")");
                }
                else if ((g = SpeedRx.Match(tag)).Success)
                {
                    double v;
                    if (double.TryParse(g.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                        speed = Math.Max(0.5, Math.Min(3.0, v));
                }
                else if ((g = VolRx.Match(tag)).Success)
                {
                    double v;
                    if (double.TryParse(g.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                        vol = Math.Max(0, Math.Min(100, v));
                }
                else if ((g = RadioRx.Match(tag)).Success)
                {
                    string r = g.Groups[1].Success ? g.Groups[1].Value.ToLowerInvariant() : "on";
                    radio = (r == "off" || r == "0") ? 0 : 1;
                    fx = null;
                }
                else if ((g = IcsRx.Match(tag)).Success)
                {
                    string r = g.Groups[1].Success ? g.Groups[1].Value.ToLowerInvariant() : "on";     // <ics> = the intercom of the helicopter: a click, the hiss of the user
                    radio = (r == "off" || r == "0") ? 0 : 1;
                    fx = radio == 1 ? "ics" : null;
                }
                else if (RadioExtRx.IsMatch(tag))
                {
                    radio = 1;                                   // <radio:ext> = a call on an external radio: double beep, more hiss
                    fx = "ext";
                }
                else if ((g = ZoneRx.Match(tag)).Success) far = g.Groups[1].Value.ToLowerInvariant() == "far";
                else if ((g = RadioBeepRx.Match(tag)).Success && AudioFx.NormalizeBeep(g.Groups[1].Value) != null)
                {
                    radio = 1;                                   // <radio:chirp> = radio effect with that beep
                    fx = null;
                    beep = AudioFx.NormalizeBeep(g.Groups[1].Value);
                }
                else if ((g = BeepRx.Match(tag)).Success)
                {
                    string b = AudioFx.NormalizeBeep(g.Groups[1].Value);
                    if (b != null) beep = b;
                    else if (notes != null) notes.Add("beep type " + g.Groups[1].Value.Trim() + " does not exist (none, click, beep, double, chirp)");
                }
                else if ((g = LangRx.Match(tag)).Success) lang = LangInfo.Normalize(g.Groups[1].Value);
                else if (TwoLettersRx.IsMatch(tag)) lang = LangInfo.Normalize(tag);
                else if ((g = VoiceRx.Match(tag)).Success) voice = g.Groups[1].Value.Trim();
                else voice = tag;
            }
            Add(result, input.Substring(pos), actor, voice, lang, speed, vol, radio, beep, fx, far);
            return result;
        }

        static void Add(List<Segment> list, string text, int actor, string voice, string lang, double speed, double vol, int radio, string beep, string fx, bool far)
        {
            string t = Regex.Replace(text, "\\s+", " ").Trim();
            if (t.Length == 0) return;
            Segment s = new Segment();
            s.Text = t; s.Actor = actor; s.Voice = voice; s.Lang = lang; s.Speed = speed; s.Volume = vol; s.Radio = radio; s.Beep = beep; s.Fx = radio == 1 ? fx : null; s.Far = far;
            list.Add(s);
        }
    }

    // ---- voices ----------------------------------------------------------------------------------
    internal sealed class VoiceInfo
    {
        public string Id;           // "win:<id>" or "piper:<key>[:<speaker>]"
        public string Display;      // text shown in the lists
        public string Engine;       // "windows" or "piper"
        public string Family;       // "en", "fr", "it", "de", ...
        public string[] Keys;       // names a mission tag can use
        public int Rank;            // quality, higher is better
        public string WinId;
        public string ModelPath;
        public string ConfigPath;
        public int Speaker = -1;
        public double LengthScale = 1.0;
        public override string ToString() { return Display; }
    }

    internal static class Ctx
    {
        public static string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        public static string AudioDir;
        public static Settings Settings = new Settings();
        public static volatile List<VoiceInfo> Voices = new List<VoiceInfo>();
        public static Action<string> Log = delegate (string s) { };
        public static bool Silent;
        public static Player Player;

        public static string SettingsPath { get { return Path.Combine(BaseDir, "voice-settings.json"); } }
        public static string PiperDir { get { return Path.Combine(BaseDir, "piper"); } }
        public static string PiperExe { get { return Path.Combine(PiperDir, "piper.exe"); } }
        public static string PiperVoicesDir { get { return Path.Combine(Path.Combine(BaseDir, "voices"), "piper"); } }
        public static string CatalogPath { get { return Path.Combine(Path.Combine(BaseDir, "voices"), "piper-catalog.json"); } }
        public static bool PiperInstalled { get { return File.Exists(PiperExe); } }
    }

    internal static class VoiceLibrary
    {
        // Rebuilds Ctx.Voices: the Windows voices and the installed Piper voices (if the Piper engine is there).
        public static void Refresh()
        {
            List<VoiceInfo> list = new List<VoiceInfo>();
            try
            {
                foreach (dynamic v in WinRt.AllVoices())
                {
                    VoiceInfo e = new VoiceInfo();
                    string name = (string)v.DisplayName;
                    string lang = (string)v.Language;
                    string id = (string)v.Id;
                    string gender = v.Gender.ToString();
                    e.WinId = id;
                    e.Id = "win:" + id;
                    e.Engine = "windows";
                    e.Family = LangInfo.FamilyOf(lang);
                    e.Display = name + " - " + gender.ToLowerInvariant() + " (Windows, " + lang + ")";
                    e.Keys = new string[] { name, id.Substring(id.LastIndexOf('\\') + 1) };
                    e.Rank = 5;
                    list.Add(e);
                }
            }
            catch (Exception ex) { Ctx.Log("[ERROR] Windows voices: " + ex.Message); }
            if (Ctx.PiperInstalled) ScanPiper(list);
            list.Sort(delegate (VoiceInfo a, VoiceInfo b)
            {
                int c = string.Compare(a.Family, b.Family, StringComparison.Ordinal);
                if (c != 0) return c;
                c = string.Compare(a.Engine, b.Engine, StringComparison.Ordinal);
                if (c != 0) return c;
                return string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase);
            });
            Ctx.Voices = list;
        }

        static void ScanPiper(List<VoiceInfo> list)
        {
            string dir = Ctx.PiperVoicesDir;
            if (!Directory.Exists(dir)) return;
            foreach (string model in Directory.GetFiles(dir, "*.onnx"))
            {
                string config = model + ".json";
                if (!File.Exists(config)) continue;
                try
                {
                    string key = Path.GetFileNameWithoutExtension(model);   // en_US-ryan-medium
                    string[] parts = key.Split('-');
                    string langCode = parts[0];
                    string name = parts.Length > 1 ? parts[1] : key;
                    string quality = parts.Length > 2 ? parts[2] : "medium";
                    double length = 1.0;
                    Dictionary<string, object> speakers = null;
                    int numSpeakers = 1;
                    Dictionary<string, object> cfg = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(config, Encoding.UTF8)) as Dictionary<string, object>;
                    if (cfg != null)
                    {
                        object inf;
                        if (cfg.TryGetValue("inference", out inf))
                        {
                            Dictionary<string, object> im = inf as Dictionary<string, object>;
                            object ls;
                            if (im != null && im.TryGetValue("length_scale", out ls)) length = Convert.ToDouble(ls, CultureInfo.InvariantCulture);
                        }
                        object ns;
                        if (cfg.TryGetValue("num_speakers", out ns)) numSpeakers = Convert.ToInt32(ns, CultureInfo.InvariantCulture);
                        object sm;
                        if (cfg.TryGetValue("speaker_id_map", out sm)) speakers = sm as Dictionary<string, object>;
                    }
                    int rank = QualityRank(quality);
                    string family = LangInfo.FamilyOf(langCode);
                    string hint = GenderHint(name);
                    if (numSpeakers > 1 && speakers != null && speakers.Count > 0 && speakers.Count <= 40)
                    {
                        foreach (KeyValuePair<string, object> kv in speakers)
                        {
                            VoiceInfo e = NewPiper(key, model, config, length, family, rank);
                            e.Id = "piper:" + key + ":" + kv.Key;
                            e.Speaker = Convert.ToInt32(kv.Value, CultureInfo.InvariantCulture);
                            e.Display = name + " " + kv.Key + " - " + quality + " (Piper, " + langCode + ")";
                            e.Keys = new string[] { key + ":" + kv.Key, name + ":" + kv.Key, name + " " + kv.Key };
                            list.Add(e);
                        }
                    }
                    else if (numSpeakers <= 1)
                    {
                        VoiceInfo e = NewPiper(key, model, config, length, family, rank);
                        e.Id = "piper:" + key;
                        e.Display = name + " - " + quality + (hint.Length > 0 ? " - " + hint : "") + " (Piper, " + langCode + ")";
                        e.Keys = new string[] { key, name + "-" + quality, name };
                        list.Add(e);
                    }
                }
                catch (Exception ex) { Ctx.Log("[ERROR] Piper voice " + Path.GetFileName(model) + ": " + ex.Message); }
            }
        }

        static VoiceInfo NewPiper(string key, string model, string config, double length, string family, int rank)
        {
            VoiceInfo e = new VoiceInfo();
            e.Engine = "piper"; e.Family = family; e.ModelPath = model; e.ConfigPath = config;
            e.LengthScale = length <= 0 ? 1.0 : length; e.Rank = rank;
            return e;
        }

        public static int QualityRank(string q)
        {
            switch ((q ?? "").ToLowerInvariant())
            {
                case "high": return 4;
                case "medium": return 3;
                case "low": return 2;
                case "x_low": return 1;
            }
            return 3;
        }

        // Only names whose gender is certain; the Piper catalog does not say.
        static readonly string[] Male = { "ryan", "joe", "john", "danny", "bryce", "kusal", "norman", "mike", "reza_ibrahim", "hfc_male", "alan", "northern_english_male", "tom", "gilles", "riccardo", "leonardo", "miro", "thorsten", "karlsson", "pavoque" };
        static readonly string[] Female = { "amy", "kathleen", "kristin", "lessac", "ljspeech", "hfc_female", "alba", "cori", "jenny_dioco", "southern_english_female", "siwis", "paola", "serena", "eva_k", "kerstin", "ramona" };

        public static string GenderHint(string name)
        {
            foreach (string m in Male) if (m == name) return "male";
            foreach (string f in Female) if (f == name) return "female";
            return "";
        }

        public static VoiceInfo ById(List<VoiceInfo> voices, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (VoiceInfo v in voices) if (v.Id == id) return v;
            return null;
        }

        // A name from a mission tag: exact name first, then part of a name; the language of the text wins, then the best quality.
        public static VoiceInfo Find(List<VoiceInfo> voices, string key, string family)
        {
            if (string.IsNullOrEmpty(key)) return null;
            VoiceInfo best = null;
            for (int pass = 0; pass < 2 && best == null; pass++)
            {
                foreach (VoiceInfo v in voices)
                {
                    bool hit = false;
                    foreach (string k in v.Keys)
                    {
                        if (pass == 0 ? string.Equals(k, key, StringComparison.OrdinalIgnoreCase) : k.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                    }
                    if (!hit) continue;
                    if (best == null || Better(v, best, family)) best = v;
                }
            }
            return best;
        }

        static bool Better(VoiceInfo a, VoiceInfo b, string family)
        {
            bool fa = a.Family == family, fb = b.Family == family;
            if (fa != fb) return fa;
            return a.Rank > b.Rank;
        }

        public static List<VoiceInfo> OfFamily(List<VoiceInfo> voices, string family)
        {
            List<VoiceInfo> r = new List<VoiceInfo>();
            foreach (VoiceInfo v in voices) if (v.Family == family) r.Add(v);
            return r;
        }
    }

    // The voices drawn at random (the user's request of 2026-10-16). An actor with Random takes one voice of its pool (every voice of the language when the pool
    // is empty) and keeps it until the mission asks for a new draw (<voices:shift> the dispatcher, <voices:dispatch> the ground unit and the heli rescuer).
    // The voices of the actors that are drawn are never the same, and they avoid the voices that the other actors have chosen; only when there are not enough
    // voices the rule is relaxed (first the voices of the chosen actors, then the draws of the others).
    internal static class VoiceDraw
    {
        static readonly object sync = new object();
        static readonly Dictionary<string, string> current = new Dictionary<string, string>();       // "EN|8" -> the voice id
        static readonly Dictionary<string, string> previous = new Dictionary<string, string>();      // the voice before the last redraw: a new draw avoids it when it can
        static readonly Random rng = new Random();

        static string Key(string lang, int actor) { return lang + "|" + actor; }

        // a new draw for these actors at the next use (all the languages)
        public static void Redraw(params int[] actors)
        {
            lock (sync)
            {
                List<string> drop = new List<string>();
                foreach (string k in current.Keys)
                {
                    int bar = k.IndexOf('|');
                    int a;
                    if (bar > 0 && int.TryParse(k.Substring(bar + 1), out a) && Array.IndexOf(actors, a) >= 0) drop.Add(k);
                }
                foreach (string k in drop) { previous[k] = current[k]; current.Remove(k); }
            }
        }

        public static void Reset() { lock (sync) { current.Clear(); previous.Clear(); } }

        public static string Current(string lang, int actor)
        {
            lock (sync) { string v; return current.TryGetValue(Key(lang, actor), out v) ? v : null; }
        }

        public static string Pick(Settings s, LangInfo li, int actor, List<VoiceInfo> voices)
        {
            lock (sync)
            {
                ActorSetting me = s.Get(li.Code, actor);
                List<VoiceInfo> fam = VoiceLibrary.OfFamily(voices, li.Family);
                string have;
                if (current.TryGetValue(Key(li.Code, actor), out have) && VoiceLibrary.ById(fam, have) != null && InPool(me, have)) return have;
                List<VoiceInfo> cand = new List<VoiceInfo>();
                foreach (VoiceInfo v in fam) if (InPool(me, v.Id)) cand.Add(v);
                if (cand.Count == 0) cand = fam;                       // the pool has no installed voice: every voice of the language
                if (cand.Count == 0) return null;
                List<string> usedFixed = new List<string>(), usedDrawn = new List<string>();
                for (int a = 1; a <= Actors.Count; a++)
                {
                    if (a == actor) continue;
                    ActorSetting o = s.Get(li.Code, a);
                    if (o.Random)
                    {
                        string d;
                        if (current.TryGetValue(Key(li.Code, a), out d)) usedDrawn.Add(d);
                    }
                    else if (!string.IsNullOrEmpty(o.Voice)) usedFixed.Add(o.Voice);
                }
                List<VoiceInfo> best = Without(cand, usedFixed, usedDrawn);
                if (best.Count == 0) best = Without(cand, null, usedDrawn);
                if (best.Count == 0) best = cand;
                string before;
                if (previous.TryGetValue(Key(li.Code, actor), out before) && best.Count > 1)
                {
                    List<VoiceInfo> other = new List<VoiceInfo>();
                    foreach (VoiceInfo v in best) if (v.Id != before) other.Add(v);
                    if (other.Count > 0) best = other;
                }
                string pick = best[rng.Next(best.Count)].Id;
                current[Key(li.Code, actor)] = pick;
                Ctx.Log("[VOICE] drawn for " + Actors.Names[actor - 1] + " (" + li.Code + "): " + pick);
                return pick;
            }
        }

        static bool InPool(ActorSetting a, string id)
        {
            return a.Pool == null || a.Pool.Count == 0 || a.Pool.Contains(id);
        }

        static List<VoiceInfo> Without(List<VoiceInfo> cand, List<string> a, List<string> b)
        {
            List<VoiceInfo> r = new List<VoiceInfo>();
            foreach (VoiceInfo v in cand)
                if ((a == null || !a.Contains(v.Id)) && (b == null || !b.Contains(v.Id))) r.Add(v);
            return r;
        }
    }

    internal sealed class Plan
    {
        public VoiceInfo Voice;
        public double Speed;
        public string Lang;
        public int Actor;
        public double Volume = 100;
        public bool Radio;
        public string Beep;
        public string Fx;
        public bool Far;
        public string Note;
    }

    internal static class Resolver
    {
        // No voice chosen for the actor: one of the family by the number of the actor. The technicians are the exception: Technician male (6) takes a male
        // voice and Technician female (7) a female one when the family has them (the gender is the one the voice list shows), so that the voice always
        // follows the technician of the scene; without such a voice the rule by number applies.
        static VoiceInfo FallbackVoice(List<VoiceInfo> fam, int actor)
        {
            if (actor == 6 || actor == 7)
            {
                string want = actor == 7 ? " - female" : " - male";
                List<VoiceInfo> match = fam.FindAll(delegate (VoiceInfo x) { return x.Display != null && x.Display.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0; });
                if (match.Count > 0) return match[0];
            }
            return fam[(actor - 1) % fam.Count];
        }

        public static Plan Resolve(Segment seg, Settings s, List<VoiceInfo> voices)
        {
            Plan p = new Plan();
            LangInfo li = LangInfo.ByCode(seg.Lang);
            string note = "";
            if (li == null)
            {
                if (seg.Lang != null) note += "language " + seg.Lang + " not supported, using " + s.DefaultLanguage + "; ";
                li = LangInfo.ByCode(s.DefaultLanguage) ?? LangInfo.All[0];
            }
            p.Lang = li.Code;
            p.Actor = seg.Actor > 0 ? seg.Actor : 1;
            ActorSetting a = s.Get(li.Code, p.Actor);

            VoiceInfo v = null;
            if (!string.IsNullOrEmpty(seg.Voice))
            {
                v = VoiceLibrary.Find(voices, seg.Voice, li.Family);
                if (v == null) note += "voice '" + seg.Voice + "' not installed, using the actor voice; ";
            }
            bool forced = v != null;
            if (v == null)
            {
                v = VoiceLibrary.ById(voices, a.Random ? VoiceDraw.Pick(s, li, p.Actor, voices) : a.Voice);
                if (v != null && v.Family != li.Family && !string.IsNullOrEmpty(a.Voice)) { /* kept: the user chose it */ }
                if (v == null)
                {
                    List<VoiceInfo> fam = VoiceLibrary.OfFamily(voices, li.Family);
                    if (fam.Count > 0) v = FallbackVoice(fam, p.Actor);
                    else if (voices.Count > 0) { v = voices[0]; note += "no " + li.Name + " voice installed; "; }
                }
            }
            p.Voice = v;
            p.Speed = seg.Speed > 0 ? seg.Speed : ((forced && seg.Actor == 0) ? 1.0 : a.Speed);
            p.Volume = seg.Volume >= 0 ? seg.Volume : 100;
            p.Radio = seg.Radio == 1;
            p.Beep = seg.Beep;
            p.Fx = seg.Fx;
            p.Far = seg.Far;
            p.Note = note;
            return p;
        }
    }
}
