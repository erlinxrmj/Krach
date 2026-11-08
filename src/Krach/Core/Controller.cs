using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Krach.Audio;
using Krach.Input;

namespace Krach.Core
{
    /// <summary>Verbindet Einstellungen, Audio und Makropad-Eingabe.</summary>
    public sealed class Controller
    {
        public static readonly string[] Palette =
        {
            "#7C5CFF", "#3D8BFF", "#00B8D9", "#2ED47A", "#FFC233", "#FF8A3D", "#FF4D5E", "#FF5CC8", "#8E9AAF"
        };

        public const int TargetCount = 18; // 9 Tasten + 3 Regler × (links, rechts, drücken)

        readonly DispatcherTimer _saveTimer;

        public AppSettings S { get; }
        public AudioEngine Audio { get; }
        public InputManager Input { get; }

        public event Action Changed;          // Struktur geändert (Sounds, Seiten, Tasten, Geräte)
        public event Action LiveChanged;      // Lautstärke/Auswahl per Regler geändert
        public event Action LearnChanged;
        public event Action<string> Message;  // kurze Hinweise für die Oberfläche

        public int KnobSelectedSlot { get; private set; } = -1;
        public int LearnTarget { get; private set; } = -1;
        bool _learnSingle;

        public Controller(AppSettings s, AudioEngine audio, InputManager input, Dispatcher ui)
        {
            S = s;
            Audio = audio;
            Input = input;
            _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (o, e) => SaveNow(), ui);
            _saveTimer.Stop();
            Input.MacroKeyDown += OnMacroKey;
        }

        public SoundPage Page => S.Pages[S.CurrentPage];
        public SoundItem FindSound(string id) => id == null ? null : S.Sounds.FirstOrDefault(x => x.Id == id);
        public SoundItem SlotSound(int slot) => slot < 0 || slot > 8 ? null : FindSound(Page.Slots[slot]);

        public void SaveSoon() { _saveTimer.Stop(); _saveTimer.Start(); }
        public void SaveNow() { _saveTimer.Stop(); SettingsStore.Save(S); }
        public void NotifyChanged() { SaveSoon(); Changed?.Invoke(); }
        void Say(string msg) => Message?.Invoke(msg);

        // ---------------- Bibliothek ----------------

        public List<SoundItem> AddFiles(IEnumerable<string> files)
        {
            var added = new List<SoundItem>();
            Directory.CreateDirectory(AppPaths.SoundsDir);
            foreach (var f in files)
            {
                try
                {
                    if (Directory.Exists(f))
                    {
                        added.AddRange(AddFilesInternal(Directory.EnumerateFiles(f).Where(SoundLoader.IsSupported)));
                        continue;
                    }
                    added.AddRange(AddFilesInternal(new[] { f }));
                }
                catch (Exception ex)
                {
                    Say($"„{Path.GetFileName(f)}“ konnte nicht hinzugefügt werden: {ex.Message}");
                }
            }
            if (added.Count > 0)
            {
                Audio.Preload(added.Select(a => a.FilePath));
                NotifyChanged();
            }
            return added;
        }

        IEnumerable<SoundItem> AddFilesInternal(IEnumerable<string> files)
        {
            var result = new List<SoundItem>();
            foreach (var f in files)
            {
                if (!SoundLoader.IsSupported(f))
                {
                    Say($"„{Path.GetFileName(f)}“ wird nicht unterstützt (MP3, WAV, OGG, FLAC, M4A).");
                    continue;
                }
                string full = Path.GetFullPath(f);
                string dest = full;
                if (!full.StartsWith(AppPaths.SoundsDir, StringComparison.OrdinalIgnoreCase))
                {
                    dest = UniquePath(Path.Combine(AppPaths.SoundsDir, Path.GetFileName(full)));
                    File.Copy(full, dest);
                }
                var item = new SoundItem
                {
                    Name = Path.GetFileNameWithoutExtension(full),
                    FilePath = dest,
                    Color = Palette[S.Sounds.Count % Palette.Length]
                };
                S.Sounds.Add(item);
                result.Add(item);
            }
            return result;
        }

