// HPG Mission Companion - audio effects on the speech wav: volume and the radio effect (band filter, beep, hiss, squelch tail).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace HPGMissionCompanion
{
    internal static class AudioFx
    {
        public static readonly string[] BeepTypes = new string[] { "None", "Click", "Beep", "Double beep", "Chirp" };
        // a beep type from a mission tag ("double", "Double beep", "3", "chirp", "off") -> the name in BeepTypes, or null when it is not one
        public static string NormalizeBeep(string name)
        {
            if (name == null) return null;
            string n = name.Trim().ToLowerInvariant().Replace(" ", "");
            switch (n)
            {
                case "0": case "none": case "off": case "no": return BeepTypes[0];
                case "1": case "click": return BeepTypes[1];
                case "2": case "beep": case "single": return BeepTypes[2];
                case "3": case "double": case "doublebeep": return BeepTypes[3];
                case "4": case "chirp": return BeepTypes[4];
            }
            return null;
        }

        public static readonly string[] Qualities = new string[] { "Clean", "Aviation", "Rough" };

        sealed class Pcm
        {
            public float[] Data;
            public int Rate;
        }

        sealed class Biquad
        {
            double b0, b1, b2, a1, a2, x1, x2, y1, y2;

            public static Biquad LowPass(double fs, double f, double q) { return Make(fs, f, q, true); }
            public static Biquad HighPass(double fs, double f, double q) { return Make(fs, f, q, false); }

            static Biquad Make(double fs, double f, double q, bool low)
            {
                f = Math.Min(f, fs * 0.45);
                double w0 = 2 * Math.PI * f / fs;
                double cs = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
                double a0 = 1 + alpha;
                Biquad b = new Biquad();
                if (low) { b.b0 = (1 - cs) / 2; b.b1 = 1 - cs; b.b2 = (1 - cs) / 2; }
                else { b.b0 = (1 + cs) / 2; b.b1 = -(1 + cs); b.b2 = (1 + cs) / 2; }
                b.a1 = -2 * cs; b.a2 = 1 - alpha;
                b.b0 /= a0; b.b1 /= a0; b.b2 /= a0; b.a1 /= a0; b.a2 /= a0;
                return b;
            }

            public double Process(double x)
            {
                double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
                return y;
            }
        }

        // ---- wav in and out -------------------------------------------------------------------
        static Pcm Decode(byte[] wav)
        {
            if (wav == null || wav.Length < 44) return null;
            if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return null;
            int pos = 12, channels = 0, rate = 0, bits = 0, format = 0;
            int dataPos = -1, dataLen = 0;
            while (pos + 8 <= wav.Length)
            {
                string id = "" + (char)wav[pos] + (char)wav[pos + 1] + (char)wav[pos + 2] + (char)wav[pos + 3];
                long size = BitConverter.ToUInt32(wav, pos + 4);
                int body = pos + 8;
                if (id == "fmt " && body + 16 <= wav.Length)
                {
                    format = BitConverter.ToUInt16(wav, body);
                    channels = BitConverter.ToUInt16(wav, body + 2);
                    rate = BitConverter.ToInt32(wav, body + 4);
                    bits = BitConverter.ToUInt16(wav, body + 14);
                }
                else if (id == "data")
                {
                    dataPos = body;
                    dataLen = (int)Math.Min(size, (long)(wav.Length - body));
                    break;
                }
                long next = body + size + (size & 1);
                if (next > wav.Length) break;
                pos = (int)next;
            }
            if (dataPos < 0 || bits != 16 || channels < 1 || rate < 8000) return null;
            if (format != 1 && format != 0xFFFE) return null;
            int frames = dataLen / (2 * channels);
            float[] data = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                double sum = 0;
                for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(wav, dataPos + (i * channels + c) * 2);
                data[i] = (float)(sum / channels / 32768.0);
            }
            Pcm p = new Pcm();
            p.Data = data; p.Rate = rate;
            return p;
        }

        static byte[] Encode(float[] data, int rate)
        {
            byte[] outb = new byte[44 + data.Length * 2];
            WriteAscii(outb, 0, "RIFF");
            BitConverter.GetBytes(36 + data.Length * 2).CopyTo(outb, 4);
            WriteAscii(outb, 8, "WAVEfmt ");
            BitConverter.GetBytes(16).CopyTo(outb, 16);
            BitConverter.GetBytes((short)1).CopyTo(outb, 20);
            BitConverter.GetBytes((short)1).CopyTo(outb, 22);
            BitConverter.GetBytes(rate).CopyTo(outb, 24);
            BitConverter.GetBytes(rate * 2).CopyTo(outb, 28);
            BitConverter.GetBytes((short)2).CopyTo(outb, 32);
            BitConverter.GetBytes((short)16).CopyTo(outb, 34);
            WriteAscii(outb, 36, "data");
            BitConverter.GetBytes(data.Length * 2).CopyTo(outb, 40);
            for (int i = 0; i < data.Length; i++)
            {
                double v = Math.Max(-1.0, Math.Min(1.0, data[i]));
                BitConverter.GetBytes((short)Math.Round(v * 32767.0)).CopyTo(outb, 44 + i * 2);
            }
            return outb;
        }

        static void WriteAscii(byte[] b, int pos, string s)
        {
            for (int i = 0; i < s.Length; i++) b[pos + i] = (byte)s[i];
        }

        // ---- public entry ---------------------------------------------------------------------
        // volume 0-100 (tag), master 0-100 (settings). Returns the wav unchanged when nothing has to be done.
        public static byte[] Apply(byte[] wav, double volume, double master, bool radio, RadioSettings rs)
        {
            double gain = Math.Max(0, Math.Min(100, volume)) / 100.0 * Math.Max(0, Math.Min(100, master)) / 100.0;
            if (!radio && Math.Abs(gain - 1.0) < 0.001) return wav;
            Pcm pcm = Decode(wav);
            if (pcm == null) return wav;
            float[] data = radio ? RadioChain(pcm, rs) : pcm.Data;
            if (!radio) data = (float[])data.Clone();
            double peak = 0;
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (float)(data[i] * gain);
                double a = Math.Abs(data[i]);
                if (a > peak) peak = a;
            }
            if (peak > 0.98)
            {
                float k = (float)(0.98 / peak);
                for (int i = 0; i < data.Length; i++) data[i] *= k;
            }
            return Encode(data, pcm.Rate);
        }

        // ---- radio effect ---------------------------------------------------------------------
        static float[] RadioChain(Pcm pcm, RadioSettings rs)
        {
            int sr = pcm.Rate;
            double hp, lp, drive;
            switch (rs.Quality)
            {
                case "Clean": hp = 250; lp = 4500; drive = 1.0; break;
                case "Rough": hp = 500; lp = 2400; drive = 5.0; break;
                default: hp = 350; lp = 2800; drive = 2.2; break;
            }

            // speech: band filter, soft compression, back to the original peak
            float[] speech = new float[pcm.Data.Length];
            Biquad h1 = Biquad.HighPass(sr, hp, 0.707), h2 = Biquad.HighPass(sr, hp, 0.707);
            Biquad l1 = Biquad.LowPass(sr, lp, 0.707), l2 = Biquad.LowPass(sr, lp, 0.707);
            double inSum = 0, outSum = 0;
            for (int i = 0; i < speech.Length; i++)
            {
                double x = pcm.Data[i];
                inSum += x * x;
                x = l2.Process(l1.Process(h2.Process(h1.Process(x))));
                if (drive > 1.01) x = Math.Tanh(drive * x) / Math.Tanh(drive);
                speech[i] = (float)x;
                outSum += x * x;
            }
            // same loudness as the clean voice (rms), the final limiter takes care of the peaks
            if (outSum > 1e-9 && inSum > 1e-9)
            {
                float k = (float)Math.Sqrt(inSum / outSum);
                for (int i = 0; i < speech.Length; i++) speech[i] *= k;
            }

            // timeline: lead-in hiss, beep, gap, speech, hiss, squelch tail
            float[] beep = MakeBeep(rs.Beep, sr, rs.BeepLevel / 100.0);
            int lead = (int)(0.06 * sr);
            int gap = beep.Length > 0 ? (int)(0.09 * sr) : (int)(0.10 * sr);
            int hissTail = (int)(0.10 * sr);
            int speechStart = lead + beep.Length + gap;
            int hissEnd = speechStart + speech.Length + hissTail;

            double noiseRms = 0.25 * Math.Pow(rs.NoiseLevel / 100.0, 1.5);
            float[] burst = rs.Tail ? GetSquelchTail(sr, noiseRms) : null;
            int squelch = burst != null ? burst.Length : 0;
            float[] outd = new float[hissEnd + squelch];
            if (noiseRms > 0)
            {
                float[] noise = MakeNoise(hissEnd, sr, noiseRms);
                int fade = (int)(0.01 * sr);
                for (int i = 0; i < hissEnd; i++)
                {
                    double env = 1.0;
                    if (i < fade) env = i / (double)fade;
                    else if (i > hissEnd - fade) env = (hissEnd - i) / (double)fade;
                    outd[i] += (float)(noise[i] * env);
                }
            }
            for (int i = 0; i < beep.Length; i++) outd[lead + i] += beep[i];
            for (int i = 0; i < speech.Length; i++) outd[speechStart + i] += speech[i];
            for (int i = 0; i < squelch; i++) outd[hissEnd + i] += burst[i];
            return outd;
        }

        // ---- squelch tail: the click of a real transmission ends -------------------------------
        // Ten recordings of ATC transmission ends (assets\squelch-tail, embedded in the program; idea and recordings of the fork by d41k4n, commit 64a54ca).
        // One is drawn at every call (never the same twice in a row) and resampled to the rate of the voice. The user can replace them: wav files in the
        // folder squelch-tail of the audio folder are used instead (the folder is created with the ten defaults the first time); without any file the
        // old synthetic burst is used.
        static readonly object squelchSync = new object();
        static List<Pcm> squelchSnippets = null;
        static string squelchSource = null;
        static int squelchLast = -1;
        static readonly Random squelchRng = new Random();

        public static string SquelchFolder
        {
            get { return string.IsNullOrEmpty(Ctx.AudioDir) ? null : Path.Combine(Ctx.AudioDir, "squelch-tail"); }
        }

        // forget the loaded recordings (the audio folder changed, or the user edited the folder)
        public static void ReloadSquelch() { lock (squelchSync) { squelchSnippets = null; squelchLast = -1; } }

        static float[] GetSquelchTail(int targetRate, double noiseRms)
        {
            Pcm snippet = null;
            lock (squelchSync)
            {
                if (squelchSnippets == null || squelchSource != SquelchFolder) { squelchSource = SquelchFolder; squelchSnippets = LoadSquelchSnippets(); }
                int n = squelchSnippets.Count;
                if (n > 0)
                {
                    int idx = squelchRng.Next(n);
                    if (n > 1 && idx == squelchLast) idx = (idx + 1 + squelchRng.Next(n - 1)) % n;
                    squelchLast = idx;
                    snippet = squelchSnippets[idx];
                }
            }
            if (snippet != null)
            {
                float[] burst = Resample(snippet.Data, snippet.Rate, targetRate);
                int fade = Math.Min(burst.Length / 4, (int)(0.002 * targetRate));     // micro fades: no click at the edges
                for (int i = 0; i < fade; i++)
                {
                    float k = (float)(i / (double)fade);
                    burst[i] *= k;
                    burst[burst.Length - 1 - i] *= k;
                }
                return burst;
            }
            int len = (int)(0.12 * targetRate);
            double tailRms = Math.Max(0.05, noiseRms * 2.5);
            float[] fb = MakeNoise(len, targetRate, tailRms);
            for (int i = 0; i < len; i++) fb[i] = (float)(fb[i] * Math.Exp(-5.0 * i / len));
            return fb;
        }

        static List<Pcm> LoadSquelchSnippets()
        {
            List<Pcm> list = new List<Pcm>();
            Assembly asm = typeof(AudioFx).Assembly;
            string dir = SquelchFolder;
            try
            {
                if (dir != null)
                {
                    if (RuntimeFileSafety.HasReparsePoint(dir)) throw new InvalidDataException("Squelch folder cannot follow links.");
                    if (!Directory.Exists(dir))        // first use: the defaults, so that the user sees them and can replace them
                    {
                        Directory.CreateDirectory(dir);
                        foreach (string res in SquelchResources(asm))
                        {
                            using (Stream s = asm.GetManifestResourceStream(res))
                            using (FileStream o = File.Create(Path.Combine(dir, Path.GetFileName(res))))
                                if (s != null) s.CopyTo(o);
                        }
                    }
                    string[] files = Directory.GetFiles(dir, "*.wav");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (string f in files)
                    {
                        if (list.Count >= 16) break;
                        if (new FileInfo(f).Length > 2 * 1024 * 1024 || RuntimeFileSafety.HasReparsePoint(f)) continue;
                        try
                        {
                            Pcm p = Decode(File.ReadAllBytes(f));
                            if (p != null && p.Data.Length > 0) list.Add(p);
                            else if (Ctx.Log != null) Ctx.Log("[WARN] Squelch tail: " + Path.GetFileName(f) + " is not a 16 bit wav, skipped");
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            if (list.Count == 0)
            {
                foreach (string res in SquelchResources(asm))
                {
                    try
                    {
                        using (Stream s = asm.GetManifestResourceStream(res))
                        {
                            if (s == null) continue;
                            MemoryStream ms = new MemoryStream();
                            s.CopyTo(ms);
                            Pcm p = Decode(ms.ToArray());
                            if (p != null && p.Data.Length > 0) list.Add(p);
                        }
                    }
                    catch (Exception) { }
                }
            }
            return list;
        }

        static List<string> SquelchResources(Assembly asm)
        {
            List<string> names = new List<string>();
            foreach (string n in asm.GetManifestResourceNames())
                if (n.StartsWith("squelch_tail_", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) names.Add(n);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        // linear interpolation to the rate of the voice
        static float[] Resample(float[] input, int inRate, int outRate)
        {
            if (input == null || input.Length == 0) return new float[0];
            if (inRate == outRate) return (float[])input.Clone();
            int outLen = (int)Math.Round((long)input.Length * outRate / (double)inRate);
            if (outLen <= 0) return new float[0];
            float[] outd = new float[outLen];
            double ratio = inRate / (double)outRate;
            for (int i = 0; i < outLen; i++)
            {
                double pos = i * ratio;
                int idx = (int)pos;
                if (idx >= input.Length - 1) outd[i] = input[input.Length - 1];
                else outd[i] = (float)(input[idx] + (pos - idx) * (input[idx + 1] - input[idx]));
            }
            return outd;
        }

        static float[] MakeNoise(int n, int sr, double targetRms)
        {
            Random rng = new Random(12345 + n);
            float[] r = new float[n];
            Biquad h1 = Biquad.HighPass(sr, 280, 0.707), h2 = Biquad.HighPass(sr, 280, 0.707);
            Biquad l1 = Biquad.LowPass(sr, 3600, 0.707), l2 = Biquad.LowPass(sr, 3600, 0.707);
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                double x = l2.Process(l1.Process(h2.Process(h1.Process(rng.NextDouble() * 2 - 1))));
                r[i] = (float)x;
                sum += x * x;
            }
            double rms = n > 0 ? Math.Sqrt(sum / n) : 0;
            if (rms > 1e-9)
            {
                float k = (float)(targetRms / rms);
                for (int i = 0; i < n; i++) r[i] *= k;
            }
            return r;
        }

        static float[] MakeBeep(string type, int sr, double level)
        {
            double amp = 0.35 * level;
            List<float> list = new List<float>();
            if (type == "Beep") Tone(list, sr, 1000, 1000, 0.110, amp);
            else if (type == "Double beep")
            {
                Tone(list, sr, 1000, 1000, 0.070, amp);
                Silence(list, sr, 0.060);
                Tone(list, sr, 1000, 1000, 0.070, amp);
            }
            else if (type == "Chirp") Tone(list, sr, 700, 1500, 0.150, amp);
            else if (type == "Click")
            {
                Random rng = new Random(77);
                int n = (int)(0.030 * sr);
                for (int i = 0; i < n; i++) list.Add((float)((rng.NextDouble() * 2 - 1) * Math.Exp(-6.0 * i / n) * amp * 1.4));
            }
            return list.ToArray();
        }

        static void Tone(List<float> list, int sr, double f0, double f1, double seconds, double amp)
        {
            int n = (int)(seconds * sr);
            int fade = (int)(0.005 * sr);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double f = f0 + (f1 - f0) * i / (double)n;
                phase += 2 * Math.PI * f / sr;
                double env = 1.0;
                if (i < fade) env = i / (double)fade;
                else if (i > n - fade) env = (n - i) / (double)fade;
                list.Add((float)(Math.Sin(phase) * amp * env));
            }
        }

        static void Silence(List<float> list, int sr, double seconds)
        {
            int n = (int)(seconds * sr);
            for (int i = 0; i < n; i++) list.Add(0f);
        }

        // for the selftest report
        public static string Describe(byte[] wav)
        {
            Pcm p = Decode(wav);
            if (p == null) return "(not PCM16)";
            double peak = 0, sum = 0;
            foreach (float s in p.Data) { double a = Math.Abs(s); if (a > peak) peak = a; sum += s * s; }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00} s at {1} Hz, peak {2:0.000}, rms {3:0.0000}", p.Data.Length / (double)p.Rate, p.Rate, peak, p.Data.Length > 0 ? Math.Sqrt(sum / p.Data.Length) : 0);
        }
    }

    // The output devices of Windows (the classic wave out, winmm.dll: the names are the ones of the sound settings of Windows, cut at 31 characters) and
    // the playback of a PCM wav on a chosen one. Id -1 is the Windows default device (the wave mapper).
    internal static class WaveOutDevices
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct WAVEOUTCAPS
        {
            public ushort wMid; public ushort wPid; public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwFormats; public ushort wChannels; public ushort wReserved1; public uint dwSupport;
        }

        [DllImport("winmm.dll")] static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Auto)] static extern int waveOutGetDevCaps(IntPtr uDeviceID, ref WAVEOUTCAPS caps, int size);

        public const string DefaultName = "Windows default device";
        static readonly HashSet<string> warned = new HashSet<string>();

        // the names of the output devices, in the order of Windows; a name that repeats gets " (2)", " (3)"
        public static List<string> Names()
        {
            List<string> list = new List<string>();
            try
            {
                int n = waveOutGetNumDevs();
                Dictionary<string, int> seen = new Dictionary<string, int>();
                for (int i = 0; i < n; i++)
                {
                    WAVEOUTCAPS caps = new WAVEOUTCAPS();
                    string name = "Device " + i;
                    if (waveOutGetDevCaps((IntPtr)i, ref caps, Marshal.SizeOf(typeof(WAVEOUTCAPS))) == 0 && !string.IsNullOrEmpty(caps.szPname)) name = caps.szPname;
                    int c;
                    seen.TryGetValue(name, out c);
                    seen[name] = c + 1;
                    list.Add(c == 0 ? name : name + " (" + (c + 1) + ")");
                }
            }
            catch (Exception) { }
            return list;
        }

        // -1 = the default device: no name, or a name that is not there any more (said once in the log)
        public static int Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            List<string> names = Names();
            for (int i = 0; i < names.Count; i++) if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            if (warned.Add(name)) Ctx.Log("[WARN] the output device '" + name + "' is not there: the Windows default device is used");
            return -1;
        }
    }

    // One playback of a PCM wav on a device; Play blocks until the end, Stop (from another thread) ends it at once.
    internal sealed class WaveOutPlayback
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX
        {
            public ushort wFormatTag; public ushort nChannels; public uint nSamplesPerSec; public uint nAvgBytesPerSec;
            public ushort nBlockAlign; public ushort wBitsPerSample; public ushort cbSize;
        }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, int uDeviceID, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);

        volatile bool stop;

        public void Stop() { stop = true; }

        // false: the wav is not a PCM one or the device could not be opened (the caller plays it another way)
        public bool Play(byte[] wav, int deviceId)
        {
            WAVEFORMATEX fmt = new WAVEFORMATEX();
            int dataPos = -1, dataLen = 0;
            if (!Parse(wav, ref fmt, out dataPos, out dataLen)) return false;
            IntPtr hwo;
            if (waveOutOpen(out hwo, deviceId, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) return false;
            int hdrSize = IntPtr.Size == 8 ? 48 : 32;
            int flagsOffset = IntPtr.Size == 8 ? 24 : 16;
            IntPtr data = Marshal.AllocHGlobal(dataLen);
            IntPtr hdr = Marshal.AllocHGlobal(hdrSize);
            bool prepared = false;
            try
            {
                Marshal.Copy(wav, dataPos, data, dataLen);
                for (int i = 0; i < hdrSize; i++) Marshal.WriteByte(hdr, i, 0);
                Marshal.WriteIntPtr(hdr, 0, data);
                Marshal.WriteInt32(hdr, IntPtr.Size, dataLen);
                if (waveOutPrepareHeader(hwo, hdr, hdrSize) != 0) return false;
                prepared = true;
                if (waveOutWrite(hwo, hdr, hdrSize) != 0) return false;
                bool reset = false;
                while ((Marshal.ReadInt32(hdr, flagsOffset) & 1) == 0)       // WHDR_DONE
                {
                    if (stop && !reset) { waveOutReset(hwo); reset = true; }
                    Thread.Sleep(10);
                }
                return true;
            }
            finally
            {
                if (prepared) waveOutUnprepareHeader(hwo, hdr, hdrSize);
                waveOutClose(hwo);
                Marshal.FreeHGlobal(hdr);
                Marshal.FreeHGlobal(data);
            }
        }

        // the format and the data of a RIFF wav (PCM only)
        static bool Parse(byte[] w, ref WAVEFORMATEX fmt, out int dataPos, out int dataLen)
        {
            dataPos = -1; dataLen = 0;
            if (w == null || w.Length < 44 || w[0] != 'R' || w[1] != 'I' || w[2] != 'F' || w[3] != 'F' || w[8] != 'W' || w[9] != 'A' || w[10] != 'V' || w[11] != 'E') return false;
            bool haveFmt = false;
            int pos = 12;
            while (pos + 8 <= w.Length)
            {
                string id = "" + (char)w[pos] + (char)w[pos + 1] + (char)w[pos + 2] + (char)w[pos + 3];
                int len = BitConverter.ToInt32(w, pos + 4);
                int body = pos + 8;
                if (id == "fmt " && len >= 16 && body + 16 <= w.Length)
                {
                    fmt.wFormatTag = BitConverter.ToUInt16(w, body);
                    fmt.nChannels = BitConverter.ToUInt16(w, body + 2);
                    fmt.nSamplesPerSec = BitConverter.ToUInt32(w, body + 4);
                    fmt.nAvgBytesPerSec = BitConverter.ToUInt32(w, body + 8);
                    fmt.nBlockAlign = BitConverter.ToUInt16(w, body + 12);
                    fmt.wBitsPerSample = BitConverter.ToUInt16(w, body + 14);
                    fmt.cbSize = 0;
                    haveFmt = fmt.wFormatTag == 1;
                }
                else if (id == "data")
                {
                    dataPos = body;
                    dataLen = len < 0 || body + len > w.Length ? w.Length - body : len;     // a streamed wav may say 0 or 0xFFFFFFFF
                    if (len == 0) dataLen = w.Length - body;
                    break;
                }
                if (len < 0) break;
                pos = body + len + (len & 1);
            }
            return haveFmt && dataPos >= 0 && dataLen > 0;
        }
    }

    // Synthetic notification effects are resolved from the installed mission, never from recorded voice mappings.
    internal static class AppSounds
    {
        static readonly object sync = new object();
        static readonly HashSet<string> missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static string MissionSoundRoot;
        static readonly string[] names = { "AND.NEW.DISPATCH.wav", "AND.NEW.DISPATCH1.wav", "AND.NEW.DISPATCH2.wav",
            "AND.NEW.DISPATCH3.wav", "AND.NEW.DISPATCH4.wav", "AND.NEW.MSG.wav", "AND.Inside.TabletAlarm2.wav", "AND.dead.wav", "stayngalive.wav" };

        public static List<string> Names() { return new List<string>(names); }

        public static bool Provide(string path)
        {
            string temporary = null;
            try
            {
                var name = Path.GetFileName(path);
                var target = RuntimeFileSafety.ManagedPath(Ctx.AudioDir, name);
                if (!Path.GetFullPath(path).Equals(target, StringComparison.OrdinalIgnoreCase)) return false;
                if (File.Exists(target)) return true;
                if (Array.FindIndex(names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) < 0) return false;
                lock (sync)
                {
                    if (File.Exists(target)) return true;
                    if (!string.IsNullOrEmpty(MissionSoundRoot))
                    {
                        var source = RuntimeFileSafety.ManagedPath(MissionSoundRoot, name);
                        if (File.Exists(source))
                        {
                            Directory.CreateDirectory(Ctx.AudioDir);
                            temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
                            File.Copy(source, temporary, false);
                            File.Move(temporary, target);
                            Ctx.Log("[SFX] Copied installed mission notification: " + name);
                            return true;
                        }
                    }
                    if (missing.Add(name)) Ctx.Log("[WARN] Synthetic notification missing: " + name + ". Install the mission sound assets; no recorded voice will be substituted.");
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is ArgumentException)
            {
                Ctx.Log("[WARN] Synthetic notification could not be prepared: " + Path.GetFileName(path) + ": " + ex.Message);
                return false;
            }
            finally
            {
                try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public static int EnsureAll()
        {
            var copied = 0;
            foreach (var name in names)
            {
                var path = Path.Combine(Ctx.AudioDir, name);
                var existed = File.Exists(path);
                if (Provide(path) && !existed) copied++;
            }
            return copied;
        }
    }
}
