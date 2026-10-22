// R&E Audio Manager integration: safe pack discovery, counter edges and shared output.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using RandomEverywhereInstaller.Audio;

namespace HPGMissionCompanion
{
    internal enum RecordedChannel { Independent, Crew, Checklist }

    internal sealed class RecordedMapping
    {
        public int Id;
        public string Variable;
        public string DeclaredFiles;
        public RecordedChannel Channel;
        public List<string> Files = new List<string>();
        public List<string> Problems = new List<string>();
    }

    internal sealed class RecordedPack
    {
        public string Name;
        public string File;
        public List<RecordedMapping> Mappings = new List<RecordedMapping>();
        public List<RecordedMapping> AllMappings = new List<RecordedMapping>();
        public List<string> Warnings = new List<string>();
        public override string ToString() { return Name; }
    }

    internal static class RecordedAudio
    {
        static readonly Random random = new Random();
        static readonly object sync = new object();
        static RecordedPlaybackEngine player;
        static Timer previewCleanup;
        static bool sessionActive;
        static int pendingStarts;
        public static int Device = -1;
        public static List<RecordedPack> Packs = new List<RecordedPack>();
        public static RecordedPack Selected;
        public static readonly HashSet<string> Muted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static int Volume = 80;
        public static bool MasterMuted;
        public static string LastPlayed = "";
        public static List<string> DiscoveryWarnings = new List<string>();

        public static void Scan(string root, RecordedPackRegistry registry = null)
        {
            var packs = new List<RecordedPack>();
            var warnings = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hidden = new HashSet<string>(registry == null ? new string[0] : registry.Hidden.ToArray(), StringComparer.OrdinalIgnoreCase);
            Action<string, bool> read = (file, explicitFile) =>
            {
                try
                {
                    file = Path.GetFullPath(file);
                    if (hidden.Contains(file) || !seen.Add(file)) return;
                    var pack = Load(file);
                    if (pack != null) packs.Add(pack);
                    else if (explicitFile) warnings.Add(Path.GetFileName(file) + ": not a recorded SoundList pack.");
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is XmlException || ex is ArgumentException)
                {
                    if (warnings.Count < 100) warnings.Add(file + ": " + ex.Message);
                }
            };
            var pending = new Queue<string>();
            if (Directory.Exists(root) && !RuntimeFileSafety.HasReparsePoint(root)) pending.Enqueue(Path.GetFullPath(root));
            while (pending.Count > 0)
            {
                var directory = pending.Dequeue();
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (var file in Directory.EnumerateFiles(directory, "*.xml")) read(file, false);
                    foreach (var child in Directory.EnumerateDirectories(directory)) pending.Enqueue(child);
                }
                catch (IOException ex) { if (warnings.Count < 100) warnings.Add(directory + ": " + ex.Message); }
                catch (UnauthorizedAccessException ex) { if (warnings.Count < 100) warnings.Add(directory + ": " + ex.Message); }
            }
            if (registry != null) foreach (var file in registry.Files) read(file, true);
            Packs = packs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            DiscoveryWarnings = warnings;
            Selected = null;
        }

