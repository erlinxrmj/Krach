using System;
using Krach.Core;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Krach.Audio
{
    /// <summary>
    /// Live-Stimmeffekte für das Mikrofon (48 kHz Stereo). Der Effekt kann während des Sprechens gewechselt werden.
    /// </summary>
    internal sealed class VoiceFx : ISampleProvider
    {
        readonly ISampleProvider _dry;
        readonly SmbPitchShiftingSampleProvider _pitch;
        readonly AudioEngine _engine;
        readonly BiQuadFilter[] _hp = new BiQuadFilter[2];
        readonly BiQuadFilter[] _lp = new BiQuadFilter[2];
        readonly BiQuadFilter[] _warm = new BiQuadFilter[2];
        readonly float[] _delay;
        int _delayPos;
        double _phase;
        VoiceEffect _last = VoiceEffect.None;

        public VoiceFx(ISampleProvider source, AudioEngine engine)
        {
            _engine = engine;
            _dry = source;
            _pitch = new SmbPitchShiftingSampleProvider(source, 2048, 4, 1f);
            int sr = source.WaveFormat.SampleRate;
            for (int c = 0; c < 2; c++)
            {
                _hp[c] = BiQuadFilter.HighPassFilter(sr, 450, 0.8f);
                _lp[c] = BiQuadFilter.LowPassFilter(sr, 2800, 0.9f);
                _warm[c] = BiQuadFilter.LowPassFilter(sr, 3200, 0.7f);
            }
            _delay = new float[sr * 2 * 280 / 1000 / 2 * 2]; // 280 ms Stereo
        }

        public WaveFormat WaveFormat => _dry.WaveFormat;

        static float PitchOf(VoiceEffect fx) => fx switch
        {
            VoiceEffect.Deep => 0.78f,
            VoiceEffect.High => 1.28f,
            VoiceEffect.Monster => 0.62f,
            VoiceEffect.Kid => 1.55f,
            _ => 1f
        };

        public int Read(float[] buffer, int offset, int count)
        {
            var fx = _engine.VoiceEffectActive ? _engine.VoiceEffect : VoiceEffect.None;
            if (fx != _last)
            {
                _last = fx;
                Array.Clear(_delay, 0, _delay.Length);
            }

            float pitch = PitchOf(fx);
            int n;
            if (pitch != 1f)
            {
                _pitch.PitchFactor = pitch;
                n = _pitch.Read(buffer, offset, count);
            }
            else
            {
                n = _dry.Read(buffer, offset, count);
            }

            switch (fx)
            {
                case VoiceEffect.Robot: Robot(buffer, offset, n); break;
                case VoiceEffect.Radio: Radio(buffer, offset, n); break;
                case VoiceEffect.Echo: Echo(buffer, offset, n); break;
                case VoiceEffect.Monster:
                    for (int i = 0; i < n; i++)
                    {
                        int c = i & 1;
                        float v = _warm[c].Transform(buffer[offset + i]) * 1.6f;
                        buffer[offset + i] = (float)Math.Tanh(v);
                    }
                    break;
            }
            return n;
        }

        void Robot(float[] b, int off, int n)
        {
            double step = 2 * Math.PI * 55.0 / WaveFormat.SampleRate;
            for (int i = 0; i < n; i += 2)
            {
                float m = (float)Math.Sin(_phase);
                _phase += step;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                b[off + i] = (float)Math.Tanh(b[off + i] * m * 2.2f);
                if (i + 1 < n) b[off + i + 1] = (float)Math.Tanh(b[off + i + 1] * m * 2.2f);
            }
        }

        void Radio(float[] b, int off, int n)
        {
            for (int i = 0; i < n; i++)
            {
                int c = i & 1;
                float v = _lp[c].Transform(_hp[c].Transform(b[off + i])) * 2.5f;
                b[off + i] = (float)Math.Tanh(v) * 0.8f;
            }
        }

        void Echo(float[] b, int off, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float d = _delay[_delayPos];
                float v = b[off + i] + d * 0.45f;
                _delay[_delayPos] = v;
                _delayPos = (_delayPos + 1) % _delay.Length;
                b[off + i] = Math.Clamp(v, -1f, 1f);
            }
        }
    }
}
