using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Krach.Core;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Krach.Audio
{
    internal static class SoundLoader
    {
        public static readonly string[] Extensions = { ".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma" };

        public static bool IsSupported(string path)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return Array.IndexOf(Extensions, ext) >= 0;
        }

        /// <summary>Öffnet eine Datei und liefert sie als 48-kHz-Stereo-Float.</summary>
        public static ISampleProvider Open(string path, out WaveStream reader)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            reader = ext == ".ogg" ? new VorbisWaveReader(path) : new AudioFileReader(path);
            ISampleProvider sp = reader as ISampleProvider ?? reader.ToSampleProvider();
            return ToMixFormat(sp);
        }

        public static ISampleProvider ToMixFormat(ISampleProvider sp)
        {
            int ch = sp.WaveFormat.Channels;
            if (ch == 1) sp = new MonoToStereoSampleProvider(sp);
            else if (ch > 2) sp = new FirstTwoChannels(sp);
            if (sp.WaveFormat.SampleRate != AudioEngine.SampleRate)
                sp = new WdlResamplingSampleProvider(sp, AudioEngine.SampleRate);
            return sp;
        }
    }

    internal sealed class FirstTwoChannels : ISampleProvider
    {
        readonly ISampleProvider _src;
        readonly int _ch;
        float[] _buf = Array.Empty<float>();

        public FirstTwoChannels(ISampleProvider src)
        {
            _src = src;
            _ch = src.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int need = frames * _ch;
            if (_buf.Length < need) _buf = new float[need];
            int got = _src.Read(_buf, 0, need) / _ch;
            for (int f = 0; f < got; f++)
            {
                buffer[offset + f * 2] = _buf[f * _ch];
                buffer[offset + f * 2 + 1] = _buf[f * _ch + 1];
            }
            return got * 2;
        }
    }

    /// <summary>Komplett dekodierter Sound im Speicher (für sofortige Wiedergabe ohne Verzögerung).</summary>
    internal sealed class CachedSound
    {
        public static readonly TimeSpan MaxCached = TimeSpan.FromSeconds(90);
        public string Path;
        public float[] Data;
        public bool Streaming => Data == null;

        public static CachedSound Load(string path)
        {
            var sp = SoundLoader.Open(path, out var reader);
            using (reader)
            {
                if (reader.TotalTime > MaxCached) return new CachedSound { Path = path };
                long limit = (long)(MaxCached.TotalSeconds + 5) * AudioEngine.SampleRate * 2;
                var list = new List<float>((int)Math.Min(limit, Math.Max(4096, reader.TotalTime.TotalSeconds * AudioEngine.SampleRate * 2 + 4096)));
                var buf = new float[AudioEngine.SampleRate * 2];
                int n;
                while ((n = sp.Read(buf, 0, buf.Length)) > 0)
                {
                    list.AddRange(new ArraySegment<float>(buf, 0, n));
                    if (list.Count > limit) return new CachedSound { Path = path };
                }
                if (list.Count % 2 == 1) list.RemoveAt(list.Count - 1);
                return new CachedSound { Path = path, Data = list.ToArray() };
            }
        }
    }

    internal sealed class SoundCache
    {
        readonly ConcurrentDictionary<string, Lazy<CachedSound>> _items =
            new ConcurrentDictionary<string, Lazy<CachedSound>>(StringComparer.OrdinalIgnoreCase);

        public CachedSound Get(string path)
        {
            var lazy = _items.GetOrAdd(path, p => new Lazy<CachedSound>(() => CachedSound.Load(p), LazyThreadSafetyMode.ExecutionAndPublication));
            try { return lazy.Value; }
            catch
            {
                _items.TryRemove(path, out _);
                throw;
            }
        }

        public void Forget(string path)
        {
            if (path != null) _items.TryRemove(path, out _);
        }
    }

    /// <summary>Ein laufender Sound. Er kann gleichzeitig in mehrere Ausgänge (Kopfhörer + Voice-Chat) "angezapft" werden.</summary>
    public sealed class Voice
    {
        readonly Action<Voice> _ended;
        int _taps;

        internal Voice(SoundItem item, Action<Voice> ended)
        {
            Item = item;
            _ended = ended;
        }

        public SoundItem Item { get; }
        public volatile bool Stopped;
        public volatile bool Paused;

        internal void SetTaps(int n) => _taps = n;

        internal void TapDone()
        {
            if (Interlocked.Decrement(ref _taps) == 0) _ended?.Invoke(this);
        }

        public void Stop() => Stopped = true;
    }

    internal sealed class VoiceTap : ISampleProvider
    {
        const int FadeSamples = 960; // ~10 ms, verhindert Knacken beim Stoppen
        readonly Voice _voice;
        readonly AudioEngine _engine;
        readonly float[] _data;
        int _pos;
        readonly WaveStream _reader;
        readonly ISampleProvider _stream;
        bool _finished;
        int _fade = -1;

        public VoiceTap(Voice voice, AudioEngine engine, CachedSound cached)
        {
            _voice = voice;
            _engine = engine;
            if (cached.Streaming)
                _stream = SoundLoader.Open(cached.Path, out _reader);
            else
                _data = cached.Data;
        }

        public WaveFormat WaveFormat => AudioEngine.MixFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            if (_finished) return 0;
            if (_voice.Stopped && _fade < 0) _fade = FadeSamples;
            if (_voice.Paused && _fade < 0)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            int written;
            try { written = _data != null ? ReadCached(buffer, offset, count) : ReadStream(buffer, offset, count); }
            catch { written = 0; }

            float vol = _engine.VolumeFor(_voice.Item);
            if (_fade >= 0)
            {
                for (int i = 0; i < written; i++)
                {
                    float g = (float)_fade / FadeSamples;
                    buffer[offset + i] *= vol * g;
                    if (_fade > 0) _fade--;
                }
                if (_fade == 0) Finish();
            }
            else
            {
                for (int i = 0; i < written; i++) buffer[offset + i] *= vol;
            }

            if (written < count) Finish();
            return written;
        }

        int ReadCached(float[] buffer, int offset, int count)
        {
            int written = 0;
            while (written < count)
            {
                int avail = _data.Length - _pos;
                if (avail <= 0)
                {
                    if (_voice.Item.Loop && !_voice.Stopped && _data.Length > 0) { _pos = 0; continue; }
                    break;
                }
                int n = Math.Min(avail, count - written);
                Array.Copy(_data, _pos, buffer, offset + written, n);
                _pos += n;
                written += n;
            }
            return written;
        }

        int ReadStream(float[] buffer, int offset, int count)
        {
            int written = 0;
            int restarts = 0;
            while (written < count)
            {
                int n = _stream.Read(buffer, offset + written, count - written);
                if (n > 0) { written += n; continue; }
                if (_voice.Item.Loop && !_voice.Stopped && restarts++ < 2) { _reader.Position = 0; continue; }
                break;
            }
            return written;
        }

        void Finish()
        {
            if (_finished) return;
            _finished = true;
            try { _reader?.Dispose(); } catch { }
            _voice.TapDone();
        }
    }

    /// <summary>Misst den Spitzenpegel für die Anzeigen.</summary>
    internal sealed class PeakMeter : ISampleProvider
    {
        readonly ISampleProvider _src;
        float _peak;

        public PeakMeter(ISampleProvider src) { _src = src; }
        public WaveFormat WaveFormat => _src.WaveFormat;

        public float TakePeak()
        {
            float p = _peak;
            _peak = 0;
            return p;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = _src.Read(buffer, offset, count);
            float max = 0;
            for (int i = 0; i < n; i++)
            {
                float a = Math.Abs(buffer[offset + i]);
                if (a > max) max = a;
            }
            if (max > _peak) _peak = max;
            return n;
        }
    }

    internal sealed class GainProvider : ISampleProvider
    {
        readonly ISampleProvider _src;
        readonly Func<float> _gain;

        public GainProvider(ISampleProvider src, Func<float> gain) { _src = src; _gain = gain; }
        public WaveFormat WaveFormat => _src.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = _src.Read(buffer, offset, count);
            float g = _gain();
            if (g != 1f) for (int i = 0; i < n; i++) buffer[offset + i] *= g;
            return n;
        }
    }

    /// <summary>Puffer zwischen Mikrofon-Aufnahme und Voice-Chat-Ausgang (Stereo, Aufnahme-Samplerate).</summary>
    internal sealed class MicSource : ISampleProvider
    {
        readonly float[] _ring;
        readonly object _lock = new object();
        int _read, _write, _count;

        public MicSource(int sampleRate)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
            _ring = new float[sampleRate * 2]; // 1 Sekunde
        }

        public WaveFormat WaveFormat { get; }

        public void WriteFrames(float[] stereo, int samples)
        {
            lock (_lock)
            {
                for (int i = 0; i + 1 < samples; i += 2)
                {
                    if (_count >= _ring.Length - 1)
                    {
                        _read = (_read + 2) % _ring.Length;
                        _count -= 2;
                    }
                    _ring[_write] = stereo[i];
                    _ring[(_write + 1) % _ring.Length] = stereo[i + 1];
                    _write = (_write + 2) % _ring.Length;
                    _count += 2;
                }
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                // Latenz klein halten: mehr als ~150 ms Rückstand werden verworfen
                int maxBacklog = WaveFormat.SampleRate * 2 * 15 / 100;
                if (_count > maxBacklog)
                {
                    int drop = _count - WaveFormat.SampleRate * 2 * 4 / 100;
                    drop -= drop % 2;
                    _read = (_read + drop) % _ring.Length;
                    _count -= drop;
                }
                int n = Math.Min(count, _count);
                n -= n % 2;
                for (int i = 0; i < n; i++)
                {
                    buffer[offset + i] = _ring[_read];
                    _read = (_read + 1) % _ring.Length;
                }
                _count -= n;
                if (n < count) Array.Clear(buffer, offset + n, count - n);
            }
            return count;
        }
    }

    /// <summary>Mikrofonlautstärke und Stummschaltung für den Voice-Chat-Mix.</summary>
    internal sealed class MicGain : ISampleProvider
    {
        readonly ISampleProvider _src;
        readonly AudioEngine _engine;
        float _peak;

        public MicGain(ISampleProvider src, AudioEngine engine) { _src = src; _engine = engine; }
        public WaveFormat WaveFormat => _src.WaveFormat;

        public float TakePeak()
        {
            float p = _peak;
            _peak = 0;
            return p;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = _src.Read(buffer, offset, count);
            float g = _engine.MicVolume;
            float max = 0;
            for (int i = 0; i < n; i++)
            {
                float v = buffer[offset + i] * g;
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                buffer[offset + i] = v;
                float a = Math.Abs(v);
                if (a > max) max = a;
            }
            if (max > _peak) _peak = max;
            if (_engine.MicMuted) Array.Clear(buffer, offset, n);
            return n;
        }
    }
}
