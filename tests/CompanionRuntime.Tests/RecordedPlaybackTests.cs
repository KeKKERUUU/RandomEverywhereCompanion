using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HPGMissionCompanion;
using NAudio.Wave;
using RandomEverywhereInstaller.Audio;

internal static class RecordedPlaybackTests
{
    public static void Run(string root, Action<bool, string> require)
    {
        var constant = WriteWav(root, "constant.wav", WaveFormat.CreateIeeeFloatWaveFormat(44100, 2),
            Enumerable.Repeat(0.25f, 4096).ToArray());
        var hash = Hash(constant);
        var output = new FakeOutput();
        using (var engine = new RecordedPlaybackEngine(() => output, 100))
        {
            engine.PlayFile(constant, null);
            require(output.OutputWaveFormat.SampleRate == 44100 && output.OutputWaveFormat.Channels == 2 &&
                output.OutputWaveFormat.Encoding == WaveFormatEncoding.IeeeFloat && output.OutputWaveFormat.BitsPerSample == 32,
                "Recorded output uses the required 44.1 kHz stereo float format");
            require(AllNear(output.ReadSamples(16), 0.25f), "Unity gain preserves decoded samples");
            engine.SetVolume(50);
            require(AllNear(output.ReadSamples(16), 0.125f), "Master volume changes active clips without restarting output");
            engine.SetVolume(0);
            require(AllNear(output.ReadSamples(16), 0f), "Recorded mute is exact silence");
            engine.SetVolume(200);
            require(AllNear(output.ReadSamples(16), 0.25f), "Master gain cannot exceed unity");
            engine.PlayFile(constant, null);
            require(AllNear(output.ReadSamples(16), 0.5f) && output.InitCount == 1 && output.PlayCount == 1,
                "Concurrent recorded clips share one continuous output");
        }
        require(output.Disposed && CanOpenExclusively(constant), "Session stop releases the output and active file streams");
        require(hash.SequenceEqual(Hash(constant)), "Playback leaves original recording bytes unchanged");

        // Attach a fake output to exercise the facade's live mute path without a device.
        var liveOutput = new FakeOutput();
        var liveEngine = new RecordedPlaybackEngine(() => liveOutput, 37);
        typeof(RecordedAudio).GetField("player", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, liveEngine);
        try
        {
            liveEngine.PlayFile(constant, null);
            RecordedAudio.SetVolume(37); RecordedAudio.SetMuted(true);
            require(AllNear(liveOutput.ReadSamples(16), 0f) && RecordedAudio.Volume == 37,
                "Recorded master mute silences active samples while retaining the level");
            RecordedAudio.SetVolume(42);
            require(AllNear(liveOutput.ReadSamples(16), 0f), "Moving the level while muted cannot leak recorded samples");
            RecordedAudio.SetMuted(false);
            require(AllNear(liveOutput.ReadSamples(16), 0.105f) && liveOutput.InitCount == 1,
                "Unmute applies the retained level without reopening the output");
        }
        finally { RecordedAudio.Stop(); RecordedAudio.SetMuted(false); RecordedAudio.SetVolume(80); }

        var previewOutput = new FakeOutput();
        var previewEngine = new RecordedPlaybackEngine(() => previewOutput, 100);
        typeof(RecordedAudio).GetField("player", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, previewEngine);
        try
        {
            previewEngine.PlayFile(constant, null);
            RecordedAudio.ReleaseIdlePreview();
            require(!previewOutput.Disposed, "Preview cleanup preserves an active clip");
            previewOutput.ReadSamples(8192);
            require(previewEngine.IsIdle && CanOpenExclusively(constant), "A final short read closes the file and marks the mixer idle");
            RecordedAudio.ReleaseIdlePreview();
            require(previewOutput.Disposed, "Idle preview releases the output without a simulator session");
        }
        finally { RecordedAudio.Stop(); }

        var activeOutput = new FakeOutput();
        var activeEngine = new RecordedPlaybackEngine(() => activeOutput, 100);
        typeof(RecordedAudio).GetField("player", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, activeEngine);
        try
        {
            RecordedAudio.Start();
            RecordedAudio.ReleaseIdlePreview();
            require(!activeOutput.Disposed, "An idle connected session retains its output for later triggers");
        }
        finally { RecordedAudio.Stop(); }

        foreach (var bits in new[] { 16, 24 })
        {
            var original = new[] { 0.25f, -0.5f, 0.75f, -0.125f, -0.25f, 0.5f, 0.125f, -0.75f };
            var path = WriteWav(root, "pcm-" + bits + ".wav", new WaveFormat(44100, bits, 2), original);
            var expected = new float[8];
            using (var reader = new AudioFileReader(path)) reader.Read(expected, 0, expected.Length);
            var fake = new FakeOutput();
            using (var engine = new RecordedPlaybackEngine(() => fake, 100))
            {
                engine.PlayFile(path, null);
                var actual = fake.ReadSamples(expected.Length);
                require(actual.Zip(expected, (a, b) => Math.Abs(a - b)).All(error => error < 0.000001f),
                    bits + "-bit PCM stereo preserves decoder output without added effects");
                require(actual.Length == original.Length &&
                    actual.Zip(original, (a, b) => Math.Abs(a - b)).All(error => error <= 1.0 / (1 << (bits - 1))),
                    bits + "-bit PCM matches independently specified samples within one quantization step");
                fake.ReadSamples(16);
                require(CanOpenExclusively(path), "Completed " + bits + "-bit clip releases its stream");
            }
        }

        var mono = WriteWav(root, "mono-48000.wav", WaveFormat.CreateIeeeFloatWaveFormat(48000, 1),
            Enumerable.Repeat(0.25f, 4800).ToArray());
        var monoOutput = new FakeOutput();
        using (var engine = new RecordedPlaybackEngine(() => monoOutput, 100))
        {
            engine.PlayFile(mono, null);
            var samples = monoOutput.ReadSamples(1024);
            require(samples.All(value => !float.IsNaN(value) && !float.IsInfinity(value)) &&
                samples.Any(value => Math.Abs(value) > 0.01f) &&
                Enumerable.Range(0, samples.Length / 2).All(index => samples[index * 2] == samples[index * 2 + 1]),
                "WDL conversion and mono duplication preserve equal stereo channels");
        }

        // A nonconstant fixture catches rate, pitch and level regressions independently of the decoder.
        var tone = WriteWav(root, "tone-48000.wav", WaveFormat.CreateIeeeFloatWaveFormat(48000, 1),
            Enumerable.Range(0, 48000).Select(i => (float)(0.4 * Math.Sin(2 * Math.PI * 1000 * i / 48000))).ToArray());
        var toneOutput = new FakeOutput();
        using (var engine = new RecordedPlaybackEngine(() => toneOutput, 100))
        {
            engine.PlayFile(tone, null);
            var stereo = toneOutput.ReadSamples(97020);
            var left = Enumerable.Range(0, stereo.Length / 2).Select(i => stereo[i * 2]).ToArray();
            var lastSignal = Array.FindLastIndex(left, sample => Math.Abs(sample) > 0.001f);
            require(Math.Abs(lastSignal + 1 - 44100) <= 128 && engine.IsIdle,
                "48 kHz resampling preserves one-second duration within filter-tail tolerance");
            var steady = left.Skip(4410).Take(35280).ToArray();
            var crossings = Enumerable.Range(1, steady.Length - 1).Count(i => steady[i - 1] <= 0 && steady[i] > 0);
            var frequency = crossings * 44100.0 / steady.Length;
            var rms = Math.Sqrt(steady.Average(sample => (double)sample * sample));
            require(Math.Abs(frequency - 1000) < 2 && Math.Abs(rms - 0.4 / Math.Sqrt(2)) < 0.005,
                "48 kHz resampling preserves independently specified tone frequency and RMS level");
            require(Enumerable.Range(0, left.Length).All(i => stereo[i * 2] == stereo[i * 2 + 1]),
                "Resampled mono tone is duplicated without channel imbalance");
        }

        float channel = 1;
        var channelOutput = new FakeOutput();
        using (var engine = new RecordedPlaybackEngine(() => channelOutput, 100))
        {
            engine.PlayFile(constant, () => channel);
            require(AllNear(channelOutput.ReadSamples(16), 0.25f), "Explicit channel starts at unity");
            channel = 0.2f;
            require(AllNear(channelOutput.ReadSamples(16), 0.05f), "Explicit channel gain follows changes during a clip");
            engine.SetVolume(50);
            require(AllNear(channelOutput.ReadSamples(16), 0.025f), "Master and explicit channel gain compose once");
            engine.Dispose();
            require(!engine.PlayFile(constant, null), "Stopped session rejects late clip submissions");
        }

        var multi = WriteWav(root, "surround.wav", WaveFormat.CreateIeeeFloatWaveFormat(44100, 6), new float[60]);
        var limitedOutput = new FakeOutput();
        using (var limited = new RecordedPlaybackEngine(() => limitedOutput, 100, maximumClips: 2))
        {
            limited.PlayFile(constant, null);
            limited.PlayFile(constant, null);
            var limitHit = false;
            try { limited.PlayFile("not-opened.wav", null); } catch (InvalidOperationException) { limitHit = true; }
            require(limitHit && AllNear(limitedOutput.ReadSamples(16), 0.5f),
                "Clip limit rejects newest input before decoding and leaves active audio unchanged");
            limitedOutput.ReadSamples(8192);
            require(limited.PlayFile(constant, null), "Completed clips release their capacity for later triggers");
        }
        var recoveryOutput = new FakeOutput();
        using (var recovery = new RecordedPlaybackEngine(() => recoveryOutput, 100, maximumClips: 1))
        {
            try { recovery.PlayFile("not-present.wav", null); } catch (IOException) { }
            require(recovery.PlayFile(constant, null), "Failed decoders release reserved clip capacity");
        }
        using (var engine = new RecordedPlaybackEngine(() => new FakeOutput(), 100))
        {
            var rejected = false;
            try { engine.PlayFile(multi, null); } catch (InvalidDataException) { rejected = true; }
            require(rejected && CanOpenExclusively(multi), "Unsupported multichannel files are not silently downmixed");
        }
        var failedOutput = new FakeOutput { FailInit = true };
        var failed = false;
        try { using (new RecordedPlaybackEngine(() => failedOutput, 100)) { } }
        catch (InvalidOperationException) { failed = true; }
        require(failed && failedOutput.Disposed, "Output initialization failure cleans up the device");

        var xml = Path.Combine(root, "channels.xml");
        File.WriteAllText(xml, "<SoundList category='Channels' Channel='crew'>" +
            "<Sound LocalVar='VOICE' FileName='constant.wav'/>" +
            "<Sound LocalVar='CHECK' FileName='constant.wav' Channel='checklist'/>" +
            "<Sound LocalVar='ORIGINAL' FileName='constant.wav' Channel='independent'/>" +
            "<Sound LocalVar='UNKNOWN' FileName='constant.wav' Channel='unsupported'/></SoundList>");
        var pack = RecordedAudio.Load(xml);
        require(pack.Mappings[0].Channel == RecordedChannel.Crew && pack.Mappings[1].Channel == RecordedChannel.Checklist &&
            pack.Mappings[2].Channel == RecordedChannel.Independent && pack.Mappings[3].Channel == RecordedChannel.Independent &&
            pack.Warnings.Count == 1, "Mapping channels are explicit and unknown values retain independent playback");
        File.WriteAllText(xml, "<SoundList><Sound LocalVar='CHECK' FileName='constant.wav'/></SoundList>");
        require(RecordedAudio.Load(xml).Mappings[0].Channel == RecordedChannel.Independent,
            "Original untagged packs retain standalone master-only behavior");

        var previous = Ctx.Settings;
        Ctx.Settings = new Settings { UseMissionVolumes = true };
        try
        {
            Mixer.Reset();
            Mixer.OnWatch(Mixer.VarCrew, 80); Mixer.OnWatch(Mixer.VarChk, 25);
            require(Math.Abs(RecordedAudio.ChannelGain(RecordedChannel.Crew, false) - 0.8f) < 0.000001f &&
                RecordedAudio.ChannelGain(RecordedChannel.Checklist, false) == 0.25f,
                "Explicit crew and checklist mappings use independent mission sliders");
            require(RecordedAudio.ChannelGain(RecordedChannel.Independent, false) == 1f &&
                RecordedAudio.ChannelGain(RecordedChannel.Checklist, true) == 1f,
                "Original packs and previews are unaffected by mission slider attenuation");
            Mixer.Reset();
            require(RecordedAudio.ChannelGain(RecordedChannel.Checklist, false) == 1f,
                "New sessions do not inherit a previous flight's gain");
        }
        finally { Ctx.Settings = previous; Mixer.Reset(); }
    }