        static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string p = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(p)) return p;
            }
        }

        public void DeleteSound(SoundItem item)
        {
            if (item == null) return;
            Audio.StopSound(item.Id);
            S.Sounds.Remove(item);
            foreach (var p in S.Pages)
                for (int i = 0; i < 9; i++)
                    if (p.Slots[i] == item.Id) p.Slots[i] = null;
            Audio.Forget(item.FilePath);
            try
            {
                if (item.FilePath.StartsWith(AppPaths.SoundsDir, StringComparison.OrdinalIgnoreCase) &&
                    !S.Sounds.Any(s => string.Equals(s.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)))
                    File.Delete(item.FilePath);
            }
            catch { }
            NotifyChanged();
        }

        public string UsageOf(SoundItem item)
        {
            if (item == null) return "";
            var uses = new List<string>();
            for (int p = 0; p < S.Pages.Count; p++)
                for (int i = 0; i < 9; i++)
                    if (S.Pages[p].Slots[i] == item.Id) uses.Add($"{S.Pages[p].Name} · Taste {i + 1}");
            return uses.Count == 0 ? "Noch keiner Taste zugewiesen" : "Zugewiesen: " + string.Join(", ", uses);
        }

        public void AssignSlot(int slot, SoundItem item)
        {
            if (slot < 0 || slot > 8) return;
            Page.Slots[slot] = item?.Id;
            NotifyChanged();
        }

        /// <summary>Weist Dateien ab der gewünschten Taste den nächsten freien Tasten zu.</summary>
        public void AssignFilesToSlots(int startSlot, IEnumerable<string> files)
        {
            var added = AddFiles(files);
            if (added.Count == 0) return;
            Page.Slots[startSlot] = added[0].Id;
            int next = 0;
            foreach (var item in added.Skip(1))
            {
                while (next < 9 && Page.Slots[next] != null) next++;
                if (next >= 9) break;
                Page.Slots[next] = item.Id;
            }
            NotifyChanged();
        }

        // ---------------- Seiten ----------------

        public void SetPage(int index)
        {
            if (index < 0 || index >= S.Pages.Count || index == S.CurrentPage) return;
            S.CurrentPage = index;
            NotifyChanged();
        }

        public void AddPage()
        {
            S.Pages.Add(new SoundPage { Name = $"Seite {S.Pages.Count + 1}" });
            S.CurrentPage = S.Pages.Count - 1;
            NotifyChanged();
        }

        public void DeletePage()
        {
            if (S.Pages.Count <= 1) { Say("Die letzte Seite kann nicht gelöscht werden."); return; }
            S.Pages.RemoveAt(S.CurrentPage);
            S.CurrentPage = Math.Clamp(S.CurrentPage, 0, S.Pages.Count - 1);
            NotifyChanged();
        }

        public void RenamePage(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name == Page.Name) return;
            Page.Name = name;
            NotifyChanged();
        }

        // ---------------- Wiedergabe ----------------

        public void TriggerSlot(int slot)
        {
            var item = SlotSound(slot);
            if (item != null) Play(item);
        }

        public void Play(SoundItem item)
        {
            try { Audio.Trigger(item); }
            catch (Exception ex) { Say($"„{item.Name}“ konnte nicht abgespielt werden: {ex.Message}"); }
        }

        public void SetSoundVolume(double v)
        {
            S.SoundVolume = Math.Clamp(v, 0, 1);
            Audio.SoundVolume = (float)S.SoundVolume;
            SaveSoon();
        }

        public void SetMonitorVolume(double v)
        {
            S.MonitorVolume = Math.Clamp(v, 0, 1);
            Audio.MonitorVolume = (float)S.MonitorVolume;
            SaveSoon();
        }

        public void SetMicVolume(double v)
        {
            S.MicVolume = Math.Clamp(v, 0, 2);
            Audio.MicVolume = (float)S.MicVolume;
            SaveSoon();
        }

        public void SetMicMuted(bool muted)
        {
            S.MicMuted = muted;
            Audio.MicMuted = muted;
            SaveSoon();
        }

        public void SetVoiceEffect(VoiceEffect fx, bool on)
        {
            if (fx == VoiceEffect.None) { on = false; fx = S.VoiceEffect == VoiceEffect.None ? VoiceEffect.Deep : S.VoiceEffect; }
            S.VoiceEffect = fx;
            S.VoiceEffectOn = on;
            Audio.VoiceEffect = fx;
            Audio.VoiceEffectActive = on;
            SaveSoon();
        }

        public static string EffectName(VoiceEffect fx) => fx switch
        {
            VoiceEffect.Deep => "Tief",
            VoiceEffect.High => "Hoch",
            VoiceEffect.Robot => "Roboter",
            VoiceEffect.Monster => "Monster",
            VoiceEffect.Kid => "Kind",
            VoiceEffect.Radio => "Funk / Radio",
            VoiceEffect.Echo => "Echo",
            _ => "Aus"
        };

        public void ApplyAudio()
        {
            Audio.Apply(S);
            NotifyChanged();
        }

        // ---------------- Makropad ----------------

        public void SelectDevice(PadDevice dev)
        {
            S.DeviceKey = dev?.Key;
            S.DeviceName = dev?.Name;
            Input.Configure(S.DeviceKey, S.BlockOriginalKeys);
            Input.RefreshDevices();
            NotifyChanged();
        }

        public void SetBlocking(bool block)
        {
            S.BlockOriginalKeys = block;
            Input.Configure(S.DeviceKey, block);
            NotifyChanged();
        }

        public void ToggleEmergency() => Input.SetEmergency(!Input.Emergency);

        void OnMacroKey(string keyId)
        {
            if (LearnTarget >= 0)
            {
                AssignKey(LearnTarget, keyId);
                AdvanceLearn();
                return;
            }

            for (int i = 0; i < 9; i++)
            {
                if (S.ButtonKeys[i] == keyId)
                {
                    TriggerSlot(i);
                    return;
                }
            }
            for (int k = 0; k < 3; k++)
            {
                var knob = S.Knobs[k];
                if (knob.LeftKey == keyId) { Turn(k, -1); return; }
                if (knob.RightKey == keyId) { Turn(k, +1); return; }
                if (knob.PressKey == keyId) { Press(k); return; }
            }
        }

        public void Turn(int knob, int dir)
        {
            switch (S.Knobs[knob].Turn)
            {
                case KnobTurnAction.SoundVolume: SetSoundVolume(S.SoundVolume + dir * 0.05); break;
                case KnobTurnAction.MicVolume: SetMicVolume(S.MicVolume + dir * 0.05); break;
                case KnobTurnAction.MonitorVolume: SetMonitorVolume(S.MonitorVolume + dir * 0.05); break;
                case KnobTurnAction.SwitchPage:
                    int n = S.Pages.Count;
                    S.CurrentPage = ((S.CurrentPage + dir) % n + n) % n;
                    NotifyChanged();
                    return;
                case KnobTurnAction.SwitchVoiceEffect:
                    {
                        int count = Enum.GetValues(typeof(VoiceEffect)).Length;
                        int cur = (int)S.VoiceEffect;
                        int next = ((cur - 1 + dir) % (count - 1) + (count - 1)) % (count - 1) + 1; // "Aus" überspringen
                        SetVoiceEffect((VoiceEffect)next, true);
                    }
                    break;
                case KnobTurnAction.SelectSound:
                    KnobSelectedSlot = KnobSelectedSlot < 0 ? (dir > 0 ? 0 : 8) : ((KnobSelectedSlot + dir) % 9 + 9) % 9;
                    break;
                default: return;
            }
            LiveChanged?.Invoke();
        }

        public void Press(int knob)
        {
            switch (S.Knobs[knob].Press)
            {
                case KnobPressAction.StopAll: Audio.StopAll(); break;
                case KnobPressAction.PauseResumeAll: Audio.TogglePauseAll(); break;
                case KnobPressAction.ToggleMicMute: SetMicMuted(!S.MicMuted); LiveChanged?.Invoke(); break;
                case KnobPressAction.NextPage: S.CurrentPage = (S.CurrentPage + 1) % S.Pages.Count; NotifyChanged(); break;
                case KnobPressAction.ToggleVoiceEffect: SetVoiceEffect(S.VoiceEffect, !S.VoiceEffectOn); LiveChanged?.Invoke(); break;
                case KnobPressAction.PlaySelected: if (KnobSelectedSlot >= 0) TriggerSlot(KnobSelectedSlot); break;
            }
        }

        // ---------------- Anlernen ----------------

        public static string TargetName(int t)
        {
            if (t < 9)
            {
                string[] pos = { "oben links", "oben Mitte", "oben rechts", "Mitte links", "Mitte", "Mitte rechts", "unten links", "unten Mitte", "unten rechts" };
                return $"Taste {t + 1} ({pos[t]})";
            }
            int k = (t - 9) / 3, part = (t - 9) % 3;
            string[] parts = { "nach links drehen", "nach rechts drehen", "drücken" };
            return $"Regler {k + 1} {parts[part]}";
        }

        public string GetTargetKey(int t)
        {
            if (t < 9) return S.ButtonKeys[t];
            var knob = S.Knobs[(t - 9) / 3];
            return ((t - 9) % 3) switch { 0 => knob.LeftKey, 1 => knob.RightKey, _ => knob.PressKey };
        }

        void SetTargetKey(int t, string key)
        {
            if (t < 9) { S.ButtonKeys[t] = key; return; }
            var knob = S.Knobs[(t - 9) / 3];
            switch ((t - 9) % 3)
            {
                case 0: knob.LeftKey = key; break;
                case 1: knob.RightKey = key; break;
                default: knob.PressKey = key; break;
            }
        }

        void AssignKey(int target, string key)
        {
            for (int t = 0; t < TargetCount; t++)
                if (t != target && GetTargetKey(t) == key) SetTargetKey(t, null); // doppelte Belegung auflösen
            SetTargetKey(target, key);
        }

        public void ClearTargetKey(int target)
        {
            SetTargetKey(target, null);
            NotifyChanged();
        }

        public void StartLearn(int target, bool single)
        {
            if (string.IsNullOrEmpty(S.DeviceKey))
            {
                Say("Bitte wähle zuerst unter „Makropad“ dein Gerät aus.");
                return;
            }
            if (Input.Emergency)
            {
                Say("Notaus ist aktiv – bitte zuerst die Eingabe wieder aktivieren.");
                return;
            }
            LearnTarget = target;
            _learnSingle = single;
            LearnChanged?.Invoke();
        }

        public void SkipLearn()
        {
            if (LearnTarget >= 0) AdvanceLearn();
        }

        public void CancelLearn()
        {
            LearnTarget = -1;
            NotifyChanged();
            LearnChanged?.Invoke();
        }

        void AdvanceLearn()
        {
            if (_learnSingle || LearnTarget >= TargetCount - 1)
            {
                LearnTarget = -1;
                Say("Anlernen abgeschlossen.");
                NotifyChanged();
            }
            else
            {
                LearnTarget++;
                SaveSoon();
            }
            LearnChanged?.Invoke();
        }

        public bool HasAnyButtonKeys => S.ButtonKeys.Any(k => k != null);
    }
}
