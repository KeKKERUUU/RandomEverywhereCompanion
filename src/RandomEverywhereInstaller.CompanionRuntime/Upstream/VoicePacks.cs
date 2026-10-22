// HPG Mission Companion - voice packs: XML files in the voicepacks folder with the texts of the mission messages (several options each),
// and the <say:KEY> tag family that the missions use to speak them. See docs/architecture/VOICEPACKS.md.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace HPGMissionCompanion
{
    internal sealed class PackMessage
    {
        public string Key;
        public int Actor = 1;
        public string Radio = "auto";           // auto: the program decides from the context of the mission; on (radio call), off (voice), ics (intercom), ext (external radio call, heard only when the radio is on), ext-near (external call from somebody next to the crew, always heard): the pack decides when the mission sends no radio tag
        public string Group = "";               // conversation: with the coherent variety the lines of a group use the same option number
        public int Seq;
        public string Lvar = "";                // the LVAR of the wav voice packs (information)
        public string Wav = "";                 // the wav of the same message (information, or the file played by a sound-only message)
        public double Sleep = -1;               // seconds the mission waits after the message (information)
        public string Trigger = "";             // when the message happens (information)
        public bool Sfx;                        // a sound without text: the wav is played from the audio folder
        public int Vol = 100;                   // vol="70": the level (0-100) of the wav of a sound-only message, at most; 100 = the file as it is
        public bool Futile;                     // small talk that nobody needs: its options 1 and 2 are silent, 3 is the default; the options of a conversation of futile messages are always coherent
        public bool ToTech;                     // to="tech": a crew line said to the technician of the scene (not to the crew): heard like the technician is (intercom next to the helicopter with the speaker aboard and the battery on, else voice), never as a radio call
        public bool Text;                       // the words of a text that the mission speaks as it is (<text:KEY>English in the mission): the pack replaces the English words, the mission decides who speaks and how
        public List<string> Options = new List<string>();
        public List<string> OptionSex = new List<string>();     // for each option: "" (any technician), "m" or "f" (only when the technician of the scene is male or female); the voice follows: actor 6 or 7

        // the LVAR the mission increments for this message when its voice code is vcp: "L:CPL{VCP}5" -> "L:CPLAPP5"; "" when the message has none
        public string LvarFor(string vcp)
        {
            if (Lvar.Length < 3 || !Lvar.StartsWith("L:", StringComparison.OrdinalIgnoreCase) || Lvar.IndexOf("{VCP}", StringComparison.Ordinal) < 0) return "";
            return Lvar.Replace("{VCP}", vcp);
        }
    }

    internal sealed class VoicePack
    {
        public string Name = "";
        public string Language = "EN";
        public string Title = "";
        public int Version;
        public int BaseVer;                     // basever="": the version of the base pack (EN) this pack was made from or last completed with; 0 = not said
        public string MinApp = "";              // minapp="": the oldest program version that can use the pack ("" = any)
        public string File = "";
        public Dictionary<string, PackMessage> Messages = new Dictionary<string, PackMessage>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Pools = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);   // names to draw from for each crew role
        public string Vcp = "APP";              // the voice code the mission uses when an external program plays the voices (L:AUDIOINHIBIT = 1)
        public List<string> Problems = new List<string>();
        public bool Usable { get { return Name.Length > 0 && Version > 0; } }
        public override string ToString() { return Name + " v" + Version; }
    }

    internal static class VoicePacks
    {
        public static List<VoicePack> Packs = new List<VoicePack>();
        public static string Dir { get { return Path.Combine(Ctx.BaseDir, "voicepacks"); } }
        public static string ObsoleteDir { get { return Path.Combine(Dir, "obsolete"); } }

        public static void Refresh()
        {
            List<VoicePack> list = new List<VoicePack>();
            if (Directory.Exists(Dir))
            {
                string[] files = Directory.GetFiles(Dir, "*.xml");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    VoicePack p = Load(f);
                    list.Add(p);
                    foreach (string problem in p.Problems) Ctx.Log("[WARN] voice pack " + Path.GetFileName(f) + ": " + problem);
                }
            }
            Packs = list;
            VoicePack reference = PackCheck.Reference();
            foreach (VoicePack p in list)
            {
                if (!p.Usable || p == reference) continue;
                PackReport r = PackCheck.Compare(p, reference);
                if (r.Level > 0) Ctx.Log("[WARN] voice pack " + Path.GetFileName(p.File) + ": " + r.Summary());
            }
            Say.Reload();
        }

        // Reads and checks a pack file; it never throws: the problems are in Problems and an unusable pack has Name "" or Version 0.
        public static VoicePack Load(string path)
        {
            VoicePack p = new VoicePack();
            p.File = path;
            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            try { using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 })) doc.Load(reader); }
            catch (Exception ex) { p.Problems.Add("not a valid XML file: " + ex.Message); return p; }
            XmlElement root = doc.DocumentElement;
            if (root == null || root.Name != "voicepack") { p.Problems.Add("the root element must be <voicepack>"); return p; }
            p.Name = (root.GetAttribute("name") ?? "").Trim();
            if (p.Name.Length == 0)       // no name inside the file: the file name is the pack name (My pack.xml -> My_pack)
                p.Name = Regex.Replace(Path.GetFileNameWithoutExtension(path).Trim(), "[^A-Za-z0-9_-]+", "_").Trim('_');
            if (p.Name.Length > 24) p.Name = p.Name.Substring(0, 24);
            p.Language = (root.GetAttribute("language") ?? "EN").Trim().ToUpperInvariant();
            if (p.Language.Length == 0) p.Language = "EN";
            p.Title = (root.GetAttribute("title") ?? "").Trim();
            string vcp = (root.GetAttribute("vcp") ?? "").Trim().ToUpperInvariant();
            if (vcp.Length > 0)
            {
                if (Regex.IsMatch(vcp, "^[A-Z0-9]{1,12}$")) p.Vcp = vcp; else p.Problems.Add("vcp may only have letters and digits (up to 12)");
            }
            int v;
            if (!int.TryParse(root.GetAttribute("version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v < 1) { p.Problems.Add("version must be a whole number from 1"); v = 0; }
            p.Version = v;
            int bv;
            if (int.TryParse(root.GetAttribute("basever"), NumberStyles.Integer, CultureInfo.InvariantCulture, out bv) && bv >= 1) p.BaseVer = bv;
            else if (root.HasAttribute("basever")) p.Problems.Add("basever must be a whole number from 1");
            p.MinApp = (root.GetAttribute("minapp") ?? "").Trim();
            if (p.MinApp.Length > 0 && !Regex.IsMatch(p.MinApp, "^[0-9]+\\.[0-9]+$")) { p.Problems.Add("minapp must look like 0.13"); p.MinApp = ""; }
            if (p.Name.Length == 0) p.Problems.Add("the pack has no name");
            else if (!Regex.IsMatch(p.Name, "^[A-Za-z0-9_-]{1,24}$")) { p.Problems.Add("the name may only have letters, digits, _ and - (up to 24)"); p.Name = ""; }
            if (LangInfo.ByCode(p.Language) == null) p.Problems.Add("language " + p.Language + " is not supported (EN, FR, IT, DE)");

            foreach (XmlNode n in root.SelectNodes("names/name"))
            {
                XmlElement e = n as XmlElement;
                if (e == null) continue;
                string role = e.GetAttribute("role").Trim();
                if (role.Length > 0) p.Names[role] = e.InnerText.Trim();
            }
            foreach (XmlNode n in root.SelectNodes("names/pool"))
            {
                XmlElement e = n as XmlElement;
                if (e == null) continue;
                string role = e.GetAttribute("role").Trim();
                if (role.Length == 0) continue;
                List<string> list = new List<string>();
                foreach (string part in e.InnerText.Split(new char[] { ',', ';' }))
                {
                    string nm = Say.Literal(part.Trim());
                    if (nm.Length > 0) list.Add(nm);
                }
                if (list.Count > 0) p.Pools[role] = list;
            }
            foreach (XmlNode n in root.SelectNodes("message"))
            {
                XmlElement e = n as XmlElement;
                if (e == null) continue;
                PackMessage m = new PackMessage();
                m.Key = e.GetAttribute("key").Trim();
                if (m.Key.Length == 0) { p.Problems.Add("a message has no key"); continue; }
                if (!Regex.IsMatch(m.Key, "^[A-Za-z0-9_.\\-]{1,40}$")) { p.Problems.Add("message " + m.Key + ": the key may only have letters, digits, . _ -"); continue; }
                if (p.Messages.ContainsKey(m.Key)) { p.Problems.Add("message " + m.Key + " appears twice, the first one is kept"); continue; }
                int actor;
                if (e.HasAttribute("actor"))
                {
                    if (int.TryParse(e.GetAttribute("actor"), NumberStyles.Integer, CultureInfo.InvariantCulture, out actor) && actor >= 1 && actor <= Actors.Count) m.Actor = actor;
                    else p.Problems.Add("message " + m.Key + ": actor must be 1-" + Actors.Count);
                }
                string radio = e.GetAttribute("radio").Trim().ToLowerInvariant();
                if (radio == "on" || radio == "off" || radio == "auto" || radio == "ics" || radio == "ext" || radio == "ext-near") m.Radio = radio;
                else if (radio.Length > 0) p.Problems.Add("message " + m.Key + ": radio must be auto, on, off, ics, ext or ext-near");
                m.Group = e.GetAttribute("group").Trim();
                int.TryParse(e.GetAttribute("seq"), NumberStyles.Integer, CultureInfo.InvariantCulture, out m.Seq);
                m.Lvar = e.GetAttribute("lvar").Trim();
                m.Wav = e.GetAttribute("wav").Trim();
                m.Trigger = e.GetAttribute("trigger").Trim();
                double sl;
                if (double.TryParse(e.GetAttribute("sleep"), NumberStyles.Float, CultureInfo.InvariantCulture, out sl)) m.Sleep = sl;
                m.Sfx = e.GetAttribute("sfx") == "1";
                int volAttr;
                if (int.TryParse(e.GetAttribute("vol"), NumberStyles.Integer, CultureInfo.InvariantCulture, out volAttr)) m.Vol = Math.Max(0, Math.Min(100, volAttr));
                m.Futile = e.GetAttribute("futile") == "1";
                m.Text = e.GetAttribute("text") == "1";
                m.ToTech = e.GetAttribute("to").Trim().ToLowerInvariant() == "tech";
                foreach (XmlNode o in e.SelectNodes("o"))
                {
                    string t = Regex.Replace(o.InnerText, "\\s+", " ").Trim();
                    bool silent = ((XmlElement)o).GetAttribute("silent") == "1";       // an empty option on purpose: nothing is said (keeps the option numbers)
                    if (t.Length == 0 && !silent) continue;
                    if (silent) t = "";
                    string sx = ((XmlElement)o).GetAttribute("sex").Trim().ToLowerInvariant();
                    if (sx == "male") sx = "m"; else if (sx == "female") sx = "f";
                    if (sx.Length > 0 && sx != "m" && sx != "f") { p.Problems.Add("message " + m.Key + ": sex must be m or f"); sx = ""; }
                    m.Options.Add(t);
                    m.OptionSex.Add(sx);
                }
                if (!m.Options.Exists(delegate (string s) { return s.Length > 0; }) && !(m.Sfx && m.Wav.Length > 0)) { p.Problems.Add("message " + m.Key + " has no <o> text"); continue; }
                p.Messages[m.Key] = m;
            }
            return p;
        }

        public static VoicePack Find(string name)
        {
            foreach (VoicePack p in Packs) if (p.Usable && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        // Upstream 0.36 selection order: an unset/missing choice must not switch to
        // a different language merely because its XML filename sorts first.
        public static VoicePack Preferred(Settings settings, string declaredPack = null)
        {
            foreach (var name in new[] { settings.BridgePack, declaredPack, settings.DefaultLanguage, PackCheck.BaseName })
            {
                var selected = Find(name);
                if (selected != null) return selected;
            }
            foreach (var pack in Packs) if (pack.Usable) return pack;
            return null;
        }

        // Copies a pack file into the voicepacks folder. An older pack of the same name goes to voicepacks\obsolete.
        // confirm is asked when the new version is not newer than the installed one; it returns true to replace.
        public static string Install(string path, Func<string, bool> confirm)
        {
            VoicePack np = Load(path);
            if (!np.Usable)
            {
                string why = np.Problems.Count > 0 ? np.Problems[0] : "unusable pack";
                throw new InvalidOperationException(Path.GetFileName(path) + " cannot be installed: " + why);
            }
            Directory.CreateDirectory(Dir);
            string target = Path.Combine(Dir, np.Name + ".xml");
            VoicePack old = File.Exists(target) ? Load(target) : null;
            if (old != null && old.Usable)
            {
                if (np.Version <= old.Version && !confirm(old.Name + " version " + old.Version + " is installed; this file is version " + np.Version + (np.Version == old.Version ? " (the same)." : " (older).") + "\n\nReplace it anyway?"))
                    return "Not installed: " + np.Name + " version " + old.Version + " was kept.";
                Directory.CreateDirectory(ObsoleteDir);
                string arch = Path.Combine(ObsoleteDir, old.Name + "-v" + old.Version + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".xml");
                File.Copy(target, arch, true);
            }
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) File.Copy(path, target, true);
            Refresh();
            int warnings = np.Problems.Count;
            VoicePack installed = Find(np.Name);
            PackReport rep = installed != null && installed != PackCheck.Reference() ? PackCheck.Compare(installed, PackCheck.Reference()) : null;
            return "Installed " + np.Name + " version " + np.Version + " (" + np.Messages.Count + " messages)" + (old != null && old.Usable ? "; version " + old.Version + " moved to the obsolete folder" : "") + (warnings > 0 ? "; " + warnings + " warning(s), see the log" : "") + (rep != null && rep.Level > 0 ? ". CHECK: " + rep.Summary() : "") + ".";
        }
    }

    // ---- the pack against the base pack (EN) ---------------------------------------------------------------
    // A translated pack copies the base pack: the same keys, the same {0} {1} values. The base pack grows with the missions (new keys, new texts), so a pack that
    // lacks keys speaks them in English (the program says so in the log) and the Voice packs tab shows it in red, with the keys that are missing.
    internal sealed class PackReport
    {
        public List<string> Missing = new List<string>();           // keys of the base pack that the pack does not have
        public int Extra;                                           // keys that the base pack does not have (fine: the pack has more)
        public List<string> Values = new List<string>();            // keys whose texts use a {n} value the base message has not, or never use one it has
        public int Same;                                            // messages whose texts are the English ones (not translated yet)
        public int Total;                                           // messages of the base pack
        public string Version = "";                                 // what is wrong with the versions ("" = nothing)
        public bool VersionBad;                                     // the versions cannot work together (red); otherwise only a hint (orange)
        public int Level { get { return Missing.Count > 0 || VersionBad ? 2 : (Values.Count > 0 || Version.Length > 0 ? 1 : 0); } }     // 0 fine, 1 orange, 2 red
        public string Summary()
        {
            List<string> parts = new List<string>();
            if (Missing.Count > 0) parts.Add("MISSING " + Missing.Count + " of the " + Total + " texts of the base pack (spoken in English)");
            if (Version.Length > 0) parts.Add(Version);
            if (Values.Count > 0) parts.Add(Values.Count + " text(s) with other {values} than the base pack");
            if (parts.Count == 0) return "OK" + (Same > 0 ? ", " + Same + " text(s) still the English ones" : "");
            return string.Join("; ", parts.ToArray());
        }
    }

    internal static class PackCheck
    {
        public const string BaseName = "EN";
        static readonly Regex ValueRx = new Regex("\\{(?:[A-Za-z]+:)?([0-9]+)\\}", RegexOptions.Compiled);

        public static VoicePack Reference() { return VoicePacks.Find(BaseName); }

        static HashSet<int> Values(PackMessage m)
        {
            HashSet<int> set = new HashSet<int>();
            foreach (string o in m.Options) foreach (Match x in ValueRx.Matches(o)) set.Add(int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture));
            return set;
        }

        static bool SameOptions(PackMessage a, PackMessage b)
        {
            if (a.Options.Count != b.Options.Count) return false;
            for (int i = 0; i < a.Options.Count; i++) if (!string.Equals(a.Options[i], b.Options[i], StringComparison.Ordinal)) return false;
            return true;
        }

        public static PackReport Compare(VoicePack p, VoicePack reference)
        {
            PackReport r = new PackReport();
            if (p == null) return r;
            if (reference != null && p != reference)
            {
                r.Total = reference.Messages.Count;
                foreach (KeyValuePair<string, PackMessage> kv in reference.Messages)
                {
                    PackMessage m;
                    if (!p.Messages.TryGetValue(kv.Key, out m)) { r.Missing.Add(kv.Key); continue; }
                    if (SameOptions(m, kv.Value)) r.Same++;
                    HashSet<int> have = Values(m), want = Values(kv.Value);
                    bool bad = false;
                    foreach (int n in have) if (!want.Contains(n)) bad = true;
                    foreach (int n in want) if (!have.Contains(n)) bad = true;
                    if (bad) r.Values.Add(kv.Key);
                }
                foreach (string key in p.Messages.Keys) if (!reference.Messages.ContainsKey(key)) r.Extra++;
                r.Missing.Sort(StringComparer.Ordinal);
                if (p.BaseVer > reference.Version) { r.Version = "made for the base pack version " + p.BaseVer + " but this program has " + reference.Version + ": update the program"; r.VersionBad = true; }
                else if (p.BaseVer > 0 && p.BaseVer < reference.Version) r.Version = "made for the base pack version " + p.BaseVer + ", now " + reference.Version + ": check the new texts";
            }
            int needMajor, needMinor, haveMajor, haveMinor;       // two integers: 0.10 comes after 0.9
            if (p.MinApp.Length > 0 && VersionCompatibility.ParseVersion(p.MinApp, out needMajor, out needMinor) && VersionCompatibility.ParseVersion(AppInfo.Version, out haveMajor, out haveMinor)
                && (needMajor > haveMajor || (needMajor == haveMajor && needMinor > haveMinor)))
            {
                r.Version = (r.Version.Length > 0 ? r.Version + "; " : "") + "needs the program " + p.MinApp + " or newer (this is " + AppInfo.Version + ")";
                r.VersionBad = true;
            }
            if (Say.Declared.Length > 0 && string.Equals(Say.Declared, p.Name, StringComparison.OrdinalIgnoreCase) && Say.Required > p.Version)
            {
                r.Version = (r.Version.Length > 0 ? r.Version + "; " : "") + "the mission needs version " + Say.Required + " of this pack";
                r.VersionBad = true;
            }
            return r;
        }

        // the base pack as the starting point of a translation: the same file with the name, the language and the version of the new pack
        public static string ExportTemplate(VoicePack reference, string name, string language, string path)
        {
            if (reference == null) throw new InvalidOperationException("The base pack " + BaseName + " is not installed.");
            language = (language ?? "").Trim().ToUpperInvariant();
            if (LangInfo.ByCode(language) == null) throw new InvalidOperationException("The language " + language + " is not supported (EN, FR, IT, DE).");
            name = Regex.Replace((name ?? "").Trim(), "[^A-Za-z0-9_-]+", "_").Trim('_');
            if (name.Length == 0 || name.Length > 24) throw new InvalidOperationException("The name may only have letters, digits, _ and - (up to 24).");
            XmlDocument doc = new XmlDocument();
            doc.PreserveWhitespace = true;
            doc.Load(reference.File);
            XmlElement root = doc.DocumentElement;
            root.SetAttribute("name", name);
            root.SetAttribute("language", language);
            root.SetAttribute("title", language + " voices of the HEMS missions (translation of " + BaseName + " " + reference.Version + ")");
            root.SetAttribute("version", "1");
            root.SetAttribute("basever", reference.Version.ToString(CultureInfo.InvariantCulture));
            XmlNode comment = null;
            foreach (XmlNode n in doc.ChildNodes) if (n.NodeType == XmlNodeType.Comment) { comment = n; break; }
            XmlComment help = doc.CreateComment(" Translation of the pack " + BaseName + ". Translate the texts inside <o>..</o>; keep the keys, the {0} {1} values, the tags and the number of options. Raise version=\"\" at every change. basever is the version of " + BaseName + " this file was made from. ");
            if (comment != null) doc.ReplaceChild(help, comment); else doc.InsertBefore(help, root);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            XmlWriterSettings ws = new XmlWriterSettings();
            ws.Encoding = new UTF8Encoding(false);
            using (XmlWriter w = XmlWriter.Create(path, ws)) doc.Save(w);
            return "Template of the pack " + name + " (" + language + ") written to " + path + ": " + reference.Messages.Count + " messages to translate.";
        }

        // adds the messages that the base pack has and the pack lacks, with their English texts, to the pack file (the old file is kept in voicepacks\obsolete); basever and version go up
        public static string AddMissing(VoicePack p, VoicePack reference)
        {
            if (p == null || !p.Usable) throw new InvalidOperationException("Choose a usable pack.");
            if (reference == null) throw new InvalidOperationException("The base pack " + BaseName + " is not installed.");
            if (p == reference) throw new InvalidOperationException("This is the base pack.");
            PackReport r = Compare(p, reference);
            XmlDocument refDoc = new XmlDocument();
            refDoc.PreserveWhitespace = true;
            refDoc.Load(reference.File);
            XmlDocument doc = new XmlDocument();
            doc.PreserveWhitespace = true;
            doc.Load(p.File);
            XmlElement root = doc.DocumentElement;
            HashSet<string> want = new HashSet<string>(r.Missing, StringComparer.OrdinalIgnoreCase);
            int added = 0;
            if (want.Count > 0)
            {
                root.AppendChild(doc.CreateWhitespace("\n  "));
                root.AppendChild(doc.CreateComment(" Added from the base pack " + BaseName + " version " + reference.Version + ": still in English, translate them. "));
                root.AppendChild(doc.CreateWhitespace("\n"));
                foreach (XmlNode n in refDoc.DocumentElement.SelectNodes("message"))
                {
                    XmlElement e = n as XmlElement;
                    if (e == null || !want.Contains(e.GetAttribute("key").Trim())) continue;
                    root.AppendChild(doc.ImportNode(e, true));
                    root.AppendChild(doc.CreateWhitespace("\n"));
                    added++;
                }
            }
            root.SetAttribute("basever", reference.Version.ToString(CultureInfo.InvariantCulture));
            root.SetAttribute("version", (p.Version + 1).ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(VoicePacks.ObsoleteDir);
            string arch = Path.Combine(VoicePacks.ObsoleteDir, p.Name + "-v" + p.Version + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".xml");
            System.IO.File.Copy(p.File, arch, true);
            XmlWriterSettings ws = new XmlWriterSettings();
            ws.Encoding = new UTF8Encoding(false);
            using (XmlWriter w = XmlWriter.Create(p.File, ws)) doc.Save(w);
            VoicePacks.Refresh();
            return p.Name + ": " + added + " message(s) added in English" + (added == 0 ? " (none was missing)" : "") + ", version " + (p.Version + 1) + " (basever " + reference.Version + "); the old file is in voicepacks\\obsolete.";
        }
    }

    // ---- the tags that use the packs ------------------------------------------------------------------------
    //   <say:KEY>args      speak the message KEY of the pack; args = texts separated by // that fill {0} {1} ...  (everything after the tag)
    //   <say:KEY:3>args    the same with option 3 forced
    //   <plain>text        speak the text as it is: no tag inside it is interpreted (for free texts such as a dispatch)
    //   <text:KEY>English text//value//value   a free text of the mission that has a key: the active pack may have a message KEY with the words in its language
    //                      (the tags before it, the actor and the radio, stay the mission's); without one the English text is spoken as it is. The values fill {0} {1}
    //   <pack:EN:2>        the pack the mission uses and the version it needs (status goes to L:HMC_VOICE_STATUS)
    //   <names:PLT=Mark,CPL=Anna>  names for {PLT} {CPL} {PAX1} {PAX2} {PAX3}; <names:off> goes back to the functions
    //   <variety:random> / <variety:coherent>  how the option of a message is chosen
    //   <defaultactor:11>  actor 11 speaks every text that has no actor tag (the mission checklists); <defaultactor:off> ends it
    internal static class Say
    {
        const string Rest = "(?:\\s*[:=]\\s*([^<>\\[\\]]{0,400}))?";
        static readonly Regex ControlRx = new Regex("[<\\[]\\s*(say|pack|mission|dsp|voices|names|variety|plain|defaultactor|text)" + Rest + "\\s*[>\\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ActorTagRx = new Regex("[<\\[]\\s*actor\\s*\\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex RadioTagRx = new Regex("[<\\[]\\s*radio\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex HolderRx = new Regex("\\{(@?[A-Za-z0-9_]+(?::[0-9]+)?)\\}", RegexOptions.Compiled);

        // crew roles and technicians; {PLT} is the job word, {@PLT} the name of the shift (random or typed by the user) or the job word when there is none
        static readonly string[] Roles = { "PLT", "CPL", "PAX1", "PAX2", "PAX3", "TECHM", "TECHF" };
        static readonly string[] RoleDefaults = { "Pilot", "Copilot", "Chief", "Paramedic", "Doc", "Technician", "Technician" };
        static int techSex = -1;                            // L:HMC_TECH_SEX: 0 male, 1 female, -1 not known

        static readonly object sync = new object();
        static Random rng = new Random();
        static string declared = "";
        static int required;
        static bool coherent;
        static readonly Dictionary<string, string> missionNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int> lastOption = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static string lastGroup = "";
        static DateTime lastGroupTime = DateTime.MinValue;
        static int lastGroupOption;
        static readonly HashSet<string> warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string bridgePack = "";                      // the pack read from the simulator LVARs (setting)
        static bool ctxValid;                               // the mission publishes its context (L:HMC_CTX_VALID)
        static bool heliOff;                                // L:HMC_HELI_OFF
        static int crewMask;                                // L:HMC_CREW_PRESENT: bit 0 pilot, 1 copilot, 2 doctor, 3 HEMS1, 4 HEMS2 are aboard
        static int chatMax = 6;                               // L:HMC_CHAT_MAX: the highest option number the mission lets a message use (the setting CREW CHATTER: 6 full, 3 reduced, 1 minimal)
        static int muteMask;                                // L:HMC_CREW_MUTE: the same bits, crew members who cannot speak
        static readonly Dictionary<string, PackMessage> watchMap = new Dictionary<string, PackMessage>(StringComparer.OrdinalIgnoreCase);
        static int radioVar = -1;                           // the battery master of the aircraft (SimLink.CtxRadio): 1 on, -1 not read
        static bool atBase;                                 // L:HMC_AT_BASE
        static int crewExist;                               // L:HMC_CREW_EXIST (0 = not published)
        static int zoneTech, zoneGround, zonePatient;       // L:HMC_ZONE_TECH / _GROUND / _PATIENT (0 = not published)
        static VoicePack watchPack;
        static string lastBridgeText = "";
        public static Action<string> Sink;                  // tests: gets the text that would be spoken instead of the player

        public static string Declared { get { return declared; } }
        public static int Required { get { return required; } }
        public static string BridgePackName { get { return bridgePack; } }

        public static void Seed(int seed) { lock (sync) { rng = new Random(seed); } }

        // Session state is not configuration; do not reuse the previous flight's context.
        public static void ResetSession()
        {
            lock (sync)
            {
                declared = missionId = bridgePack = lastBridgeText = lastGroup = "";
                defaultActor = required = crewMask = crewExist = muteMask = zoneTech = zoneGround = zonePatient = 0;
                ctxValid = heliOff = atBase = false;
                techSex = radioVar = -1; chatMax = 6;
                dspIndex = -2; dspCity = callSign = atcId = atcAirline = atcNumber = "";
                missionNames.Clear(); lastOption.Clear(); warned.Clear(); watchMap.Clear(); watchPack = null;
                lastGroupTime = DateTime.MinValue; lastGroupOption = 0;
            }
            VoiceDraw.Reset();
        }

        public static void Reload()
        {
            lock (sync) { warned.Clear(); }
            if (declared.Length > 0) Declare(declared, required, false);
            ApplySettings();
        }

        // The runtime and shell snapshot share the same usable-pack fallback order.
        public static void ApplySettings()
        {
            SetVariety(Ctx.Settings.Variety);
            VoicePack p = VoicePacks.Preferred(Ctx.Settings, declared);
            SetBridgePack(p != null ? p.Name : "");
            SimLink.OnWatch = OnWatch;
            SimLink.OnAtcStrings = OnAtc;
            ApplyCallSign();
            SimLink.SetWatch(WatchNames());
            if (Ctx.Settings.Names == "random") RandomizeNames(); else lock (sync) { ApplyUserNames(); }
        }

        // the names typed in the Actors tab (the option "functions"): {@PLT} says them; a role without a name keeps its job word
        public static void ApplyUserNamesNow()
        {
            lock (sync) { if (Ctx.Settings.Names != "random") ApplyUserNames(); }
        }

        static void ApplyUserNames()
        {
            missionNames.Clear();
            foreach (string role in Roles)
            {
                string n;
                if (Ctx.Settings.CrewNames != null && Ctx.Settings.CrewNames.TryGetValue(role, out n) && n != null)
                {
                    n = Literal(n.Trim());
                    if (n.Length > 0) missionNames[role] = n;
                }
            }
        }

        // ---- main entry: the text of a speak message in, the text for the tag parser out ----
        public static string Process(string text, List<string> notes, out string wavFile)
        {
            wavFile = null;
            if (string.IsNullOrEmpty(text)) return text;
            if (!ControlRx.IsMatch(text)) { lock (sync) { return ApplyDefaultActor(text); } }
            lock (sync)
            {
                StringBuilder outp = new StringBuilder();
                int pos = 0;
                Match m = ControlRx.Match(text);
                while (m.Success)
                {
                    outp.Append(text, pos, m.Index - pos);
                    pos = m.Index + m.Length;
                    string kind = m.Groups[1].Value.ToLowerInvariant();
                    string arg = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
                    if (kind == "pack") ParsePack(arg, notes);
                    else if (kind == "mission") ParseMission(arg);
                    else if (kind == "dsp") ParseDsp(arg);
                    else if (kind == "voices") ParseVoices(arg);
                    else if (kind == "defaultactor") ParseDefaultActor(arg, notes);
                    else if (kind == "names") ParseNames(arg);
                    else if (kind == "variety") ParseVariety(arg, notes);
                    else if (kind == "plain")
                    {
                        outp.Append(Literal(text.Substring(pos)));
                        pos = text.Length;
                        break;
                    }
                    else if (kind == "text")
                    {
                        outp.Append(ExpandText(arg, text.Substring(pos), notes));
                        pos = text.Length;
                        break;
                    }
                    else   // say: the rest of the text is the argument list
                    {
                        outp.Append(Expand(arg, text.Substring(pos), outp.ToString(), notes, out wavFile));
                        pos = text.Length;
                        break;
                    }
                    m = m.NextMatch();
                }
                if (pos < text.Length) outp.Append(text, pos, text.Length - pos);
                return ApplyDefaultActor(outp.ToString());
            }
        }

        // free text: no tag inside it works
        public static string Literal(string s)
        {
            if (s == null) return "";
            s = Regex.Replace(s, "<[^<>]{0,80}>", " ");        // <br /> <b> and any other tag are dropped, not read
            return s.Replace('<', ' ').Replace('>', ' ').Replace('[', '(').Replace(']', ')');
        }

        // ---- default actor: <defaultactor:11> makes actor 11 the voice of every text that has no actor tag until <defaultactor:off> ----
        static int defaultActor;
        static readonly Regex TagOnlyRx = new Regex("<[^<>]*>|\\[[^\\[\\]]*\\]", RegexOptions.Compiled);

        static void ParseDefaultActor(string arg, List<string> notes)
        {
            int n;
            if (arg.Length == 0 || string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase) || arg == "0") { defaultActor = 0; return; }
            if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 1 && n <= Actors.Count) defaultActor = n;
            else if (notes != null) notes.Add("defaultactor " + arg + " does not exist (1-" + Actors.Count + " or off)");
        }

        static string ApplyDefaultActor(string t)
        {
            if (defaultActor == 0 || string.IsNullOrEmpty(t) || ActorTagRx.IsMatch(t)) return t;
            if (TagOnlyRx.Replace(t, "").Trim().Length == 0) return t;
            return "<actor" + defaultActor + ">" + t;
        }

        // ---- state tags ----
        // <mission:RE>: the identity of the mission that speaks (RE = Random & Everywhere). Only kept and logged for now; a pack may later say which mission it is for.
        static string missionId = "";
        public static string MissionId { get { return missionId; } }

        // <voices:shift> a new shift: the drawn voice of the dispatcher changes; <voices:dispatch> a new dispatch: the drawn voices of the ground unit and the heli rescuer
        // change (<voices:all> both). The draw itself happens at the next line of that actor (VoiceDraw).
        static void ParseVoices(string arg)
        {
            string a = (arg ?? "").Trim().ToLowerInvariant();
            if (a == "shift" || a == "all") VoiceDraw.Redraw(8);
            if (a == "dispatch" || a == "all") VoiceDraw.Redraw(9, Actors.HeliRescuer);
            Ctx.Log("[INFO] New voice draw: " + (a.Length > 0 ? a : "(nothing)"));
        }

        static void ParseMission(string arg)
        {
            arg = Regex.Replace(arg == null ? "" : arg.Trim(), "[^A-Za-z0-9_.-]", "");
            if (arg.Length > 16) arg = arg.Substring(0, 16);
            if (arg == missionId) return;
            missionId = arg;
            Ctx.Log("[INFO] Mission: " + (arg.Length > 0 ? arg : "(none)"));
        }

        // <dsp:INDEX:CITY>: who the dispatcher is (user request of 2026-10-16). INDEX is the position of the group selected on the CARLS in the static GRPID of the
        // mission (-1 the manual frequency), CITY the city of the base (empty when the mission did not find it). The words come from the pack: the message
        // DSP.NAME.<index> ({0} the city) or, without a city, DSP.NAMEX.<index>; {DSP} and {@DSP} in a text become those words. The last tag stays until another comes.
        static int dspIndex = -2;            // -2 = no tag yet: the old word "dispatch"
        static string dspCity = "";
        static string callSign = "";         // the call sign of the helicopter ({@HEMS}); empty = HEMS

        public static int DispatcherIndex { get { return dspIndex; } }
        public static string DispatcherCity { get { return dspCity; } }

        static void ParseDsp(string arg)
        {
            int idx = -1; string city = "";
            string a = arg == null ? "" : arg.Trim();
            int c = a.IndexOf(':');
            string head = c >= 0 ? a.Substring(0, c) : a;
            if (c >= 0) city = a.Substring(c + 1).Trim();
            if (!int.TryParse(head.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) idx = -1;
            if (Regex.IsMatch(city, "^[A-Za-z0-9_]+_[Nn]ame$")) city = "";        // an unresolved name of the simulator (the query of the mission found nothing)
            city = Regex.Replace(city, "[<>\\[\\]{}]", " ").Trim();
            if (city.Length > 40) city = city.Substring(0, 40);
            if (idx == dspIndex && string.Equals(city, dspCity, StringComparison.Ordinal)) return;
            dspIndex = idx; dspCity = city;
            Ctx.Log("[INFO] Dispatcher: group " + idx + (city.Length > 0 ? ", city " + city : ", no city"));
        }

        // the words for {DSP}: the pack message of the group, the manual frequency (-1) is the Dispatcher, no tag the old "dispatch"
        static string DispatcherWords(VoicePack pack)
        {
            if (dspIndex == -2) return "dispatch";
            string key = dspIndex < 0 ? "DSP.NAME.MAN" : (dspCity.Length > 0 ? "DSP.NAME." + dspIndex : "DSP.NAMEX." + dspIndex);
            PackMessage m;
            if (pack != null && !pack.Messages.TryGetValue(key, out m) && dspIndex >= 0) key = "DSP.NAME." + dspIndex;
            if (pack != null && pack.Messages.TryGetValue(key, out m) && m.Options.Count > 0)
            {
                string t = m.Options[0].Length > 0 ? m.Options[0] : "";
                if (t.Length > 0) return Fill(t, new string[] { dspCity }, pack, null).Trim();
            }
            return dspIndex < 0 ? "Dispatcher" : "dispatch";
        }

        // the call sign of the helicopter: the setting of the user, else what the livery says (airline and flight number of the aircraft, Pelikan 3), else the tail
        // number spelled, else HEMS
        public static void SetCallSign(string s)
        {
            s = (s ?? "").Trim();
            lock (sync) { if (s == callSign) return; callSign = s; }
            Ctx.Log("[INFO] Call sign of the helicopter: " + (s.Length > 0 ? s : "HEMS"));
        }

        static string CallSignWords() { return callSign.Length > 0 ? callSign : "HEMS"; }

        static string atcId = "", atcAirline = "", atcNumber = "";

        // what the simulator says about the aircraft (SimLink, every 30 s)
        internal static void OnAtc(string id, string airline, string number)
        {
            lock (sync)
            {
                if (id == atcId && airline == atcAirline && number == atcNumber) return;
                atcId = id; atcAirline = airline; atcNumber = number;
            }
            Ctx.Log("[INFO] Aircraft: ATC ID '" + id + "', airline '" + airline + "', flight number '" + number + "'");
            ApplyCallSign();
        }

        // the setting Call sign of the Actors tab wins; empty = what the livery says
        public static void ApplyCallSign()
        {
            if (Ctx.Settings.CallSignMode == "custom") { SetCallSign(Ctx.Settings.CallSign ?? ""); return; }
            string a, b, c;
            lock (sync) { a = atcId; b = atcAirline; c = atcNumber; }
            SetCallSign(BuildCallSign(a, b, c));
        }

        // Preserve the livery airline; a missing airline has no invented call sign.
        public static string BuildCallSign(string atcId, string airline, string number)
        {
            // the atc_airline of the aircraft.cfg of the livery ("PELIKAN 3" -> "Pelikan 3"); the flight number and the tail number are no longer used (user, 2026-10-16)
            airline = Regex.Replace((airline ?? "").Trim(), "\\s+", " ");
            if (airline.Length == 0) return "";
            string[] words = airline.Split(' ');
            for (int i = 0; i < words.Length; i++)
                words[i] = words[i].Length > 1 ? char.ToUpperInvariant(words[i][0]) + words[i].Substring(1).ToLowerInvariant() : words[i].ToUpperInvariant();
            return string.Join(" ", words);
        }

        static void ParsePack(string arg, List<string> notes)
        {
            string name = arg; int ver = 0;
            int c = arg.IndexOf(':');
            if (c >= 0)
            {
                name = arg.Substring(0, c).Trim();
                int.TryParse(arg.Substring(c + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ver);
            }
            Declare(name, ver, true);
        }

        static void Declare(string name, int need, bool announce)
        {
            declared = name; required = need;
            VoicePack p = VoicePacks.Find(name);
            int status; int version = 0;
            if (p == null)
            {
                status = 2;
                if (announce) Ctx.Log("[WARN] The mission needs the voice pack " + name + (need > 0 ? " version " + need : "") + ", which is not installed: add it in the Voice packs tab.");
            }
            else
            {
                version = p.Version;
                if (need > 0 && p.Version < need)
                {
                    status = 3;
                    if (announce) Ctx.Log("[WARN] The voice pack " + p.Name + " is version " + p.Version + " but the mission needs version " + need + ": the pack is obsolete, install a newer one.");
                }
                else
                {
                    status = 1;
                    if (announce) Ctx.Log("[INFO] Voice pack " + p.Name + " version " + p.Version + (need > 0 ? " (the mission needs " + need + ")" : "") + " in use.");
                }
            }
            SimLink.Publish(SimLink.VarStatus, status);
            SimLink.Publish(SimLink.VarPackVersion, version);
        }

        static void ParseNames(string arg)
        {
            missionNames.Clear();
            if (arg.Length == 0 || string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(arg, "random", StringComparison.OrdinalIgnoreCase)) { if (Ctx.Settings.Names == "random") DrawNames(); else ApplyUserNames(); return; }     // the user chooses in the Voice packs tab; the mission only says "a new shift"
            foreach (string part in arg.Split(new char[] { ',', ';' }))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string role = part.Substring(0, eq).Trim();
                string name = Literal(part.Substring(eq + 1).Trim());
                if (role.Length > 0 && name.Length > 0) missionNames[role] = name;
            }
        }

        static void ParseVariety(string arg, List<string> notes)
        {
            string a = arg.Trim().ToLowerInvariant();
            if (a == "coherent" || a == "coherence") { coherent = true; lastGroup = ""; }
            else if (a == "random" || a == "") coherent = false;
            else if (notes != null) notes.Add("variety " + arg + " does not exist (random, coherent)");
        }

        // ---- settings, names, variety ----
        public static void SetVariety(string v)
        {
            lock (sync) { coherent = string.Equals(v, "coherent", StringComparison.OrdinalIgnoreCase); lastGroup = ""; }
        }

        public static void SetBridgePack(string name)
        {
            lock (sync) { bridgePack = name ?? ""; }
        }

        // new names for the crew roles and the technicians (the start of a shift): one name from the static lists (src/Names.cs: the first names of the
        // patients of the mission) or from the <pool> of the pack for that role; the male technician takes a male name, the female one a female name; never the same name twice
        public static void RandomizeNames()
        {
            lock (sync) { DrawNames(); }
        }

        static void DrawNames()
        {
            missionNames.Clear();
            VoicePack p = null;
            if (declared.Length > 0) p = VoicePacks.Find(declared);
            if (p == null && bridgePack.Length > 0) p = VoicePacks.Find(bridgePack);
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int r = 0; r < Roles.Length; r++)
            {
                List<string> pool = null;
                if (p != null) p.Pools.TryGetValue(Roles[r], out pool);
                if (pool == null || pool.Count == 0)
                {
                    pool = new List<string>();
                    if (Roles[r] != "TECHF") pool.AddRange(NameLists.Male);
                    if (Roles[r] != "TECHM") pool.AddRange(NameLists.Female);
                }
                string pick = pool[rng.Next(pool.Count)];
                for (int tries = 0; tries < 12 && used.Contains(pick); tries++) pick = pool[rng.Next(pool.Count)];
                used.Add(pick);
                missionNames[Roles[r]] = pick;
            }
            Ctx.Log("[INFO] Names for this shift: " + string.Join(", ", new string[] { "pilot " + missionNames["PLT"], "copilot " + missionNames["CPL"], "chief " + missionNames["PAX1"], "paramedic " + missionNames["PAX2"], "doc " + missionNames["PAX3"], "technician (male) " + missionNames["TECHM"], "technician (female) " + missionNames["TECHF"] }));
        }

        // true: the speaker is aboard, false: outside, null: not known (no context from the mission, or the dispatcher)
        public static bool? SpeakerInside(int actor)
        {
            lock (sync)
            {
                if (!ctxValid || actor == 8 || actor == 11) return null;      // the dispatcher is remote, the checklist voice is the tablet: no position
                if (crewMask == 0) return null;                  // nobody aboard: everybody is outside together, the voices keep the normal level
                if (actor >= 1 && actor <= 5) return (crewMask & (1 << (actor - 1))) != 0;
                return false;
            }
        }

        // ---- the simulator LVARs: the program watches the counters of the bridge pack ----
        // Names of the simulator variables to watch: the three context variables and the LVAR of every message of the bridge pack.
        public static List<string> WatchNames()
        {
            List<string> list = new List<string>();
            lock (sync)
            {
                watchMap.Clear();
                list.Add(Mixer.VarCrew); list.Add(Mixer.VarChk); list.Add(Mixer.VarCamera);
                watchPack = bridgePack.Length > 0 ? VoicePacks.Find(bridgePack) : null;
                if (watchPack == null) return list;
                list.Add(SimLink.CtxValid); list.Add(SimLink.CtxHeliOff); list.Add(SimLink.CtxCrew); list.Add(SimLink.CtxTechSex); list.Add(SimLink.CtxMute); list.Add(SimLink.CtxChat);
                list.Add(SimLink.CtxRadio); list.Add(SimLink.CtxExist); list.Add(SimLink.CtxBase); list.Add(SimLink.CtxZoneTech); list.Add(SimLink.CtxZoneGround); list.Add(SimLink.CtxZonePatient);
                foreach (PackMessage m in watchPack.Messages.Values)
                {
                    string l = m.LvarFor(watchPack.Vcp);
                    if (l.Length == 0 || watchMap.ContainsKey(l)) continue;
                    watchMap[l] = m;
                    list.Add(l);
                }
            }
            return list;
        }

        public static string BridgeStatus()
        {
            lock (sync)
            {
                if (watchPack == null) return "Simulator LVARs: no pack chosen";
                string c = !ctxValid ? "no context from the mission" : (heliOff ? "helicopter off" : "helicopter on") + ", crew aboard mask " + crewMask;
                return "Reading " + watchMap.Count + " LVARs of " + watchPack.Name + " (code " + watchPack.Vcp + "); " + c + (lastBridgeText.Length > 0 ? "; last: " + lastBridgeText : "");
            }
        }

        // a watched variable changed (SimLink calls this on its thread); baseline = the first value after the connection
        public static void OnWatch(string name, double oldValue, double value, bool baseline)
        {
            PackMessage m; VoicePack p;
            if (Mixer.IsMixerName(name)) { Mixer.OnWatch(name, value); return; }
            lock (sync)
            {
                // every change of the context goes to the log: how a line is heard (intercom, radio, external radio, voice) follows these values
                if (name == SimLink.CtxValid) { bool v = value > 0.5; CtxLog("mission context valid", ctxValid ? 1 : 0, v ? 1 : 0, baseline); ctxValid = v; return; }
                if (name == SimLink.CtxHeliOff) { bool v = value > 0.5; CtxLog("helicopter off (battery master off and rotor stopped)", heliOff ? 1 : 0, v ? 1 : 0, baseline); heliOff = v; return; }
                if (name == SimLink.CtxCrew) { int v = (int)Math.Round(value); CtxLog("crew aboard mask (1 pilot, 2 copilot, 4 doctor, 8 HEMS1, 16 HEMS2; the pilot is the user, the mission sets bit 1 from 169.49)", crewMask, v, baseline); crewMask = v; return; }
                if (name == SimLink.CtxMute) { int v = (int)Math.Round(value); CtxLog("crew who cannot speak mask", muteMask, v, baseline); muteMask = v; return; }
                if (name == SimLink.CtxRadio) { int v = value > 0.5 ? 1 : 0; CtxLog("battery master (" + SimLink.CtxRadio + ", the radio works with it on)", radioVar, v, baseline); radioVar = v; return; }
                if (name == SimLink.CtxExist) { int v = (int)Math.Round(value); CtxLog("crew existing mask", crewExist, v, baseline); crewExist = v; return; }
                if (name == SimLink.CtxBase) { bool v = value > 0.5; CtxLog("at the start base (dispatcher reaches the crew radios)", atBase ? 1 : 0, v ? 1 : 0, baseline); atBase = v; return; }
                if (name == SimLink.CtxZoneTech) { int v = (int)Math.Round(value); CtxLog("technician zone (1 up to 5 m, 2 up to 30 m, 3 up to 100 m, 4 beyond)", zoneTech, v, baseline); zoneTech = v; return; }
                if (name == SimLink.CtxZoneGround) { int v = (int)Math.Round(value); CtxLog("ground unit zone", zoneGround, v, baseline); zoneGround = v; return; }
                if (name == SimLink.CtxZonePatient) { int v = (int)Math.Round(value); CtxLog("patient zone", zonePatient, v, baseline); zonePatient = v; return; }
                if (name == SimLink.CtxChat) { int c = (int)Math.Round(value); int v = c >= 1 && c <= 6 ? c : 6; CtxLog("chatter level (highest option number)", chatMax, v, baseline); chatMax = v; return; }
                if (name == SimLink.CtxTechSex) { int v = value > 0.5 ? 1 : 0; CtxLog("technician sex (0 male, 1 female)", techSex, v, baseline); techSex = v; return; }
                if (baseline || double.IsNaN(oldValue) || double.IsInfinity(oldValue) ||
                    double.IsNaN(value) || double.IsInfinity(value) || value <= oldValue) return;
                // Only a finite rise speaks; resets, invalid samples and repeats are not messages.
                if (!watchMap.TryGetValue(name, out m)) return;
                p = watchPack;
                lastBridgeText = m.Key;
            }
            Speak(p, m, "", 30);
        }

        static void CtxLog(string what, int oldValue, int value, bool baseline)
        {
            if (oldValue == value && !baseline) return;
            Ctx.Log("[CTX] " + what + ": " + (baseline ? "read " + value : oldValue + " -> " + value));
        }

        // the line the pilot says when the program has started minimized with the simulator running: SYS.READY of the active pack (any option), else the English text
        public static string ReadyText()
        {
            lock (sync)
            {
                VoicePack pack = bridgePack.Length > 0 ? VoicePacks.Find(bridgePack) : null;
                PackMessage msg = null;
                if (pack != null) pack.Messages.TryGetValue("SYS.READY", out msg);
                if (pack == null || msg == null || msg.Options.Count == 0) return "<actor1>HPG Companion ready, minimized.";
                string wav;
                List<string> notes = new List<string>();
                string text = Compose(msg, pack, "", "", 0, notes, out wav);
                return string.IsNullOrEmpty(text) ? "<actor1>HPG Companion ready, minimized." : text;
            }
        }

        // the sound file of a message comes as "name" or "name|vol" (the vol of the pack message, 0-100)
        public static string SplitWav(string spec, out int vol)
        {
            vol = 100;
            int bar = spec.LastIndexOf('|');
            if (bar < 0) return spec;
            int v;
            if (int.TryParse(spec.Substring(bar + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) vol = Math.Max(0, Math.Min(100, v));
            return spec.Substring(0, bar);
        }

        // speaks one message of a pack (the LVAR bridge); lines older than maxAgeSeconds when their turn comes are dropped
        public static void Speak(VoicePack pack, PackMessage msg, string args, int maxAgeSeconds)
        {
            List<string> notes = new List<string>();
            string wav;
            string text;
            lock (sync) { text = Compose(msg, pack, args ?? "", "", 0, notes, out wav); }
            foreach (string n in notes) Ctx.Log("[WARN] " + n);
            if (Sink != null) { Sink((text ?? "") + (wav != null ? "  [sound file " + wav + "]" : "")); return; }
            if (Ctx.Player == null) return;
            if (wav != null) Ctx.Player.PlayAudioFile(wav, maxAgeSeconds);
            if (!string.IsNullOrEmpty(text)) Ctx.Player.EnqueueText(text, false, maxAgeSeconds);
        }

        // ---- text: a free text of the mission that has a key ----
        static string FirstText(PackMessage m)
        {
            foreach (string o in m.Options) if (o.Length > 0) return o;
            return "";
        }

        static string SameKey(string s)
        {
            return Regex.Replace(Literal(s), "\\s+", " ").Trim().TrimEnd('.', ' ').ToLowerInvariant();
        }

        // <text:KEY>English//value//value. The active pack (the one of the Voice packs tab) may have a message KEY with the words in its language: it replaces the English.
        // The base pack says which English text the key stands for: when the text of the mission is another one (the key is stale, or the base pack is older than the
        // mission) the English of the mission is spoken, never a wrong sentence. A pack without the key: the English as the mission sends it, and one log line per key.
        static string ExpandText(string spec, string rest, List<string> notes)
        {
            string key = spec.Trim();
            string[] parts = rest.Split(new string[] { "//" }, StringSplitOptions.None);
            string english = parts[0].Trim();
            string[] values = new string[parts.Length - 1];
            Array.Copy(parts, 1, values, 0, values.Length);
            VoicePack pack = bridgePack.Length > 0 ? VoicePacks.Find(bridgePack) : null;
            PackMessage msg = null;
            if (pack != null && key.Length > 0) pack.Messages.TryGetValue(key, out msg);
            string words = msg != null ? FirstText(msg) : "";
            if (words.Length == 0)
            {
                if (pack != null && warned.Add("text:" + key)) Ctx.Log("[UNTRANSLATED] " + key + " is not in the pack " + pack.Name + ": spoken in English as the mission sends it");
                return Literal(english);
            }
            VoicePack reference = PackCheck.Reference();
            PackMessage basis;
            if (reference != null && reference.Messages.TryGetValue(key, out basis))
            {
                string was = FirstText(basis);
                if (was.Length > 0 && was.IndexOf('{') < 0 && SameKey(was) != SameKey(english))
                {
                    if (warned.Add("stale:" + key)) Ctx.Log("[UNTRANSLATED] " + key + ": the text of the mission is not the one of the base pack " + reference.Name + " (version " + reference.Version + "), spoken in English: update the pack");
                    return Literal(english);
                }
            }
            return Fill(words, values, pack, notes);
        }

        // ---- say ----
        // the pack of the Voice packs tab (the one the player chose) comes first, then the pack the mission declares, then any other
        static PackMessage FindMessage(string key, out VoicePack pack)
        {
            pack = null;
            VoicePack d = declared.Length > 0 ? VoicePacks.Find(declared) : null;
            VoicePack b = bridgePack.Length > 0 ? VoicePacks.Find(bridgePack) : null;
            PackMessage m;
            if (b != null && b.Messages.TryGetValue(key, out m)) { pack = b; return m; }
            if (d != null && d != b && d.Messages.TryGetValue(key, out m)) { pack = d; return m; }
            foreach (VoicePack p in VoicePacks.Packs)
            {
                if (!p.Usable || p == d || p == b) continue;
                if (p.Messages.TryGetValue(key, out m)) { pack = p; return m; }
            }
            return null;
        }

        static string Expand(string spec, string args, string before, List<string> notes, out string wavFile)
        {
            wavFile = null;
            string key = spec; int forced = 0;
            int c = spec.IndexOf(':');
            if (c >= 0)
            {
                key = spec.Substring(0, c).Trim();
                int.TryParse(spec.Substring(c + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out forced);
            }
            key = key.Trim();
            VoicePack pack = null;
            PackMessage msg = null;
            if (key.Length > 0) msg = FindMessage(key, out pack);
            if (msg == null)
            {
                if (notes != null) notes.Add("voice pack message '" + key + "' not found" + (declared.Length > 0 ? " (pack " + declared + ")" : "") + ": nothing spoken");
                return "";
            }
            if (bridgePack.Length > 0 && !string.Equals(pack.Name, bridgePack, StringComparison.OrdinalIgnoreCase) && warned.Add("say:" + key))
                Ctx.Log("[UNTRANSLATED] " + key + " is not in the pack " + bridgePack + ": the text of the pack " + pack.Name + " is spoken");
            return Compose(msg, pack, args, before, forced, notes, out wavFile);
        }

        // the text for the tag parser of one message: actor, radio, the chosen option with its placeholders filled
        static string Compose(PackMessage msg, VoicePack pack, string args, string before, int forced, List<string> notes, out string wavFile)
        {
            wavFile = null;
            if (msg.Options.Count == 0)
            {
                if (msg.Wav.Length > 0) wavFile = msg.Vol < 100 ? msg.Wav + "|" + msg.Vol : msg.Wav;       // "name|vol": SplitWav
                return "";
            }
            int index = forced >= 1 && forced <= msg.Options.Count ? forced - 1 : Pick(msg);
            string[] parts = args.Length > 0 ? args.Split(new string[] { "//" }, StringSplitOptions.None) : new string[0];
            for (int pi = 0; pi < parts.Length; pi++)
                if (Regex.IsMatch(parts[pi].Trim(), "^[A-Za-z0-9_]+_[Nn]ame$")) { notes.Add("the name " + parts[pi].Trim() + " arrived unresolved (the simulator did not replace it in a spoken text): said as the one on the tablet"); parts[pi] = "the one shown on the tablet"; }
            string body = Fill(msg.Options[index], parts, pack, notes);
            if (msg.Options[index].Length == 0)
            {
                Ctx.Log("[SAY] " + msg.Key + " -> option " + (index + 1) + " of " + msg.Options.Count + " is silent (" + pack.Name + " v" + pack.Version + ")");
                return "";
            }
            int actor = msg.Actor;
            if (msg.OptionSex.Count == msg.Options.Count && msg.OptionSex.Exists(delegate (string s) { return s.Length > 0; }))
            {
                string sx = msg.OptionSex[index];
                if (sx.Length == 0) sx = TechSexOf(msg);
                actor = sx == "f" ? 7 : 6;      // a message with options for the two technicians is spoken by the technician of the scene: Technician male (6) or female (7)
            }
            else if ((actor == 6 || actor == 7) && techSex >= 0)
                actor = techSex == 1 ? 7 : 6;       // a technician message with no options for the two (a translated pack made before the sexes): the voice still follows the technician of the scene
            if (actor >= 1 && actor <= 5 && crewExist != 0 && (crewExist & (1 << (actor - 1))) == 0)       // a seat the crew of this flight does not have (3 crew: no HEMS1, no HEMS2)
            {
                int stand = StandIn(actor);
                if (stand == 0)
                {
                    Ctx.Log("[SAY] " + msg.Key + " not said: " + Actors.Names[actor - 1] + " is not in this crew and nobody else can say it");
                    return "";
                }
                Ctx.Log("[SAY] " + msg.Key + ": " + Actors.Names[actor - 1] + " is not in this crew, " + Actors.Names[stand - 1] + " says it");
                actor = stand;
            }
            if (actor >= 1 && actor <= 5 && (muteMask & (1 << (actor - 1))) != 0)       // the dead and the badly hurt do not speak
            {
                Ctx.Log("[SAY] " + msg.Key + " not said: " + Actors.Names[actor - 1] + " cannot speak (L:HMC_CREW_MUTE)");
                return "";
            }
            if (CrewAway && DispatchLine(msg.Key, actor))
            {
                Ctx.Log("[SAY] " + msg.Key + " not said: nobody of the crew is aboard (tablet sound only)");
                return "";
            }
            StringBuilder tags = new StringBuilder();
            if (!ActorTagRx.IsMatch(before)) tags.Append("<actor").Append(actor).Append('>');
            if (!RadioTagRx.IsMatch(before))
            {
                Hear h;
                switch (msg.Radio)
                {
                    case "on": h = Hear.Radio; break;
                    case "off": h = Hear.Voice; break;
                    case "ics": h = Hear.Ics; break;
                    case "ext": h = !ctxValid || RadioOn || atBase ? Hear.External : Hear.Silent; break;
                    case "ext-near": h = Hear.External; break;
                    default: h = (msg.ToTech || ToTechKeys.Contains(msg.Key)) ? DecideToTech(actor) : Decide(actor); break;
                }
                if (h == Hear.Silent)
                {
                    Ctx.Log("[SAY] " + msg.Key + " not heard: " + SilentReason(actor));
                    return "";
                }
                tags.Append(TagsFor(h));
            }
            Ctx.Log("[SAY] " + msg.Key + " -> option " + (index + 1) + " of " + msg.Options.Count + " (" + pack.Name + " v" + pack.Version + ")" + (actor == 6 || actor == 7 ? ", " + Actors.Names[actor - 1] + " (actor " + actor + ")" : ""));
            return tags.ToString() + body;
        }

        // A message of a seat that this crew does not have (L:HMC_CREW_EXIST published, the bit missing) is said by a crew member who is there, with the voice of
        // that member (user, 2026-10-14: the Daikan report, PAX1 lines with 3 crew). Rear seats go to the rear crew first, then the cockpit; a member who cannot
        // speak (L:HMC_CREW_MUTE) is skipped; 0 when nobody can. Only the texts of the packs: the messages that are a wav file (no options) are not touched.
        static readonly int[][] StandIns = { null, new int[] { }, new int[] { 1, 3, 4, 5 }, new int[] { 4, 5, 2, 1 }, new int[] { 3, 5, 2, 1 }, new int[] { 4, 3, 2, 1 } };

        static int StandIn(int actor)
        {
            foreach (int a in StandIns[actor])
                if ((crewExist & (1 << (a - 1))) != 0 && (muteMask & (1 << (a - 1))) == 0) return a;
            return 0;
        }

        // ---- how a line is heard (the rules of the user, 2026-10-11) ----
        // None: no context from the mission, the text gets no tag. Voice: spoken in the air at the normal level; VoiceFar: heard very low (30 to 100 m);
        // Ics: the wired intercom of the helicopter; Radio: a radio call (the crew radios, so also with the battery off); External: a call on an external
        // radio (the dispatcher, the hospital); Silent: not heard at all.
        internal enum Hear { None, Voice, VoiceFar, Ics, Radio, External, Silent }

        // the radio works with the battery master on (the aircraft variable); when it is not read, the opposite of L:HMC_HELI_OFF
        static bool RadioOn { get { return radioVar >= 0 ? radioVar == 1 : !heliOff; } }

        // From the context the mission publishes (L:HMC_CTX_VALID, L:HMC_CREW_PRESENT, L:HMC_CREW_EXIST, L:HMC_AT_BASE, L:HMC_ZONE_*) and the battery of the aircraft:
        // - the dispatcher: external radio call when the battery is on, or with the helicopter at the base (the crew radios reach it there); otherwise nothing;
        // - a crew member (actors 1-5; they carry radios, so the battery does not matter): radio when the call is between inside and outside, the intercom
        //   (battery on) or the voice (battery off) when everybody is aboard, the voice when everybody is outside;
        // - the technician: the intercom when next to the helicopter, with the pilot aboard and the battery on; otherwise the voice;
        // - technician, ground unit, patient (no radio): by voice; heard from inside at the normal level up to 30 m, very low up to 100 m, not beyond;
        //   when nobody is aboard everybody is outside together: normal level. The distance is the zone that the mission publishes (0 = unknown: normal).
        static Hear Decide(int a)
        {
            if (!ctxValid) return Hear.None;
            if (a == 8) return RadioOn || atBase ? Hear.External : Hear.Silent;
            if (a >= 1 && a <= 5)
            {
                int bit = 1 << (a - 1);
                if ((crewMask & bit) != 0)
                {
                    if (crewExist == 0) return Hear.Radio;                   // the mission does not say who is outside: the old rule
                    return (crewExist & ~crewMask & ~bit) != 0 ? Hear.Radio : (RadioOn ? Hear.Ics : Hear.Voice);
                }
                return (crewMask & ~bit) != 0 ? Hear.Radio : Hear.Voice;
            }
            int zone;
            if (a == 6 || a == 7)
            {
                if (zoneTech == RadioRules.ZoneNear && (crewMask & 1) != 0 && RadioOn) return Hear.Ics;
                zone = zoneTech;
            }
            else if (a == 9) zone = zoneGround;
            else if (a == 10) zone = zonePatient;
            else if (a == Actors.HeliRescuer) return Hear.Voice;      // the heli rescuers talk next to the helicopter (their radio lines say radio="on"): by voice, no zone of the mission (zoneGround measures the ground unit, not them)
            else return Hear.None;
            if (crewMask == 0) return Hear.Voice;
            if (zone >= RadioRules.ZoneOut) return Hear.Silent;
            return zone == RadioRules.ZoneFar ? Hear.VoiceFar : Hear.Voice;
        }

        // a crew member who talks to the technician of the scene: the technician is next to the helicopter, so it is the intercom when the speaker is aboard (battery on)
        // and the voice otherwise, at the distance of the technician
        // the crew lines to the technician in the base pack: a translated pack made before to="tech" existed gets the same treatment
        static readonly HashSet<string> ToTechKeys = new HashSet<string>(new string[] { "CPL.23", "PAX1.37", "PAX1.38", "PAX2.2", "PAX2.3", "PLT.5", "PLT.9", "PLT.21", "PLT.26" });

        static Hear DecideToTech(int a)
        {
            if (!ctxValid) return Hear.None;
            if (a < 1 || a > 5) return Decide(a);
            int bit = 1 << (a - 1);
            if (zoneTech == RadioRules.ZoneNear && (crewMask & bit) != 0 && RadioOn) return Hear.Ics;
            if (crewMask == 0) return Hear.Voice;
            if (zoneTech >= RadioRules.ZoneOut) return Hear.Silent;
            return zoneTech == RadioRules.ZoneFar ? Hear.VoiceFar : Hear.Voice;
        }

        static string TagsFor(Hear h)
        {
            switch (h)
            {
                case Hear.Ics: return "<ics>";
                case Hear.Radio: return "<radio>";
                case Hear.External: return "<radio:ext>";
                case Hear.Voice: return "<radio:off>";
                case Hear.VoiceFar: return "<radio:off><zone:far>";
                default: return "";
            }
        }

        // Nobody of the crew is aboard (the pilot, the user, may be): a new dispatch arrives only as the sound of the tablet (user, 2026-10-16). The dispatcher and the
        // lines of the decision about the dispatch (DSA.*, DSP.RQ, DSP.ANS.*) are not said; they come back as soon as a crew member is aboard again.
        static bool CrewAway { get { return ctxValid && crewExist != 0 && (crewMask & ~1) == 0; } }

        static bool DispatchLine(string key, int actor)
        {
            if (key.StartsWith("DSP.NAME", StringComparison.Ordinal)) return false;
            return actor == 8 || key.StartsWith("DSA.", StringComparison.Ordinal) || key.StartsWith("DSP.", StringComparison.Ordinal);
        }

        static string SilentReason(int actor)
        {
            if (actor == 8) return "the radio is off (battery master off) and the helicopter is not at the base";
            if (actor >= 6) return "more than 100 m from the helicopter";
            return "no listener";
        }

        // the dispatcher of a plain speak text (the reading of a dispatch) follows the same rule: an external radio call, or nothing with the radio off;
        // the other speakers of a plain text keep the tags the mission wrote
        public static void ApplyDelivery(List<Segment> segs)
        {
            lock (sync)
            {
                for (int i = 0; i < segs.Count; i++)
                {
                    Segment s = segs[i];
                    if (s.Actor != 8 || Decide(8) == Hear.None) continue;
                    if (CrewAway)
                    {
                        Ctx.Log("[SAY] dispatcher text not said: nobody of the crew is aboard (tablet sound only)");
                        segs.RemoveAt(i--);
                        continue;
                    }
                    if (Decide(8) == Hear.Silent)
                    {
                        Ctx.Log("[SAY] dispatcher text not heard: " + SilentReason(8));
                        segs.RemoveAt(i--);
                        continue;
                    }
                    s.Radio = 1; s.Fx = "ext";
                }
            }
        }
        // the technician of the scene (L:HMC_TECH_SEX); when the mission does not say, the one the message was written for (actor 7: female)
        static string TechSexOf(PackMessage m)
        {
            if (techSex == 1) return "f";
            if (techSex == 0) return "m";
            return m.Actor == 7 ? "f" : "m";
        }

        // options with a sex are for that technician only; the options without one are for both
        static int Pick(PackMessage m)
        {
            List<int> eligible = null;
            if (m.OptionSex.Count == m.Options.Count && m.OptionSex.Exists(delegate (string s) { return s.Length > 0; }))
            {
                string want = TechSexOf(m);
                eligible = new List<int>();
                for (int i = 0; i < m.Options.Count; i++) if (m.OptionSex[i].Length == 0 || m.OptionSex[i] == want) eligible.Add(i);
                if (eligible.Count == 0) eligible = null;
            }
            int count = eligible != null ? eligible.Count : m.Options.Count;
            if (chatMax < count) count = chatMax;      // the highest option number the setting CREW CHATTER lets through: the professional options have the low numbers, a futile message starts with two silent ones
            int pos = PickPosition(m, count);
            return eligible != null ? eligible[pos] : pos;
        }

        static int PickPosition(PackMessage m, int n)
        {
            if (n == 1) return 0;
            int k;
            if ((coherent || m.Futile) && m.Group.Length > 0)
            {
                if (string.Equals(m.Group, lastGroup, StringComparison.OrdinalIgnoreCase) && (DateTime.UtcNow - lastGroupTime).TotalSeconds < 180)
                    k = Math.Min(lastGroupOption, n - 1);
                else
                    k = rng.Next(n);
                lastGroup = m.Group; lastGroupTime = DateTime.UtcNow; lastGroupOption = k;
            }
            else
            {
                int last;
                if (!lastOption.TryGetValue(m.Key, out last)) last = -1;
                k = rng.Next(n);
                if (k == last) k = (k + 1 + rng.Next(n - 1)) % n;     // never the same option twice in a row
            }
            lastOption[m.Key] = k;
            return k;
        }

        // {list:N} and {be:N}: N is an argument made of the letters A (ambulance), F (fire engine), P (police). The words come from the <names> of the pack
        // (VEH_A, VEH_F, VEH_P, AND, IS, ARE) so that a pack of another language can say them its own way.
        static string Helper(string kind, string letters, VoicePack pack, List<string> notes)
        {
            if (kind == "hr")        // {hr:N}: the argument is 1 or 2, the words come from the names HR_1 and HR_2 of the pack
            {
                string key = letters.Trim() == "2" ? "HR_2" : "HR_1", w;
                if (pack != null && pack.Names.TryGetValue(key, out w) && w.Length > 0) return w;
                return key == "HR_2" ? "two heli rescuers" : "one heli rescuer";
            }
            if (kind == "place")     // {place:N}: the argument is H (the hospital), B (the base) or M (the meeting point): the names PL_H, PL_B and PL_M
            {
                string l = letters.Trim().ToUpperInvariant();
                string key = l == "H" ? "PL_H" : (l == "B" ? "PL_B" : "PL_M"), w;
                if (pack != null && pack.Names.TryGetValue(key, out w) && w.Length > 0) return w;
                return key == "PL_H" ? "the hospital" : (key == "PL_B" ? "the base" : "the meeting point");
            }
            List<string> words = new List<string>();
            foreach (char c in letters.ToUpperInvariant())
            {
                string role = c == 'A' ? "VEH_A" : (c == 'F' ? "VEH_F" : (c == 'P' ? "VEH_P" : null));
                if (role == null) continue;
                string w;
                if (pack == null || !pack.Names.TryGetValue(role, out w) || w.Length == 0) w = c == 'A' ? "ambulance" : (c == 'F' ? "fire engine" : "police");
                words.Add(w);
            }
            if (kind == "be")
            {
                string w;
                string key = words.Count == 1 ? "IS" : "ARE";
                if (pack != null && pack.Names.TryGetValue(key, out w) && w.Length > 0) return w;
                return words.Count == 1 ? "is" : "are";
            }
            if (kind == "list")
            {
                string and = "and";
                if (pack != null && pack.Names.TryGetValue("AND", out and) && and.Length > 0) { } else and = "and";
                if (words.Count == 0) return "";
                if (words.Count == 1) return words[0];
                return string.Join(", ", words.GetRange(0, words.Count - 1).ToArray()) + " " + and + " " + words[words.Count - 1];
            }
            if (notes != null) notes.Add("unknown placeholder helper {" + kind + ":..}");
            return "";
        }
        // PLT CPL PAX1 PAX2 PAX3 TECHM TECHF; TECH is the technician of the scene (male or female as the mission says); FRA and SARA are the old names of TECHM and TECHF
        static string RoleOf(string name)
        {
            if (string.Equals(name, "TECH", StringComparison.OrdinalIgnoreCase)) return techSex == 1 ? "TECHF" : "TECHM";
            if (string.Equals(name, "FRA", StringComparison.OrdinalIgnoreCase)) return "TECHM";
            if (string.Equals(name, "SARA", StringComparison.OrdinalIgnoreCase)) return "TECHF";
            foreach (string r in Roles) if (string.Equals(r, name, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }

        static string JobWord(string role, VoicePack pack)
        {
            string w;
            if (pack != null && pack.Names.TryGetValue(role, out w) && w.Length > 0) return w;
            return RoleDefaults[Array.IndexOf(Roles, role)];
        }

        static string PersonalName(string role, VoicePack pack)
        {
            string w;
            if (missionNames.TryGetValue(role, out w) && w.Length > 0) return w;
            return JobWord(role, pack);
        }

        static string Fill(string option, string[] args, VoicePack pack, List<string> notes)
        {
            return HolderRx.Replace(option, delegate (Match m)
            {
                string name = m.Groups[1].Value;
                bool personal = name.StartsWith("@", StringComparison.Ordinal);
                if (personal) name = name.Substring(1);
                int i;
                if (string.Equals(name, "DSP", StringComparison.OrdinalIgnoreCase)) return DispatcherWords(pack);        // the dispatcher of the CARLS group (<dsp:INDEX:CITY>)
                if (string.Equals(name, "HEMS", StringComparison.OrdinalIgnoreCase)) return CallSignWords();            // the call sign of the helicopter
                string roleKey = RoleOf(name);
                if (roleKey != null) return personal ? PersonalName(roleKey, pack) : JobWord(roleKey, pack);     // {PLT} the job word, {@PLT} the name of the shift
                if (personal)
                {
                    if (notes != null) notes.Add("unknown placeholder {@" + name + "}: only the roles PLT CPL PAX1 PAX2 PAX3 TECH TECHM TECHF have a name");
                    return "";
                }
                int colon = name.IndexOf(':');
                if (colon > 0)
                {
                    int ai;
                    string kind = name.Substring(0, colon).ToLowerInvariant();
                    if (!int.TryParse(name.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out ai) || ai < 0 || ai >= args.Length)
                    {
                        if (notes != null) notes.Add("the message needs argument {" + ai + "} for {" + name + "} but the mission sent " + args.Length);
                        return "";
                    }
                    return Helper(kind, args[ai].Trim(), pack, notes);
                }
                if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                {
                    if (i >= 0 && i < args.Length) return Literal(args[i].Trim());
                    if (notes != null) notes.Add("the message needs argument {" + i + "} but the mission sent " + args.Length);
                    return "";
                }
                string v;
                if (pack != null && pack.Names.TryGetValue(name, out v)) return v;
                if (notes != null) notes.Add("unknown placeholder {" + name + "}");
                return "";
            });
        }
    }
}