    static string WriteWav(string root, string name, WaveFormat format, float[] samples)
    {
        var path = Path.Combine(root, name);
        using (var writer = new WaveFileWriter(path, format)) writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }
    static bool AllNear(float[] samples, float value) { return samples.All(sample => Math.Abs(sample - value) < 0.000001f); }
    static byte[] Hash(string path) { using (var hash = SHA256.Create()) using (var input = File.OpenRead(path)) return hash.ComputeHash(input); }
    static bool CanOpenExclusively(string path)
    {
        try { using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return true; }
        catch (IOException) { return false; }
    }

    sealed class FakeOutput : IWavePlayer
    {
        IWaveProvider source;
        public bool FailInit;
        public bool Disposed;
        public int InitCount;
        public int PlayCount;
        public float Volume { get; set; }
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat { get { return source.WaveFormat; } }
        public event EventHandler<StoppedEventArgs> PlaybackStopped;
        public void Init(IWaveProvider provider)
        {
            InitCount++;
            if (FailInit) throw new InvalidOperationException("Synthetic output failure.");
            source = provider;
        }
        public void Play() { PlayCount++; PlaybackState = PlaybackState.Playing; }
        public void Pause() { PlaybackState = PlaybackState.Paused; }
        public void Stop()
        {
            PlaybackState = PlaybackState.Stopped;
            if (PlaybackStopped != null) PlaybackStopped(this, new StoppedEventArgs(null));
        }
        public void Dispose() { Disposed = true; }
        public float[] ReadSamples(int count)
        {
            var buffer = new byte[count * sizeof(float)];
            var bytes = source.Read(buffer, 0, buffer.Length);
            var samples = new float[bytes / sizeof(float)];
            Buffer.BlockCopy(buffer, 0, samples, 0, bytes);
            return samples;
        }
    }
}
