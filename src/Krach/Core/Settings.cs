using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Krach.Core
{
    /// <summary>Was passiert, wenn die Taste eines Sounds gedrückt wird.</summary>
    public enum TriggerMode
    {
        Toggle = 0,   // 1. Druck startet, 2. Druck stoppt
        Restart = 1,  // jeder Druck startet von vorne
        Overlap = 2   // jeder Druck startet eine weitere Kopie
    }

    public enum KnobTurnAction { None, SoundVolume, MicVolume, MonitorVolume, SwitchPage, SelectSound, SwitchVoiceEffect }
    public enum KnobPressAction { None, StopAll, PauseResumeAll, ToggleMicMute, NextPage, PlaySelected, ToggleVoiceEffect }
    public enum VoiceEffect { None, Deep, High, Robot, Monster, Kid, Radio, Echo }

    public sealed class SoundItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Sound";
        public string FilePath { get; set; } = "";
        public double Volume { get; set; } = 1.0;
        public bool Loop { get; set; }
        public TriggerMode Mode { get; set; } = TriggerMode.Toggle;
        public string Color { get; set; } = "#7C5CFF";

        [JsonIgnore]
        public string ModeText => Mode switch
        {
            TriggerMode.Restart => "Neustart bei jedem Druck",
            TriggerMode.Overlap => "Überlagern",
            _ => "Start / Stopp"
        } + (Loop ? " · Endlosschleife" : "") + $" · {Math.Round(Volume * 100)} %";

        public override string ToString() => Name;
    }

    public sealed class SoundPage
    {
        public string Name { get; set; } = "Seite 1";
        public string[] Slots { get; set; } = new string[9];
        public override string ToString() => Name;
    }

    public sealed class KnobConfig
    {
        public KnobTurnAction Turn { get; set; } = KnobTurnAction.SoundVolume;
        public KnobPressAction Press { get; set; } = KnobPressAction.StopAll;
        public string LeftKey { get; set; }
        public string RightKey { get; set; }
        public string PressKey { get; set; }
    }

    public sealed class AppSettings
    {
        public int Version { get; set; } = 1;

        public List<SoundItem> Sounds { get; set; } = new List<SoundItem>();
        public List<SoundPage> Pages { get; set; } = new List<SoundPage>();
        public int CurrentPage { get; set; }

        /// <summary>Physische Taste (Scancode-ID) je Pad-Taste 1–9.</summary>
        public string[] ButtonKeys { get; set; } = new string[9];
        public KnobConfig[] Knobs { get; set; } = new KnobConfig[3];

        /// <summary>Stabile Geräteidentität, z. B. "VID_1189&amp;PID_8840" – nie die Interception-Nummer.</summary>
        public string DeviceKey { get; set; }
        public string DeviceName { get; set; }
        public bool BlockOriginalKeys { get; set; } = true;

        public double SoundVolume { get; set; } = 0.8;
        public double MonitorVolume { get; set; } = 1.0;
        public double MicVolume { get; set; } = 1.0;
        public bool MicMuted { get; set; }

        /// <summary>"" = Windows-Standard, "off" = nicht selbst hören.</summary>
        public string MonitorDeviceId { get; set; } = "";
        public string MixDeviceId { get; set; } = "";
        public string MicDeviceId { get; set; } = "";
        public bool MixEnabled { get; set; }
        public bool MicIntoMix { get; set; } = true;
        public bool SoundsIntoMix { get; set; } = true;
        public VoiceEffect VoiceEffect { get; set; } = VoiceEffect.Deep;
        public bool VoiceEffectOn { get; set; }

        public bool SingleSoundOnly { get; set; }
        public bool Autostart { get; set; }
        public bool AutostartToTray { get; set; } = true;
        public bool CloseToTray { get; set; } = true;
        public bool TrayHintShown { get; set; }

        public void Normalize()
        {
            Sounds ??= new List<SoundItem>();
            Sounds.RemoveAll(s => s == null);
            foreach (var s in Sounds)
            {
                if (string.IsNullOrWhiteSpace(s.Id)) s.Id = Guid.NewGuid().ToString("N");
                s.Name ??= "Sound";
                s.FilePath ??= "";
                s.Volume = Math.Clamp(s.Volume, 0, 1.5);
                if (string.IsNullOrWhiteSpace(s.Color)) s.Color = "#7C5CFF";
            }

            Pages ??= new List<SoundPage>();
            Pages.RemoveAll(p => p == null);
            if (Pages.Count == 0) Pages.Add(new SoundPage { Name = "Seite 1" });
            foreach (var p in Pages)
            {
                p.Name ??= "Seite";
                p.Slots = Fix(p.Slots, 9);
            }
            CurrentPage = Math.Clamp(CurrentPage, 0, Pages.Count - 1);

            ButtonKeys = Fix(ButtonKeys, 9);
            if (Knobs == null || Knobs.Length != 3)
            {
                var old = Knobs ?? Array.Empty<KnobConfig>();
                Knobs = new KnobConfig[3];
                for (int i = 0; i < 3 && i < old.Length; i++) Knobs[i] = old[i];
            }
            if (Knobs[0] == null) Knobs[0] = new KnobConfig { Turn = KnobTurnAction.SoundVolume, Press = KnobPressAction.StopAll };
            if (Knobs[1] == null) Knobs[1] = new KnobConfig { Turn = KnobTurnAction.MicVolume, Press = KnobPressAction.ToggleMicMute };
            if (Knobs[2] == null) Knobs[2] = new KnobConfig { Turn = KnobTurnAction.SelectSound, Press = KnobPressAction.PlaySelected };

            SoundVolume = Math.Clamp(SoundVolume, 0, 1);
            MonitorVolume = Math.Clamp(MonitorVolume, 0, 1);
            MicVolume = Math.Clamp(MicVolume, 0, 2);
            MonitorDeviceId ??= "";
            MixDeviceId ??= "";
            MicDeviceId ??= "";
        }

        static string[] Fix(string[] a, int n)
        {
            var r = new string[n];
            if (a != null) Array.Copy(a, r, Math.Min(n, a.Length));
            return r;
        }
    }

    public static class AppPaths
    {
        public static readonly string DataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Krach");
        public static readonly string SoundsDir = Path.Combine(DataDir, "Sounds");
        public static readonly string SettingsFile = Path.Combine(DataDir, "einstellungen.json");
        public static readonly string LogFile = Path.Combine(DataDir, "krach.log");
    }

    public static class SettingsStore
    {
        static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static AppSettings Load()
        {
            AppSettings s = null;
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                    s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options);
            }
            catch (Exception ex)
            {
                Log.Write("Einstellungen beschädigt, Sicherung wird angelegt: " + ex.Message);
                try { File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".defekt", true); } catch { }
            }
            s ??= new AppSettings();
            s.Normalize();
            return s;
        }

        public static void Save(AppSettings s)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                string tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
                File.Move(tmp, AppPaths.SettingsFile, true);
            }
            catch (Exception ex)
            {
                Log.Write("Speichern fehlgeschlagen: " + ex.Message);
            }
        }
    }

    public static class Log
    {
        static readonly object Lock = new object();

        public static void Write(string msg)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(AppPaths.DataDir);
                    var fi = new FileInfo(AppPaths.LogFile);
                    if (fi.Exists && fi.Length > 1_000_000)
                        File.Move(AppPaths.LogFile, AppPaths.LogFile + ".alt", true);
                    File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
