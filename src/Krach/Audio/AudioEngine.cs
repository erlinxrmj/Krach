using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Krach.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Krach.Audio
{
    public sealed class AudioDeviceEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsVirtual { get; set; }
        public override string ToString() => Name;
    }

    public static class AudioDevices
    {
        public const string DefaultId = "";
        public const string OffId = "off";

        static readonly string[] VirtualHints = { "cable", "vb-audio", "voicemeeter", "virtual", "vac " };

        public static bool IsVirtualName(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            return VirtualHints.Any(h => n.Contains(h));
        }

        public static List<AudioDeviceEntry> List(DataFlow flow)
        {
            var list = new List<AudioDeviceEntry>();
            try
            {
                using var en = new MMDeviceEnumerator();
                foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    try { list.Add(new AudioDeviceEntry { Id = d.ID, Name = d.FriendlyName, IsVirtual = IsVirtualName(d.FriendlyName) }); }
                    catch { }
                    finally { d.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Audiogeräte konnten nicht gelesen werden: " + ex.Message);
            }
            return list.OrderBy(d => d.Name).ToList();
        }

        /// <summary>Bestes virtuelles Kabel zum Einspeisen (z. B. "CABLE Input (VB-Audio Virtual Cable)").</summary>
        public static AudioDeviceEntry FindVirtualCableInput(List<AudioDeviceEntry> render)
        {
            return render.FirstOrDefault(d => d.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? render.FirstOrDefault(d => d.Name.IndexOf("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? render.FirstOrDefault(d => d.IsVirtual);
        }

        /// <summary>True, wenn das Mikrofon die Gegenseite des Kabels ist, in das Krach einspeist (Rückkopplung).</summary>
        public static bool IsLoopback(string micName, string mixName)
        {
            if (string.IsNullOrEmpty(micName) || string.IsNullOrEmpty(mixName)) return false;
            string a = micName.Split('(')[0].Trim().Replace("Output", "").Replace("Out", "").Trim();
            string b = mixName.Split('(')[0].Trim().Replace("Input", "").Replace("In", "").Trim();
            string ta = micName.Contains('(') ? micName.Substring(micName.IndexOf('(')) : "";
            string tb = mixName.Contains('(') ? mixName.Substring(mixName.IndexOf('(')) : "";
            return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase) && string.Equals(ta, tb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Das Aufnahmegerät, das Discord als "Mikrofon" auswählen muss.</summary>
        public static AudioDeviceEntry FindPartnerCapture(string renderName, List<AudioDeviceEntry> capture)
        {
            if (string.IsNullOrEmpty(renderName)) return null;
            if (renderName.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string prefix = renderName.Split('(')[0].Trim().Replace("Input", "Output");
                return capture.FirstOrDefault(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    ?? capture.FirstOrDefault(c => c.Name.IndexOf("CABLE Output", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (renderName.IndexOf("VoiceMeeter", StringComparison.OrdinalIgnoreCase) >= 0)
                return capture.FirstOrDefault(c => c.Name.IndexOf("VoiceMeeter Output", StringComparison.OrdinalIgnoreCase) >= 0
                                                || c.Name.IndexOf("VoiceMeeter Out", StringComparison.OrdinalIgnoreCase) >= 0);
            return capture.FirstOrDefault(c => c.IsVirtual);
        }
    }

    sealed class DeviceNotifier : IMMNotificationClient
    {
        readonly Action _changed;
        public DeviceNotifier(Action changed) { _changed = changed; }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _changed();
        public void OnDeviceAdded(string pwstrDeviceId) => _changed();
        public void OnDeviceRemoved(string deviceId) => _changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _changed();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    /// <summary>
    /// Zwei unabhängige Ausgänge:
    ///  • Mithören: Sounds auf Kopfhörer/Lautsprecher
    ///  • Voice-Chat: Mikrofon + Sounds gemischt auf ein virtuelles Kabel → Discord nimmt dessen "Output" als Mikrofon
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        public const int SampleRate = 48000;
        public static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

        readonly Dispatcher _ui;
        readonly SoundCache _cache = new SoundCache();
        readonly List<Voice> _voices = new List<Voice>();
        readonly MMDeviceEnumerator _notifyEnum;
        readonly DeviceNotifier _notifier;
        readonly DispatcherTimer _deviceDebounce;

        MixingSampleProvider _monitorMix, _chatMix;
        WasapiOut _monitorOut, _chatOut;
        PeakMeter _monitorMeter, _chatMeter;
        WasapiCapture _capture;
        MicSource _mic;
        MicGain _micGain;
        AppSettings _settings;
        bool _soundsToChat;
        bool _singleOnly;
        bool _needsRestart;

        public float SoundVolume = 0.8f;
        public float MonitorVolume = 1f;
        public float MicVolume = 1f;
        public bool MicMuted;
        public VoiceEffect VoiceEffect = VoiceEffect.Deep;
        public bool VoiceEffectActive;

        public string MonitorName { get; private set; }
        public string ChatName { get; private set; }
        public string MicName { get; private set; }
        public List<string> Problems { get; } = new List<string>();

        public event Action StateChanged;        // Ausgänge neu gestartet / Fehler
        public event Action VoicesChanged;       // ein Sound startet/endet
        public event Action DevicesChanged;      // Windows-Audiogeräte haben sich geändert

        public AudioEngine(Dispatcher ui)
        {
            _ui = ui;
            _deviceDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(1200), DispatcherPriority.Background, (s, e) =>
            {
                _deviceDebounce.Stop();
                DevicesChanged?.Invoke();
                if (_needsRestart && _settings != null) Apply(_settings);
            }, _ui);
            _deviceDebounce.Stop();

            try
            {
                _notifyEnum = new MMDeviceEnumerator();
                _notifier = new DeviceNotifier(() => _ui.BeginInvoke(new Action(() => { _deviceDebounce.Stop(); _deviceDebounce.Start(); })));
                _notifyEnum.RegisterEndpointNotificationCallback(_notifier);
            }
            catch (Exception ex)
            {
                Log.Write("Geräteüberwachung nicht verfügbar: " + ex.Message);
            }
        }

        internal float VolumeFor(SoundItem item) => (float)(item.Volume * SoundVolume);

        public bool SingleOnly { get => _singleOnly; set => _singleOnly = value; }
        public bool ChatActive => _chatOut != null;
        public bool MonitorActive => _monitorOut != null;
        public bool MicActive => _capture != null;

        public float TakeMonitorPeak() => _monitorMeter?.TakePeak() ?? 0f;
        public float TakeChatPeak() => _chatMeter?.TakePeak() ?? 0f;
        public float TakeMicPeak() => _micGain?.TakePeak() ?? 0f;

        /// <summary>(Re)startet alle Ausgänge gemäß Einstellungen.</summary>
        public void Apply(AppSettings s)
        {
            _settings = s;
            StopOutputs();
            Problems.Clear();
            _needsRestart = false;

            SoundVolume = (float)s.SoundVolume;
            MonitorVolume = (float)s.MonitorVolume;
            MicVolume = (float)s.MicVolume;
            MicMuted = s.MicMuted;
            VoiceEffect = s.VoiceEffect;
            VoiceEffectActive = s.VoiceEffectOn;
            _singleOnly = s.SingleSoundOnly;
            _soundsToChat = s.SoundsIntoMix;

            using var en = new MMDeviceEnumerator();

            // 1) Mithören
            if (s.MonitorDeviceId != AudioDevices.OffId)
            {
                try
                {
                    var dev = GetDevice(en, s.MonitorDeviceId, DataFlow.Render);
                    var mix = new MixingSampleProvider(MixFormat) { ReadFully = true };
                    var meter = new PeakMeter(new GainProvider(mix, () => MonitorVolume));
                    var output = new WasapiOut(dev, AudioClientShareMode.Shared, true, 40);
                    output.Init(new SampleToWaveProvider(meter));
                    output.PlaybackStopped += OnPlaybackStopped;
                    output.Play();
                    _monitorMix = mix;
                    _monitorMeter = meter;
                    _monitorOut = output;
                    MonitorName = dev.FriendlyName;
                }
                catch (Exception ex)
                {
                    Problems.Add("Kopfhörer/Lautsprecher konnten nicht geöffnet werden: " + ex.Message);
                    _needsRestart = true;
                    Log.Write("Monitor-Ausgabe: " + ex);
                }
            }

            // 2) Voice-Chat (virtuelles Kabel)
            if (s.MixEnabled)
            {
                if (string.IsNullOrEmpty(s.MixDeviceId))
                {
                    Problems.Add("Voice-Chat-Modus: Bitte ein virtuelles Audiokabel als Ausgang wählen.");
                }
                else
                {
                    try
                    {
                        var dev = en.GetDevice(s.MixDeviceId);
                        if (dev.State != DeviceState.Active) throw new InvalidOperationException("Gerät ist nicht aktiv/angeschlossen.");
                        var mix = new MixingSampleProvider(MixFormat) { ReadFully = true };

                        if (s.MicIntoMix) StartMic(en, s, mix);

                        var meter = new PeakMeter(mix);
                        var output = new WasapiOut(dev, AudioClientShareMode.Shared, true, 30);
                        output.Init(new SampleToWaveProvider(meter));
                        output.PlaybackStopped += OnPlaybackStopped;
                        output.Play();
                        _chatMix = mix;
                        _chatMeter = meter;
                        _chatOut = output;
                        ChatName = dev.FriendlyName;
                    }
                    catch (Exception ex)
                    {
                        StopMic();
                        Problems.Add("Virtuelles Kabel konnte nicht geöffnet werden: " + ex.Message);
                        _needsRestart = true;
                        Log.Write("Voice-Chat-Ausgabe: " + ex);
                    }
                }
            }

            StateChanged?.Invoke();
        }

        void StartMic(MMDeviceEnumerator en, AppSettings s, MixingSampleProvider chatMix)
        {
            try
            {
                MMDevice micDev = GetDevice(en, s.MicDeviceId, DataFlow.Capture);
                if (AudioDevices.IsLoopback(micDev.FriendlyName, ChatNameFor(en, s.MixDeviceId)))
                {
                    Problems.Add("Als Mikrofon ist genau das Kabel eingestellt, in das Krach selbst einspeist – das erzeugt eine Rückkopplung. Bitte dein echtes Mikrofon (oder den Ausgang deines KI-Voice-Changers) wählen.");
                }
                var capture = new WasapiCapture(micDev, false, 30);
                var mic = new MicSource(capture.WaveFormat.SampleRate);
                var fmt = capture.WaveFormat;
                bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                               (fmt is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);
                int bytesPerSample = fmt.BitsPerSample / 8;
                int channels = fmt.Channels;
                int block = fmt.BlockAlign;
                float[] tmp = Array.Empty<float>();

                capture.DataAvailable += (o, e) =>
                {
                    int frames = e.BytesRecorded / block;
                    if (frames <= 0) return;
                    if (tmp.Length < frames * 2) tmp = new float[frames * 2];
                    for (int f = 0; f < frames; f++)
                    {
                        int off = f * block;
                        float l = ReadSample(e.Buffer, off, bytesPerSample, isFloat);
                        float r = channels > 1 ? ReadSample(e.Buffer, off + bytesPerSample, bytesPerSample, isFloat) : l;
                        tmp[f * 2] = l;
                        tmp[f * 2 + 1] = r;
                    }
                    mic.WriteFrames(tmp, frames * 2);
                };
                capture.RecordingStopped += (o, e) =>
                {
                    if (e.Exception != null)
                        _ui.BeginInvoke(new Action(() =>
                        {
                            Problems.Add("Mikrofon getrennt: " + e.Exception.Message);
                            _needsRestart = true;
                            StateChanged?.Invoke();
                        }));
                };

                ISampleProvider chain = mic;
                if (mic.WaveFormat.SampleRate != SampleRate) chain = new WdlResamplingSampleProvider(chain, SampleRate);
                chain = new VoiceFx(chain, this);
                var gain = new MicGain(chain, this);
                chatMix.AddMixerInput(gain);
                capture.StartRecording();

                _capture = capture;
                _mic = mic;
                _micGain = gain;
                MicName = micDev.FriendlyName;
            }
            catch (Exception ex)
            {
                Problems.Add("Mikrofon konnte nicht geöffnet werden: " + ex.Message);
                _needsRestart = true;
                Log.Write("Mikrofon: " + ex);
            }
        }

        static string ChatNameFor(MMDeviceEnumerator en, string id)
        {
            try { return en.GetDevice(id).FriendlyName; } catch { return null; }
        }

        static readonly Guid FloatSubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        static float ReadSample(byte[] b, int off, int bytes, bool isFloat)
        {
            switch (bytes)
            {
                case 4:
                    return isFloat ? BitConverter.ToSingle(b, off) : BitConverter.ToInt32(b, off) / 2147483648f;
                case 3:
                    return ((b[off] | (b[off + 1] << 8) | ((sbyte)b[off + 2] << 16))) / 8388608f;
                case 2:
                    return BitConverter.ToInt16(b, off) / 32768f;
                case 8:
                    return (float)BitConverter.ToDouble(b, off);
                default:
                    return 0f;
            }
        }

        static MMDevice GetDevice(MMDeviceEnumerator en, string id, DataFlow flow)
        {
            if (!string.IsNullOrEmpty(id) && id != AudioDevices.OffId)
            {
                try
                {
                    var d = en.GetDevice(id);
                    if (d.State == DeviceState.Active) return d;
                }
                catch { }
            }
            return en.GetDefaultAudioEndpoint(flow, flow == DataFlow.Capture ? Role.Communications : Role.Multimedia);
        }

        void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception == null) return;
            Log.Write("Wiedergabe unterbrochen: " + e.Exception.Message);
            _ui.BeginInvoke(new Action(() =>
            {
                Problems.Add("Audiogerät getrennt – es wird automatisch neu verbunden, sobald es wieder da ist.");
                _needsRestart = true;
                StateChanged?.Invoke();
            }));
        }

        void StopOutputs()
        {
            lock (_voices)
            {
                foreach (var v in _voices) v.Stopped = true;
                _voices.Clear();
            }
            StopMic();
            if (_monitorOut != null)
            {
                _monitorOut.PlaybackStopped -= OnPlaybackStopped;
                try { _monitorOut.Stop(); } catch { }
                try { _monitorOut.Dispose(); } catch { }
            }
            if (_chatOut != null)
            {
                _chatOut.PlaybackStopped -= OnPlaybackStopped;
                try { _chatOut.Stop(); } catch { }
                try { _chatOut.Dispose(); } catch { }
            }
            _monitorOut = _chatOut = null;
            _monitorMix = _chatMix = null;
            _monitorMeter = _chatMeter = null;
            MonitorName = ChatName = null;
            VoicesChanged?.Invoke();
        }

        void StopMic()
        {
            if (_capture != null)
            {
                try { _capture.StopRecording(); } catch { }
                try { _capture.Dispose(); } catch { }
            }
            _capture = null;
            _mic = null;
            _micGain = null;
            MicName = null;
        }

        // ---------------- Wiedergabe ----------------

        public void Preload(IEnumerable<string> paths)
        {
            var list = paths.Where(p => !string.IsNullOrEmpty(p)).ToList();
            Task.Run(() =>
            {
                foreach (var p in list)
                {
                    try { if (File.Exists(p)) _cache.Get(p); }
                    catch (Exception ex) { Log.Write($"Vorladen fehlgeschlagen ({p}): {ex.Message}"); }
                }
            });
        }

        public void Forget(string path) => _cache.Forget(path);

        /// <summary>Spielt einen Sound gemäß seinem Modus (Start/Stopp, Neustart, Überlagern).</summary>
        public void Trigger(SoundItem item)
        {
            if (item == null) return;
            List<Voice> existing;
            lock (_voices) existing = _voices.Where(v => v.Item.Id == item.Id && !v.Stopped).ToList();

            if (item.Mode == TriggerMode.Toggle && existing.Count > 0)
            {
                foreach (var v in existing) v.Stop();
                return;
            }
            if (item.Mode == TriggerMode.Restart)
                foreach (var v in existing) v.Stop();

            if (_singleOnly) StopAll();
            Play(item);
        }

        public void Play(SoundItem item)
        {
            if (!File.Exists(item.FilePath))
                throw new FileNotFoundException("Die Sounddatei fehlt.", item.FilePath);
            if (_monitorMix == null && (_chatMix == null || !_soundsToChat))
                throw new InvalidOperationException("Kein Audioausgang aktiv – bitte unter „Audio“ ein Gerät wählen.");

            var cached = _cache.Get(item.FilePath);
            var voice = new Voice(item, OnVoiceEnded);
            var taps = new List<(MixingSampleProvider Mix, VoiceTap Tap)>();
            if (_monitorMix != null) taps.Add((_monitorMix, new VoiceTap(voice, this, cached)));
            if (_chatMix != null && _soundsToChat) taps.Add((_chatMix, new VoiceTap(voice, this, cached)));
            voice.SetTaps(taps.Count);
            lock (_voices) _voices.Add(voice);
            foreach (var t in taps) t.Mix.AddMixerInput(t.Tap);
            VoicesChanged?.Invoke();
        }

        void OnVoiceEnded(Voice v)
        {
            lock (_voices) _voices.Remove(v);
            _ui.BeginInvoke(new Action(() => VoicesChanged?.Invoke()));
        }

        public void StopAll()
        {
            lock (_voices) foreach (var v in _voices) v.Stop();
        }

        public void StopSound(string id)
        {
            lock (_voices) foreach (var v in _voices) if (v.Item.Id == id) v.Stop();
        }

        /// <summary>Pausiert alle laufenden Sounds oder setzt sie fort.</summary>
        public void TogglePauseAll()
        {
            lock (_voices)
            {
                bool anyRunning = _voices.Any(v => !v.Stopped && !v.Paused);
                foreach (var v in _voices) v.Paused = anyRunning;
            }
            VoicesChanged?.Invoke();
        }

        public bool AnyPaused { get { lock (_voices) return _voices.Any(v => v.Paused && !v.Stopped); } }
        public int PlayingCount { get { lock (_voices) return _voices.Count(v => !v.Stopped); } }

        /// <summary>0 = aus, 1 = spielt, 2 = pausiert.</summary>
        public int StateOf(string soundId)
        {
            if (soundId == null) return 0;
            lock (_voices)
            {
                var v = _voices.FirstOrDefault(x => x.Item.Id == soundId && !x.Stopped);
                return v == null ? 0 : v.Paused ? 2 : 1;
            }
        }

        public void Dispose()
        {
            try { StopOutputs(); } catch { }
            try { if (_notifier != null) _notifyEnum?.UnregisterEndpointNotificationCallback(_notifier); } catch { }
            try { _notifyEnum?.Dispose(); } catch { }
        }
    }
}
