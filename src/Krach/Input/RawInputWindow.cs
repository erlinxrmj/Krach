using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Krach.Input
{
    /// <summary>
    /// Unsichtbares Fenster, das Windows Raw Input aller Tastaturen empfängt (auch im Hintergrund).
    /// Raw Input liest nur mit – es blockiert niemals eine Taste.
    /// </summary>
    internal sealed class RawInputWindow : IDisposable
    {
        const int HotkeyId = 0x4B52; // "KR"
        readonly HwndSource _source;
        bool _registered;

        public event Action<IntPtr, ushort, ushort, ushort> Key; // hDevice, makeCode, flags, vkey
        public event Action DevicesChanged;
        public event Action Hotkey;

        public bool HotkeyRegistered { get; private set; }

        public RawInputWindow()
        {
            var p = new HwndSourceParameters("KrachInputSink")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,               // nicht sichtbar
                ExtendedWindowStyle = 0x00000080 // WS_EX_TOOLWINDOW
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
            HotkeyRegistered = Native.RegisterHotKey(_source.Handle, HotkeyId,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_F12);
        }

        public IntPtr Handle => _source.Handle;

        public bool Register(bool enable)
        {
            if (enable == _registered) return true;
            var dev = new[]
            {
                new Native.RAWINPUTDEVICE
                {
                    usUsagePage = 0x01,
                    usUsage = 0x06, // Tastatur
                    dwFlags = enable ? (Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY) : Native.RIDEV_REMOVE,
                    hwndTarget = enable ? _source.Handle : IntPtr.Zero
                }
            };
            bool ok = Native.RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
            if (ok) _registered = enable;
            return ok;
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case Native.WM_INPUT:
                    try { ProcessInput(lParam); } catch { }
                    break; // nicht "handled" – DefWindowProc muss aufräumen
                case Native.WM_INPUT_DEVICE_CHANGE:
                    DevicesChanged?.Invoke();
                    break;
                case Native.WM_HOTKEY:
                    if (wParam.ToInt32() == HotkeyId)
                    {
                        handled = true;
                        Hotkey?.Invoke();
                    }
                    break;
            }
            return IntPtr.Zero;
        }

        void ProcessInput(IntPtr hRaw)
        {
            uint headerSize = (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>();
            uint size = 0;
            Native.GetRawInputData(hRaw, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0 || size > 4096) return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.GetRawInputData(hRaw, Native.RID_INPUT, buf, ref size, headerSize) <= 0) return;
                var header = Marshal.PtrToStructure<Native.RAWINPUTHEADER>(buf);
                if (header.dwType != Native.RIM_TYPEKEYBOARD) return;
                var kb = Marshal.PtrToStructure<Native.RAWKEYBOARD>(buf + (int)headerSize);
                Key?.Invoke(header.hDevice, kb.MakeCode, kb.Flags, kb.VKey);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>Alle Tastatur-Endpunkte, die Windows gerade kennt.</summary>
        public static List<(IntPtr Handle, string Path)> EnumerateKeyboards()
        {
            var result = new List<(IntPtr, string)>();
            uint count = 0;
            uint itemSize = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();
            if (Native.GetRawInputDeviceList(IntPtr.Zero, ref count, itemSize) < 0 || count == 0) return result;
            IntPtr buf = Marshal.AllocHGlobal((int)(count * itemSize));
            try
            {
                int got = Native.GetRawInputDeviceList(buf, ref count, itemSize);
                for (int i = 0; i < got; i++)
                {
                    var d = Marshal.PtrToStructure<Native.RAWINPUTDEVICELIST>(buf + (int)(i * itemSize));
                    if (d.dwType != Native.RIM_TYPEKEYBOARD) continue;
                    string path = GetDeviceName(d.hDevice);
                    if (!string.IsNullOrEmpty(path)) result.Add((d.hDevice, path));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            return result;
        }

        public static string GetDeviceName(IntPtr hDevice)
        {
            uint chars = 0;
            Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_DEVICENAME, IntPtr.Zero, ref chars);
            if (chars == 0 || chars > 4096) return null;
            IntPtr buf = Marshal.AllocHGlobal((int)(chars + 1) * 2);
            try
            {
                if (Native.GetRawInputDeviceInfo(hDevice, Native.RIDI_DEVICENAME, buf, ref chars) <= 0) return null;
                return Marshal.PtrToStringUni(buf);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        public void Dispose()
        {
            try { Register(false); } catch { }
            try { if (HotkeyRegistered) Native.UnregisterHotKey(_source.Handle, HotkeyId); } catch { }
            try { _source.RemoveHook(WndProc); _source.Dispose(); } catch { }
        }
    }
}
