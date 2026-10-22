// HPG Mission Companion - speech engines: Windows (OneCore) voices and Piper, plus the player queue.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Text;
using System.Threading;

namespace HPGMissionCompanion
{
    // The Windows.Media.SpeechSynthesis classes are used through reflection and dynamic: no Windows SDK metadata is needed to build.
    internal static class WinRt
    {
        public static readonly Type SynthType = Type.GetType("Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows.Media.SpeechSynthesis, ContentType=WindowsRuntime");

        public static System.Collections.IEnumerable AllVoices()
        {
            return (System.Collections.IEnumerable)SynthType.GetProperty("AllVoices").GetValue(null, null);
        }

        static readonly System.Reflection.MethodInfo AsStreamForRead = FindAsStreamForRead();

        static System.Reflection.MethodInfo FindAsStreamForRead()
        {
            System.Reflection.Assembly asm = System.Reflection.Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
            foreach (System.Reflection.MethodInfo m in asm.GetType("System.IO.WindowsRuntimeStreamExtensions").GetMethods())
                if (m.Name == "AsStreamForRead" && m.GetParameters().Length == 1) return m;
            throw new InvalidOperationException("AsStreamForRead not found");
        }

        static readonly Type AsyncInfoType = Type.GetType("Windows.Foundation.IAsyncInfo, Windows.Foundation, ContentType=WindowsRuntime");
        static readonly Type AsyncOperationType = Type.GetType("Windows.Foundation.IAsyncOperation`1, Windows.Foundation, ContentType=WindowsRuntime");
        static readonly Type StreamType = Type.GetType("Windows.Media.SpeechSynthesis.SpeechSynthesisStream, Windows.Media.SpeechSynthesis, ContentType=WindowsRuntime");

        static dynamic synth;
        static string synthId;
        static double synthRate;

        public static byte[] Render(string winId, double rate, string text)
        {
            if (synth == null || synthId != winId || synthRate != rate)
            {
                dynamic s = Activator.CreateInstance(SynthType);
                foreach (dynamic v in AllVoices())
                {
                    if ((string)v.Id == winId) { s.Voice = v; break; }
                }
                try { s.Options.SpeakingRate = rate; } catch (Exception) { }
                synth = s; synthId = winId; synthRate = rate;
            }
            object op = synth.SynthesizeTextToStreamAsync(text);
            System.Reflection.PropertyInfo statusProperty = AsyncInfoType.GetProperty("Status");
            int status;
            do
            {
                status = Convert.ToInt32(statusProperty.GetValue(op, null));
                if (status == 0) Thread.Sleep(5);
            } while (status == 0);
            if (status != 1) throw new InvalidOperationException("speech synthesis failed (status " + status + ")");
            object stream = AsyncOperationType.MakeGenericType(StreamType).GetMethod("GetResults").Invoke(op, null);
            Stream st = (Stream)AsStreamForRead.Invoke(null, new object[] { stream });
            using (st)
            using (MemoryStream copy = new MemoryStream())
            {
                st.CopyTo(copy);
                return copy.ToArray();
            }
        }
    }

    // One piper.exe kept running per voice, speaker and speed: the model is loaded once, then every line of text
    // written to its standard input answers with the path of a wav file on its standard output.
    internal sealed class PiperHost
    {
        public string Key;
        public DateTime LastUsed = DateTime.UtcNow;
        readonly Process proc;
        readonly string outDir;
        readonly BlockingCollection<string> outLines = new BlockingCollection<string>();
        readonly Queue<string> errLines = new Queue<string>();
        readonly StreamWriter input;

