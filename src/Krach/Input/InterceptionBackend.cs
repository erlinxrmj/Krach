using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Krach.Input
{
    public enum InterceptionStatus
    {
        NotUsed,
        NotInstalled,
        AccessDenied,
        Busy,
        Error,
        Ready
    }

    /// <summary>
    /// Spricht den Interception-Treiber direkt an (ohne interception.dll).
    /// Es wird AUSSCHLIESSLICH das Gerät gefiltert, dessen Hardware-ID zur gewählten VID/PID passt.
    /// Alle anderen Tastaturen laufen gar nicht durch dieses Programm.
    /// </summary>
    internal sealed class InterceptionBackend : IDisposable
    {
        const uint IOCTL_SET_FILTER = 0x222010;
        const uint IOCTL_SET_EVENT = 0x222040;
        const uint IOCTL_WRITE = 0x222080;
        const uint IOCTL_READ = 0x222100;
        const uint IOCTL_GET_HARDWARE_ID = 0x222200;
        const int KeyboardSlots = 10;
        const int StrokeSize = 12; // KEYBOARD_INPUT_DATA
        const ushort FilterAll = 0xFFFF;

        sealed class Dev
        {
            public int Index;
            public SafeFileHandle Handle;
            public ManualResetEvent Event;
            public string HardwareId;
            public string Key;
            public volatile bool Filtered;
            public readonly HashSet<int> Down = new HashSet<int>();
        }

        readonly List<Dev> _devs = new List<Dev>();
        readonly object _lock = new object();
        Thread _thread;
        volatile bool _running;

        /// <summary>deviceKey, makeCode, flags – wird im Interception-Thread ausgelöst.</summary>
        public event Action<string, ushort, ushort> Key;

        public InterceptionStatus Status { get; private set; } = InterceptionStatus.NotUsed;
        public int LastError { get; private set; }

        public int FilteredCount
        {
            get { lock (_lock) { int n = 0; foreach (var d in _devs) if (d.Filtered) n++; return n; } }
        }

        public bool Start()
        {
            for (int i = 0; i < KeyboardSlots; i++)
            {
                var h = Native.CreateFile(@"\\.\interception" + i.ToString("00"), Native.GENERIC_READ, 0,
                    IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    h.Dispose();
                    if (i == 0)
                    {
                        LastError = err;
                        Status = err switch
                        {
                            2 or 3 => InterceptionStatus.NotInstalled,
                            5 => InterceptionStatus.AccessDenied,
                            32 => InterceptionStatus.Busy,
                            _ => InterceptionStatus.Error
                        };
                        CloseAll();
                        return false;
                    }
                    continue;
                }

                var ev = new ManualResetEvent(false);
                var inBuf = new byte[16]; // HANDLE[2] auf x64
                BitConverter.GetBytes(ev.SafeWaitHandle.DangerousGetHandle().ToInt64()).CopyTo(inBuf, 0);
                if (!Native.DeviceIoControl(h, IOCTL_SET_EVENT, inBuf, (uint)inBuf.Length, null, 0, out _, IntPtr.Zero))
                {
                    ev.Dispose();
                    h.Dispose();
                    continue;
                }
                _devs.Add(new Dev { Index = i, Handle = h, Event = ev });
            }

            if (_devs.Count == 0)
            {
                Status = InterceptionStatus.Error;
                return false;
            }

            RefreshHardwareIds();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Krach-Interception", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            Status = InterceptionStatus.Ready;
            return true;
        }

        public void RefreshHardwareIds()
        {
            lock (_lock)
            {
                foreach (var d in _devs)
                {
                    var buf = new byte[1024];
                    string hw = null;
                    if (Native.DeviceIoControl(d.Handle, IOCTL_GET_HARDWARE_ID, null, 0, buf, (uint)buf.Length, out uint n, IntPtr.Zero) && n > 2)
                    {
                        hw = Encoding.Unicode.GetString(buf, 0, (int)n);
                        int z = hw.IndexOf('\0');
                        if (z >= 0) hw = hw.Substring(0, z);
                    }
                    d.HardwareId = string.IsNullOrWhiteSpace(hw) ? null : hw;
                    d.Key = d.HardwareId != null ? DeviceIds.KeyFromPath(d.HardwareId) : null;
                }
            }
        }

        /// <summary>Filtert nur Geräte mit exakt dieser VID/PID. Gibt die Zahl der gefilterten Schnittstellen zurück.</summary>
        public int SetTarget(string deviceKey)
        {
            bool valid = DeviceIds.IsVidPidKey(deviceKey);
            int count = 0;
            lock (_lock)
            {
                foreach (var d in _devs)
                {
                    bool want = valid && d.Key != null && d.Key == deviceKey;
                    SetFilter(d, want);
                    if (want) count++;
                }
            }
            return count;
        }

        public List<string> HardwareIds()
        {
            var l = new List<string>();
            lock (_lock) foreach (var d in _devs) if (d.HardwareId != null) l.Add($"#{d.Index}: {d.HardwareId}");
            return l;
        }

        void SetFilter(Dev d, bool on)
        {
            byte[] f = BitConverter.GetBytes(on ? FilterAll : (ushort)0);
            Native.DeviceIoControl(d.Handle, IOCTL_SET_FILTER, f, (uint)f.Length, null, 0, out _, IntPtr.Zero);
            d.Filtered = on;
            if (!on) lock (d.Down) d.Down.Clear();
        }

        void Loop()
        {
            var handles = new WaitHandle[_devs.Count];
            for (int i = 0; i < _devs.Count; i++) handles[i] = _devs[i].Event;
            var buf = new byte[StrokeSize * 32];

            while (_running)
            {
                int idx;
                try { idx = WaitHandle.WaitAny(handles, 250); }
                catch { break; }
                if (!_running) break;
                if (idx == WaitHandle.WaitTimeout || idx < 0 || idx >= _devs.Count) continue;
                Drain(_devs[idx], buf);
            }
        }

        void Drain(Dev d, byte[] buf)
        {
            for (int round = 0; round < 64 && _running; round++)
            {
                if (!Native.DeviceIoControl(d.Handle, IOCTL_READ, null, 0, buf, (uint)buf.Length, out uint got, IntPtr.Zero) || got < StrokeSize)
                {
                    d.Event.Reset();
                    // erneut prüfen, falls genau jetzt ein Tastendruck ankam
                    if (!Native.DeviceIoControl(d.Handle, IOCTL_READ, null, 0, buf, (uint)buf.Length, out got, IntPtr.Zero) || got < StrokeSize)
                        return;
                }

                int n = (int)got / StrokeSize;
                for (int i = 0; i < n; i++)
                {
                    int off = i * StrokeSize;
                    ushort make = BitConverter.ToUInt16(buf, off + 2);
                    ushort flags = BitConverter.ToUInt16(buf, off + 4);
                    HandleStroke(d, buf, off, make, flags);
                }
            }
        }

        void HandleStroke(Dev d, byte[] buf, int off, ushort make, ushort flags)
        {
            if (!d.Filtered)
            {
                // Sicherheitsnetz: nicht gewünschte Geräte immer durchreichen.
                Forward(d, buf, off);
                return;
            }

            bool up = (flags & 1) != 0;
            int code = make | ((flags & 2) != 0 ? 0x100 : 0);
            lock (d.Down)
            {
                if (up)
                {
                    if (!d.Down.Remove(code))
                    {
                        // Loslassen einer Taste, deren Drücken Windows schon gesehen hat → durchreichen (keine hängende Taste)
                        Forward(d, buf, off);
                        return;
                    }
                }
                else
                {
                    d.Down.Add(code);
                }
            }

            try { Key?.Invoke(d.Key, make, flags); }
            catch { }
        }

        void Forward(Dev d, byte[] buf, int off)
        {
            var one = new byte[StrokeSize];
            Buffer.BlockCopy(buf, off, one, 0, StrokeSize);
            Native.DeviceIoControl(d.Handle, IOCTL_WRITE, one, StrokeSize, null, 0, out _, IntPtr.Zero);
        }

        void CloseAll()
        {
            lock (_lock)
            {
                foreach (var d in _devs)
                {
                    try { if (d.Filtered) SetFilter(d, false); } catch { }
                    try { d.Handle.Dispose(); } catch { }
                }
                _devs.Clear();
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _thread?.Join(600); } catch { }
            var events = new List<ManualResetEvent>();
            lock (_lock) foreach (var d in _devs) events.Add(d.Event);
            CloseAll(); // Filter aus + Handles zu → Treiber lässt alles wieder normal durch
            foreach (var e in events) try { e.Dispose(); } catch { }
            Status = InterceptionStatus.NotUsed;
        }
    }
}