        public static RecordedPack Load(string file)
        {
            file = Path.GetFullPath(file);
            if (RuntimeFileSafety.HasReparsePoint(file)) throw new InvalidDataException("Recorded pack paths must not follow links.");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
            XDocument doc;
            using (var reader = XmlReader.Create(file, settings))
            {
                // Inspect only the root for unrelated XML; never parse a TTS pack here.
                reader.MoveToContent();
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "SoundList" || reader.NamespaceURI.Length != 0) return null;
                doc = XDocument.Load(reader);
            }
            var root = doc.Root;
            if (root == null || root.Name.LocalName != "SoundList") return null;
            var resolver = new RecordedAudioFileResolver(file);
            var pack = new RecordedPack { Name = (string)root.Attribute("category") ?? Path.GetFileName(Path.GetDirectoryName(file)), File = file };
            var defaultChannel = ParseChannel((string)root.Attribute("Channel"), pack.Warnings);
            foreach (var sound in root.Elements("Sound"))
            {
                var variable = ((string)sound.Attribute("LocalVar") ?? "").Trim();
                var names = ((string)sound.Attribute("FileName") ?? "").Split(',');
                if (variable.Length == 0) continue;
                if (!variable.Contains(":")) variable = "L:" + variable;
                var channelText = (string)sound.Attribute("Channel");
                var mapping = new RecordedMapping { Id = pack.AllMappings.Count, Variable = variable, DeclaredFiles = (string)sound.Attribute("FileName") ?? "", Channel = channelText == null ? defaultChannel : ParseChannel(channelText, pack.Warnings) };
                foreach (var name in names)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    try
                    {
                        var found = resolver.Resolve(name);
                        if (found != null)
                        {
                            // Repeated declarations retain the author's random-selection weighting.
                            mapping.Files.Add(found);
                        }
                        else mapping.Problems.Add("Audio file missing or linked: " + name);
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is NotSupportedException)
                    { mapping.Problems.Add(ex.Message); }
                }
                if (string.IsNullOrWhiteSpace(mapping.DeclaredFiles)) mapping.Problems.Add("No audio filename declared.");
                pack.Warnings.AddRange(mapping.Problems);
                pack.AllMappings.Add(mapping);
                if (mapping.Files.Count > 0) pack.Mappings.Add(mapping);
            }
            return pack;
        }

        public static void OnWatch(string name, double oldValue, double value, bool baseline)
        {
            if (Mixer.IsMixerName(name)) { Mixer.OnWatch(name, value); return; }
            foreach (var mapping in TriggeredMappings(Selected, name, oldValue, value, baseline, Muted)) Play(mapping);
        }

        internal static IEnumerable<RecordedMapping> TriggeredMappings(RecordedPack pack, string name, double oldValue, double value, bool baseline, ISet<string> muted)
        {
            if (pack == null || baseline || double.IsNaN(oldValue) || double.IsInfinity(oldValue) || double.IsNaN(value) || double.IsInfinity(value) || value <= oldValue)
                return Enumerable.Empty<RecordedMapping>();
            return pack.Mappings.Where(m => m.Variable.Equals(name, StringComparison.OrdinalIgnoreCase) && !muted.Contains(m.Variable));
        }

        // Untagged original packs keep standalone master-only gain.
        internal static RecordedChannel ParseChannel(string value, List<string> warnings)
        {
            value = value == null ? null : value.Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Equals("independent", StringComparison.OrdinalIgnoreCase))
                return RecordedChannel.Independent;
            if (value.Equals("crew", StringComparison.OrdinalIgnoreCase)) return RecordedChannel.Crew;
            if (value.Equals("checklist", StringComparison.OrdinalIgnoreCase)) return RecordedChannel.Checklist;
            warnings.Add("Unknown recorded channel '" + value + "'; master-only playback is used.");
            return RecordedChannel.Independent;
        }

        internal static float ChannelGain(RecordedChannel channel, bool preview)
        {
            if (preview || channel == RecordedChannel.Independent) return 1f;
            return (float)Mixer.MissionFactor(channel == RecordedChannel.Checklist ? Actors.Checklist : 1);
        }

        public static void Start()
        {
            lock (sync)
            {
                if (player == null) player = new RecordedPlaybackEngine(Device, MasterMuted ? 0 : Volume);
                sessionActive = true;
                if (previewCleanup != null) { previewCleanup.Dispose(); previewCleanup = null; }
            }
        }

        public static void SetVolume(int volume)
        {
            lock (sync)
            {
                Volume = Math.Max(0, Math.Min(100, volume));
                if (player != null) player.SetVolume(MasterMuted ? 0 : Volume);
            }
        }

        public static void SetMuted(bool muted)
        {
            lock (sync)
            {
                MasterMuted = muted;
                if (player != null) player.SetVolume(muted ? 0 : Volume);
            }
        }

        // The standalone manager prioritizes packtest in either variable or filename.
        internal static RecordedMapping FindTestMapping(RecordedPack pack)
        {
            if (pack == null) return null;
            return pack.Mappings.FirstOrDefault(m => m.Variable.EndsWith(".packtest", StringComparison.OrdinalIgnoreCase) ||
                (m.DeclaredFiles ?? "").IndexOf("packtest", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? pack.Mappings.FirstOrDefault(m => m.Variable.IndexOf("packtest", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? pack.Mappings.FirstOrDefault();
        }

        internal static RecordedMapping ResolvePreview(string mode, RecordedPack pack, int id)
        {
            if (mode != "recorded") throw new InvalidOperationException("Recorded previews are unavailable in synthetic mode.");
            var mapping = pack == null ? null : pack.Mappings.FirstOrDefault(m => m.Id == id);
            if (mapping == null) throw new InvalidDataException("This recorded mapping has no available audio file.");
            return mapping;
        }

        public static void Play(RecordedMapping mapping, bool preview = false)
        {
            string path;
            RecordedPlaybackEngine session;
            lock (sync)
            {
                if (mapping.Files.Count == 0) return;
                path = mapping.Files[random.Next(mapping.Files.Count)];
                if (player == null) player = new RecordedPlaybackEngine(Device, MasterMuted ? 0 : Volume);
                session = player;
                pendingStarts++;
                if (preview && !sessionActive && previewCleanup == null)
                    previewCleanup = new Timer(_ => ReleaseIdlePreview(), null, 250, 250);
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (RuntimeFileSafety.HasReparsePoint(path)) throw new InvalidDataException("The recorded file now follows a link. Reload the pack.");
                    if (session.PlayFile(path, () => ChannelGain(mapping.Channel, preview)))
                    {
                        LastPlayed = Path.GetFileName(path);
                        Ctx.Log("[PLAY] " + Path.GetFileName(path) + " channel=" + mapping.Channel + (preview ? " preview" : ""));
                    }
                }
                catch (Exception ex) { Ctx.Log("[ERROR] Recorded playback: " + ex.Message); }
                finally { lock (sync) pendingStarts--; }
            });
        }

        // Never dispose an output from its own audio callback.
        internal static void ReleaseIdlePreview()
        {
            RecordedPlaybackEngine previous = null;
            lock (sync)
            {
                if (sessionActive || pendingStarts != 0 || player == null || !player.IsIdle) return;
                previous = player; player = null;
                if (previewCleanup != null) { previewCleanup.Dispose(); previewCleanup = null; }
            }
            try { previous.Dispose(); }
            catch (Exception ex) { if (Ctx.Log != null) Ctx.Log("[WARN] Recorded preview cleanup: " + ex.Message); }
        }

        public static void Stop()
        {
            RecordedPlaybackEngine previous;
            lock (sync)
            {
                sessionActive = false;
                if (previewCleanup != null) { previewCleanup.Dispose(); previewCleanup = null; }
                previous = player; player = null;
            }
            if (previous != null) previous.Dispose();
        }
    }
}
