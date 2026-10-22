using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RandomEverywhereInstaller.Audio
{
    /// <summary>Streams recorded clips through the standalone manager's float mixer format.</summary>
    internal sealed class RecordedPlaybackEngine : IDisposable
    {
        readonly object sync = new object();
        readonly MixingSampleProvider mixer;
        readonly VolumeSampleProvider master;
        readonly IWavePlayer output;
        readonly List<OwnedInput> inputs = new List<OwnedInput>();
        readonly int maximumClips;
        int preparing;
        bool disposed;

        public RecordedPlaybackEngine(int device, int volume)
            : this(() => new WaveOutEvent { DeviceNumber = device }, volume) { }

        // The output factory allows waveform checks without opening an audio device.
        internal RecordedPlaybackEngine(Func<IWavePlayer> outputFactory, int volume, int maximumClips = 32)
        {
            if (maximumClips < 1 || maximumClips > 128) throw new ArgumentOutOfRangeException(nameof(maximumClips));
            this.maximumClips = maximumClips;
            mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)) { ReadFully = true };
            master = new VolumeSampleProvider(mixer) { Volume = ClampVolume(volume) };
            output = outputFactory();
            try
            {
                output.Init(new SampleToWaveProvider(master));
                output.Play();
            }
            catch { output.Dispose(); throw; }
        }

        public void SetVolume(int volume)
        {
            lock (sync)
            {
                if (!disposed) master.Volume = ClampVolume(volume);
            }
        }

        public bool IsIdle
        {
            get
            {
                lock (sync)
                {
                    inputs.RemoveAll(input => input.IsDisposed);
                    return preparing == 0 && inputs.Count == 0;
                }
            }
        }

        public bool PlayFile(string path, Func<float> channelGain)
        {
            // Reserve before opening a decoder; bursts cannot exhaust file handles.
            lock (sync)
            {
                if (disposed) return false;
                inputs.RemoveAll(item => item.IsDisposed);
                if (inputs.Count + preparing >= maximumClips)
                    throw new InvalidOperationException("Recorded clip limit reached (" + maximumClips + "); newest clip skipped, active recordings preserved.");
                preparing++;
            }
            var reserved = true;
            AudioFileReader reader = null;
            OwnedInput input = null;
            try
            {
                reader = new AudioFileReader(path);
                ISampleProvider source = reader;
                if (source.WaveFormat.Channels != 1 && source.WaveFormat.Channels != 2)
                    throw new InvalidDataException("Recorded playback supports mono or stereo files; multichannel audio was not silently downmixed.");
                if (source.WaveFormat.SampleRate != mixer.WaveFormat.SampleRate)
                    source = new WdlResamplingSampleProvider(source, mixer.WaveFormat.SampleRate);
                if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
                input = new OwnedInput(source, reader, channelGain);
                lock (sync)
                {
                    preparing--; reserved = false;
                    // A clip prepared during Stop belongs to the old, disposed session.
                    if (disposed) { input.Dispose(); return false; }
                    inputs.Add(input);
                    try { mixer.AddMixerInput(input); }
                    catch { inputs.Remove(input); throw; }
                }
                return true;
            }
            catch { if (input != null) input.Dispose(); else if (reader != null) reader.Dispose(); throw; }
            finally { if (reserved) lock (sync) preparing--; }
        }

        public void Dispose()
        {
            OwnedInput[] pending;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                pending = inputs.ToArray();
                inputs.Clear();
            }
            try { output.Stop(); }
            finally
            {
                try { output.Dispose(); }
                finally
                {
                    mixer.RemoveAllMixerInputs();
                    foreach (var input in pending) input.Dispose();
                }
            }
        }

        static float ClampVolume(int value) { return Math.Max(0, Math.Min(100, value)) / 100f; }

        sealed class OwnedInput : ISampleProvider, IDisposable
        {
            readonly object gate = new object();
            readonly ISampleProvider source;
            readonly IDisposable owner;
            readonly Func<float> gain;
            bool disposed;
            public OwnedInput(ISampleProvider source, IDisposable owner, Func<float> gain)
            {
                this.source = source; this.owner = owner; this.gain = gain;
            }
            public WaveFormat WaveFormat { get { return source.WaveFormat; } }
            public bool IsDisposed { get { lock (gate) return disposed; } }

            public int Read(float[] buffer, int offset, int count)
            {
                lock (gate)
                {
                    if (disposed) return 0;
                    try
                    {
                        var read = source.Read(buffer, offset, count);
                        if (read == 0) { Dispose(); return 0; }
                        var level = gain == null ? 1f : gain();
                        if (float.IsNaN(level) || float.IsInfinity(level)) level = 1f;
                        level = Math.Max(0f, Math.Min(1f, level));
                        if (level != 1f)
                            for (var index = offset; index < offset + read; index++) buffer[index] *= level;
                        // The mixer removes short reads too; no later callback will close them.
                        if (read < count) Dispose();
                        return read;
                    }
                    catch { Dispose(); throw; }
                }
            }

            public void Dispose()
            {
                lock (gate)
                {
                    if (disposed) return;
                    disposed = true;
                    owner.Dispose();
                }
            }
        }
    }
}