        public PiperHost(string key, VoiceInfo v, double lengthScale)
        {
            Key = key;
            outDir = Path.Combine(Path.GetTempPath(), "HPGMissionCompanion_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            StringBuilder args = new StringBuilder();
            args.Append("--model \"").Append(v.ModelPath).Append("\" --config \"").Append(v.ConfigPath).Append("\"");
            args.Append(" --output_dir \"").Append(outDir).Append("\"");
            args.Append(" --length_scale ").Append(lengthScale.ToString("0.00", CultureInfo.InvariantCulture));
            if (v.Speaker >= 0) args.Append(" --speaker ").Append(v.Speaker.ToString(CultureInfo.InvariantCulture));
            ProcessStartInfo psi = new ProcessStartInfo(Ctx.PiperExe, args.ToString());
            psi.WorkingDirectory = Ctx.PiperDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            proc = new Process();
            proc.StartInfo = psi;
            proc.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) outLines.Add(e.Data); };
            proc.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                lock (errLines) { errLines.Enqueue(e.Data); while (errLines.Count > 12) errLines.Dequeue(); }
            };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            input = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false));
            input.NewLine = "\n";
            input.AutoFlush = true;
        }

        public bool Alive { get { try { return !proc.HasExited; } catch (Exception) { return false; } } }

        string LastErrors()
        {
            lock (errLines) { return string.Join(" | ", errLines.ToArray()); }
        }

        public byte[] Synthesize(string text, int timeoutMs)
        {
            LastUsed = DateTime.UtcNow;
            string line;
            while (outLines.TryTake(out line)) { }
            input.WriteLine(text.Replace("\r", " ").Replace("\n", " "));
            string path = null;
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (outLines.TryTake(out line, 200))
                {
                    if (line.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) { path = line.Trim(); break; }
                }
                else if (!Alive) break;
            }
            if (path == null) throw new InvalidOperationException("Piper gave no audio" + (Alive ? " (timeout)" : " (process ended)") + ": " + LastErrors());
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    try { File.Delete(path); } catch (Exception) { }
                    return bytes;
                }
                catch (IOException) { Thread.Sleep(50); }
            }
            throw new IOException("cannot read " + path);
        }

        public void Stop()
        {
            try { input.Close(); } catch (Exception) { }
            try { if (!proc.HasExited) proc.Kill(); } catch (Exception) { }
            try { Directory.Delete(outDir, true); } catch (Exception) { }
        }
    }

    internal static class PiperEngine
    {
        const int MaxHosts = 4;
        static readonly List<PiperHost> pool = new List<PiperHost>();
        static readonly object poolLock = new object();

        public static byte[] Render(VoiceInfo v, double speed, string text)
        {
            double ls = Math.Round(v.LengthScale / Math.Max(0.5, speed), 2);
            string key = v.ModelPath + "|" + v.Speaker + "|" + ls.ToString("0.00", CultureInfo.InvariantCulture);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                PiperHost host = Get(key, v, ls);
                try { return host.Synthesize(text, 30000); }
                catch (Exception ex)
                {
                    Drop(host);
                    if (attempt == 1) throw;
                    Ctx.Log("[WARN] Piper restarted: " + ex.Message);
                }
            }
            throw new InvalidOperationException("Piper failed");
        }

        static PiperHost Get(string key, VoiceInfo v, double ls)
        {
            lock (poolLock)
            {
                foreach (PiperHost h in pool) if (h.Key == key && h.Alive) return h;
                pool.RemoveAll(delegate (PiperHost h) { if (!h.Alive) { h.Stop(); return true; } return false; });
                while (pool.Count >= MaxHosts)
                {
                    PiperHost oldest = pool[0];
                    foreach (PiperHost h in pool) if (h.LastUsed < oldest.LastUsed) oldest = h;
                    oldest.Stop();
                    pool.Remove(oldest);
                }
                PiperHost created = new PiperHost(key, v, ls);
                pool.Add(created);
                return created;
            }
        }

        static void Drop(PiperHost h)
        {
            lock (poolLock) { h.Stop(); pool.Remove(h); }
        }

        public static void StopAll()
        {
            lock (poolLock)
            {
                foreach (PiperHost h in pool) h.Stop();
                pool.Clear();
            }
        }
    }

    internal static class Synth
    {
        static readonly Dictionary<string, byte[]> cache = new Dictionary<string, byte[]>();
        static readonly Queue<string> order = new Queue<string>();
        static long cacheBytes;

        public static byte[] Render(VoiceInfo v, double speed, string text)
        {
            string key = v.Id + "|" + speed.ToString("0.00", CultureInfo.InvariantCulture) + "|" + text;
            lock (cache) { byte[] hit; if (cache.TryGetValue(key, out hit)) return hit; }
            byte[] wav;
            if (v.Engine == "piper") wav = PiperEngine.Render(v, speed, text);
            else wav = WinRt.Render(v.WinId, speed, text);
            lock (cache)
            {
                if (!cache.ContainsKey(key))
                {
                    cache[key] = wav; order.Enqueue(key); cacheBytes += wav.Length;
                    while (cacheBytes > 48 * 1024 * 1024 && order.Count > 0)
                    {
                        string old = order.Dequeue();
                        byte[] b;
                        if (cache.TryGetValue(old, out b)) { cacheBytes -= b.Length; cache.Remove(old); }
                    }
                }
            }
            return wav;
        }
    }

    // Speech and wav playback, one item at a time, in order. Interrupt cancels the current item and the queue.
    internal sealed class Player
    {
        sealed class Item { public bool IsFile; public string Value; public List<Segment> Segs; public int Generation; public bool Test; public DateTime Born = DateTime.UtcNow; public int MaxAge; public int Vol = 100; }

        readonly BlockingCollection<Item> queue = new BlockingCollection<Item>();
        readonly object sync = new object();
        int generation;
        Playing current;

        // what plays now, to be stopped by an interrupt: the system player (default device only) or a playback on a chosen device
        sealed class Playing
        {
            public SoundPlayer Sp; public WaveOutPlayback Wo;
            public void Stop() { if (Sp != null) Sp.Stop(); if (Wo != null) Wo.Stop(); }
        }

        // the categories of the output (the advanced options of the Volume tab)
        enum Cat { Headset, External, Notify }

        static string DeviceFor(Cat c) { return c == Cat.External ? Ctx.Settings.AudioOut.ExternalDevice : Ctx.Settings.AudioOut.HeadsetDevice; }      // the notifications go to the headset
        static double LevelFor(Cat c) { AudioOutSettings a = Ctx.Settings.AudioOut; return (c == Cat.External ? a.ExternalLevel : c == Cat.Notify ? a.NotifyLevel : a.HeadsetLevel) / 100.0; }

        public Player()
        {
            Thread worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Start();
        }

        public void EnqueueText(string text, bool interrupt) { EnqueueText(text, interrupt, 0); }

        // a test of the program itself (the Test buttons, Try a text): it does not depend on the simulator, so the volume of the mission and the
        // inside / outside level are not applied (as if L:VOLUME_CREW were 100): the program can be set up before the simulator and the mission run
        public void EnqueueTest(string text) { EnqueueText(text, true, 0, true); }

        // maxAgeSeconds > 0: the item is dropped when its turn comes later than that (lines from the simulator variables must not arrive late)
        public void EnqueueText(string text, bool interrupt, int maxAgeSeconds) { EnqueueText(text, interrupt, maxAgeSeconds, false); }

        void EnqueueText(string text, bool interrupt, int maxAgeSeconds, bool test)
        {
            if (interrupt) CancelAll();
            List<string> notes = new List<string>();
            string wavFile;
            text = Say.Process(text, notes, out wavFile);       // <say:KEY>, <pack:..>, <names:..>, <variety:..>, <plain>
            List<Segment> segs = TagParser.Parse(text, notes);
            foreach (string n in notes) Ctx.Log("[WARN] " + n);
            if (!test) Say.ApplyDelivery(segs);              // the dispatcher: external radio call, or nothing with the radio off
            if (wavFile != null)
            {
                int wavVol;
                wavFile = Say.SplitWav(wavFile, out wavVol);
                string path = Path.GetFullPath(Path.Combine(Ctx.AudioDir, wavFile.Replace("\\", "").Replace("..", "")));
                if (AppSounds.Provide(path)) { Ctx.Log("[PLAY] " + wavFile + (wavVol < 100 ? " (level " + wavVol + ")" : "")); EnqueueFile(path, false, wavVol); }
                else Ctx.Log("[WARN] sound file " + wavFile + " is not in the audio folder");
            }
            if (segs.Count == 0) return;
            Item item = new Item();
            item.Segs = segs;
            item.Test = test;
            item.MaxAge = maxAgeSeconds;
            item.Generation = Thread.VolatileRead(ref generation);
            queue.Add(item);
        }

        // a sound file of the audio folder by name (sound-only messages of a pack)
        public void PlayAudioFile(string spec, int maxAgeSeconds)
        {
            int vol;
            string name = Say.SplitWav(spec, out vol);
            string path = Path.GetFullPath(Path.Combine(Ctx.AudioDir, name.Replace("\\", "").Replace("..", "")));
            if (!AppSounds.Provide(path)) { Ctx.Log("[WARN] sound file " + name + " is not in the audio folder and not in the program"); return; }
            Ctx.Log("[PLAY] " + name + (vol < 100 ? " (level " + vol + ")" : ""));
            Item item = new Item();
            item.IsFile = true;
            item.Value = path;
            item.Vol = vol;
            item.MaxAge = maxAgeSeconds;
            item.Generation = Thread.VolatileRead(ref generation);
            queue.Add(item);
        }

        public void EnqueueFile(string path, bool interrupt) { EnqueueFile(path, interrupt, 100); }

        // the Test button of the notifications: a sound file of the audio folder at the notification level, not at the volume of the mission
        public void PlayNotificationTest(string name)
        {
            string path = Path.GetFullPath(Path.Combine(Ctx.AudioDir, name));
            if (!AppSounds.Provide(path)) { Ctx.Log("[WARN] sound file " + name + " is not in the audio folder and not in the program"); return; }
            Ctx.Log("[PLAY] " + name + " (test)");
            Item item = new Item();
            item.IsFile = true;
            item.Value = path;
            item.Test = true;
            item.Generation = Thread.VolatileRead(ref generation);
            queue.Add(item);
        }

        public void EnqueueFile(string path, bool interrupt, int vol)
        {
            if (interrupt) CancelAll();
            Item item = new Item();
            item.IsFile = true;
            item.Value = path;
            item.Vol = vol;
            item.Generation = Thread.VolatileRead(ref generation);
            queue.Add(item);
        }

        public void CancelAll()
        {
            Interlocked.Increment(ref generation);
            Item dropped;
            while (queue.TryTake(out dropped)) { }
            lock (sync)
            {
                if (current != null)
                {
                    try { current.Stop(); } catch (Exception) { }
                }
            }
        }

        void Run()
        {
            foreach (Item item in queue.GetConsumingEnumerable())
            {
                if (item.Generation != Thread.VolatileRead(ref generation)) continue;
                if (item.MaxAge > 0 && (DateTime.UtcNow - item.Born).TotalSeconds > item.MaxAge)
                {
                    Ctx.Log("[DROP] a line from the simulator waited more than " + item.MaxAge + " s and was dropped");
                    continue;
                }
                try
                {
                    if (item.IsFile) PlayFile(item);
                    else
                    {
                        foreach (Segment seg in item.Segs)
                        {
                            if (item.Generation != Thread.VolatileRead(ref generation)) break;
                            Speak(item, seg);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Ctx.Log("[ERROR] " + ex.Message);
                }
            }
        }

        void Speak(Item item, Segment seg)
        {
            Plan p = Resolver.Resolve(seg, Ctx.Settings, Ctx.Voices);
            if (p.Voice == null) { Ctx.Log("[ERROR] no voice available"); return; }
            double level = item.Test ? p.Volume : p.Volume * Mixer.MissionFactor(p.Actor) * Mixer.SpatialFactor(p.Radio, Say.SpeakerInside(p.Actor));
            if (p.Far) level *= RadioRules.FarVolume;
            Cat cat = p.Radio ? Cat.Headset : Cat.External;       // intercom, radio and external radio calls are heard in the headset, a voice in the air is not
            level *= LevelFor(cat);
            Ctx.Log("[VOICE] actor " + p.Actor + ", " + p.Lang + ", " + p.Voice.Display + ", speed " + p.Speed.ToString("0.0#", CultureInfo.InvariantCulture) + ", vol " + level.ToString("0.#", CultureInfo.InvariantCulture) + (item.Test ? " (test: the volume of the mission is not applied)" : "") + (p.Radio ? (p.Fx == "ics" ? ", intercom" : (p.Fx == "ext" ? ", external radio" : ", radio")) + (p.Beep != null ? " (" + p.Beep + ")" : "") : "") + (p.Far ? ", far (30 to 100 m)" : "") + (p.Note.Length > 0 ? " (" + p.Note.Trim() + ")" : "") + ": " + seg.Text);
            DateTime t0 = DateTime.UtcNow;
            byte[] wav = Synth.Render(p.Voice, p.Speed, seg.Text);
            Ctx.Log("[RENDER] " + (int)(DateTime.UtcNow - t0).TotalMilliseconds + " ms, " + (wav.Length / 1024) + " KB");
            if (item.Generation != Thread.VolatileRead(ref generation)) return;
            RadioSettings rs = FxSettings(p);
            byte[] fx = AudioFx.Apply(wav, level, Ctx.Settings.MasterVolume, p.Radio, rs);
            PlayBytes(item, fx, null, cat);
        }

        // the radio effect of a line: the intercom has the hiss of the user and a click; a radio call the hiss + 10 and the beep of the user (or the one of
        // the tag); an external radio call the hiss + 20 and a double beep (RadioRules)
        static RadioSettings FxSettings(Plan p)
        {
            RadioSettings b = Ctx.Settings.Radio;
            RadioSettings rs = new RadioSettings();
            rs.NoiseLevel = b.NoiseLevel; rs.BeepLevel = b.BeepLevel; rs.Tail = b.Tail; rs.Quality = b.Quality; rs.Beep = b.Beep;
            if (p.Fx == "ics") { rs.Beep = RadioRules.IcsBeep; rs.Tail = false; }
            else if (p.Fx == "ext") { rs.Beep = RadioRules.ExternalBeep; rs.NoiseLevel = Math.Min(100, b.NoiseLevel + RadioRules.ExternalHissExtra); }
            else
            {
                rs.NoiseLevel = Math.Min(100, b.NoiseLevel + RadioRules.RadioHissExtra);
                if (p.Beep != null) rs.Beep = p.Beep;
            }
            return rs;
        }

        // a wav file of the audio folder, at the master volume (PCM 16 bit); any other format plays as it is
        void PlayFile(Item item)
        {
            byte[] fx = null;
            double level = item.Vol * (item.Test ? 1.0 : Mixer.CrewFactor()) * LevelFor(Cat.Notify);
            try { fx = AudioFx.Apply(File.ReadAllBytes(item.Value), level, Ctx.Settings.MasterVolume, false, Ctx.Settings.Radio); }
            catch (Exception) { fx = null; }
            if (fx != null) PlayBytes(item, fx, null, Cat.Notify); else PlayBytes(item, null, item.Value, Cat.Notify);
        }

        void PlayBytes(Item item, byte[] bytes, string path, Cat cat)
        {
            if (Ctx.Silent)
            {
                Ctx.Log("[SILENT] " + (path != null ? path : (bytes.Length + " bytes of speech")));
                return;
            }
            // a PCM wav goes to the device of its category (the Windows default device when none is chosen); any other format, or a device that does not open, plays on the default device
            if (bytes != null)
            {
                WaveOutPlayback wo = new WaveOutPlayback();
                Playing pw = new Playing(); pw.Wo = wo;
                lock (sync) { current = pw; }
                if (item.Generation != Thread.VolatileRead(ref generation)) { lock (sync) { current = null; } return; }
                bool played = false;
                try { played = wo.Play(bytes, WaveOutDevices.Resolve(DeviceFor(cat))); }
                catch (Exception ex) { Ctx.Log("[WARN] the output device failed (" + ex.Message + "): the Windows default device is used"); }
                finally { lock (sync) { current = null; } }
                if (played) return;
                if (item.Generation != Thread.VolatileRead(ref generation)) return;
            }
            using (MemoryStream ms = bytes != null ? new MemoryStream(bytes) : null)
            {
                SoundPlayer player = bytes != null ? new SoundPlayer(ms) : new SoundPlayer(path);
                Playing ps = new Playing(); ps.Sp = player;
                lock (sync) { current = ps; }
                if (item.Generation != Thread.VolatileRead(ref generation)) { lock (sync) { current = null; } return; }
                try { player.PlaySync(); }
                finally { lock (sync) { current = null; } player.Dispose(); }
            }
        }
    }
}
