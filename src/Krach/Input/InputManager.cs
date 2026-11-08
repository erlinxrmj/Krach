using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Krach.Core;

namespace Krach.Input
{
    public sealed class PadDevice
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public List<string> Paths { get; } = new List<string>();
        public bool IsMacropadGuess { get; set; }
        public bool IsSelected { get; set; }

        public string Subtitle => DeviceIds.VidPidDisplay(Key) + (Paths.Count > 1 ? $" · {Paths.Count} Schnittstellen" : "");
        public string Badge => IsSelected ? "Ausgewählt" : IsMacropadGuess ? "Makropad erkannt" : "";
        public Visibility BadgeVisibility => string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;
        public override string ToString() => Name;
    }

    public sealed class KeyObservation
    {
        public string DeviceKey;
        public string DeviceName;
        public string KeyId;
        public string KeyName;
        public bool Down;
        public bool FromTarget;
        public bool ViaInterception;
    }

    /// <summary>
    /// Verbindet Raw Input (Erkennung, immer an) und Interception (optional: nur das Makropad blockieren).
    /// Nur Tasten des ausgewählten Geräts werden als Makropad-Tasten weitergegeben.
    /// </summary>
    public sealed class InputManager : IDisposable
    {
        readonly Dispatcher _ui;
        readonly RawInputWindow _raw;
        readonly Dictionary<IntPtr, string> _handleKeys = new Dictionary<IntPtr, string>();
        readonly HashSet<string> _pressed = new HashSet<string>();
        readonly DispatcherTimer _deviceDebounce;
        InterceptionBackend _icp;
        string _target;
        bool _block;

        public event Action<string> MacroKeyDown;
        public event Action<KeyObservation> KeyObserved;
        public event Action<PadDevice> DeviceDetected;
        public event Action StateChanged;
        public event Action DevicesChanged;

        public List<PadDevice> Devices { get; private set; } = new List<PadDevice>();
        public bool Emergency { get; private set; }
        public InterceptionStatus IcpStatus { get; private set; } = InterceptionStatus.NotUsed;
        public int IcpFiltered { get; private set; }
        public bool IcpActive => _icp != null;
        public bool HotkeyOk => _raw.HotkeyRegistered;
        public bool RawOk { get; private set; }
        public bool DetectNext { get; set; }
        public string TargetKey => _target;

        public InputManager(Dispatcher ui)
        {
            _ui = ui;
            _raw = new RawInputWindow();
            _raw.Key += OnRawKey;
            _raw.Hotkey += () => SetEmergency(!Emergency);
            _raw.DevicesChanged += () => { _deviceDebounce.Stop(); _deviceDebounce.Start(); };
            _deviceDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(900), DispatcherPriority.Background, (s, e) =>
            {
                _deviceDebounce.Stop();
                RefreshDevices();
                if (_icp != null)
                {
                    _icp.RefreshHardwareIds();
                    IcpFiltered = _icp.SetTarget(_target);
                }
                StateChanged?.Invoke();
            }, _ui);
            _deviceDebounce.Stop();
        }

        public void RefreshDevices()
        {
            var groups = new Dictionary<string, PadDevice>();
            var order = new List<PadDevice>();
            _handleKeys.Clear();
            foreach (var (handle, path) in RawInputWindow.EnumerateKeyboards())
            {
                string key = DeviceIds.KeyFromPath(path);
                _handleKeys[handle] = key;
                if (!groups.TryGetValue(key, out var dev))
                {
                    dev = new PadDevice { Key = key };
                    groups[key] = dev;
                    order.Add(dev);
                }
                dev.Paths.Add(path);
                if (dev.Name == null) dev.Name = DeviceIds.ProductName(path);
            }

            foreach (var d in order)
            {
                string lowerName = (d.Name ?? "").ToLowerInvariant();
                d.IsMacropadGuess = d.Key == DeviceIds.KnownMacropadKey || lowerName.Contains("macro") || lowerName.Contains("mini keyboard");
                if (string.IsNullOrWhiteSpace(d.Name))
                {
                    if (d.Key == DeviceIds.KnownMacropadKey) d.Name = "USB-Makropad (3×3 + 3 Regler)";
                    else if (DeviceIds.IsVidPidKey(d.Key)) d.Name = "USB-Tastatur";
                    else if (d.Key.Contains("RDP")) d.Name = "Remotedesktop-Tastatur";
                    else if (d.Key.StartsWith("ACPI")) d.Name = "Interne / PS/2-Tastatur";
                    else d.Name = "Tastatur";
                }
                else if (d.Key == DeviceIds.KnownMacropadKey && !lowerName.Contains("makropad"))
                {
                    d.Name += " (Makropad)";
                }
                d.IsSelected = d.Key == _target;
            }

            Devices = order.OrderByDescending(d => d.IsSelected).ThenByDescending(d => d.IsMacropadGuess).ThenBy(d => d.Name).ToList();
            DevicesChanged?.Invoke();
        }

        public PadDevice FindDevice(string key) => Devices.FirstOrDefault(d => d.Key == key);

        /// <summary>Übernimmt Gerät und Blockier-Einstellung und (re)startet die passenden Backends.</summary>
        public void Configure(string targetKey, bool block)
        {
            _target = string.IsNullOrEmpty(targetKey) ? null : targetKey;
            _block = block;
            lock (_pressed) _pressed.Clear();
            foreach (var d in Devices) d.IsSelected = d.Key == _target;
            Apply();
        }

        void Apply()
        {
            if (Emergency)
            {
                StopInterception();
                _raw.Register(false);
                RawOk = false;
                StateChanged?.Invoke();
                return;
            }

            RawOk = _raw.Register(true);

            bool wantIcp = _block && DeviceIds.IsVidPidKey(_target);
            if (wantIcp)
            {
                if (_icp == null)
                {
                    var icp = new InterceptionBackend();
                    icp.Key += OnInterceptionKey;
                    if (icp.Start())
                    {
                        _icp = icp;
                    }
                    else
                    {
                        var failed = icp.Status == InterceptionStatus.NotUsed ? InterceptionStatus.Error : icp.Status;
                        Log.Write($"Interception nicht verfügbar: {failed} (Fehler {icp.LastError})");
                        icp.Key -= OnInterceptionKey;
                        icp.Dispose();
                        IcpStatus = failed;
                    }
                }
                if (_icp != null)
                {
                    IcpFiltered = _icp.SetTarget(_target);
                    IcpStatus = InterceptionStatus.Ready;
                    Log.Write($"Interception aktiv, {IcpFiltered} Schnittstelle(n) von {_target} gefiltert.");
                }
                else
                {
                    IcpFiltered = 0;
                }
            }
            else
            {
                StopInterception();
                IcpStatus = InterceptionStatus.NotUsed;
            }
            StateChanged?.Invoke();
        }

        void StopInterception()
        {
            if (_icp == null) return;
            _icp.Key -= OnInterceptionKey;
            _icp.Dispose();
            _icp = null;
            IcpFiltered = 0;
        }

        public List<string> InterceptionHardwareIds() => _icp?.HardwareIds() ?? new List<string>();

        /// <summary>Notaus: alle gerätespezifischen Eingabe-Hooks sofort aus.</summary>
        public void SetEmergency(bool on)
        {
            Emergency = on;
            lock (_pressed) _pressed.Clear();
            DetectNext = false;
            Log.Write(on ? "NOTAUS aktiviert – Eingabe-Hooks deaktiviert." : "Notaus aufgehoben.");
            Apply();
        }

        void OnRawKey(IntPtr hDevice, ushort make, ushort flags, ushort vkey)
        {
            if (Emergency || hDevice == IntPtr.Zero) return; // Zero = per Software erzeugte Tasten
            if (!_handleKeys.TryGetValue(hDevice, out string key))
            {
                key = DeviceIds.KeyFromPath(RawInputWindow.GetDeviceName(hDevice));
                if (key == null) return;
                _handleKeys[hDevice] = key;
            }
            bool down = (flags & Native.RI_KEY_BREAK) == 0;
            string id = KeyNames.MakeId(make, (flags & Native.RI_KEY_E0) != 0, vkey);
            Dispatch(key, id, down, false);
        }

        void OnInterceptionKey(string deviceKey, ushort make, ushort flags)
        {
            bool down = (flags & 1) == 0;
            string id = KeyNames.MakeId(make, (flags & 2) != 0, 0);
            _ui.BeginInvoke(new Action(() => Dispatch(deviceKey, id, down, true)));
        }

        void Dispatch(string deviceKey, string keyId, bool down, bool viaIcp)
        {
            if (Emergency) return;
            bool isTarget = _target != null && deviceKey == _target;

            KeyObserved?.Invoke(new KeyObservation
            {
                DeviceKey = deviceKey,
                DeviceName = FindDevice(deviceKey)?.Name ?? deviceKey,
                KeyId = keyId,
                KeyName = KeyNames.Describe(keyId),
                Down = down,
                FromTarget = isTarget,
                ViaInterception = viaIcp
            });

            if (DetectNext && down)
            {
                DetectNext = false;
                var dev = FindDevice(deviceKey);
                if (dev == null)
                {
                    RefreshDevices();
                    dev = FindDevice(deviceKey);
                }
                if (dev != null) DeviceDetected?.Invoke(dev);
                return;
            }

            if (!isTarget) return; // normale Tastatur → niemals ein Sound

            string pk = keyId;
            if (down)
            {
                lock (_pressed) { if (!_pressed.Add(pk)) return; } // Tastenwiederholung ignorieren
                MacroKeyDown?.Invoke(keyId);
            }
            else
            {
                lock (_pressed) _pressed.Remove(pk);
            }
        }

        public void Dispose()
        {
            try { StopInterception(); } catch { }
            try { _raw.Dispose(); } catch { }
        }
    }
}
