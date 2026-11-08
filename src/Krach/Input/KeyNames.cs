using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Krach.Input
{
    /// <summary>
    /// Tasten werden geräteunabhängig über ihren Hardware-Scancode identifiziert
    /// (Raw Input und Interception liefern denselben Scancode).
    /// Format: "S2E" (Scancode 0x2E) oder "S1C+E0" (erweiterte Taste); "V7C" falls nur ein VK bekannt ist.
    /// </summary>
    public static class KeyNames
    {
        public static string MakeId(ushort makeCode, bool e0, ushort vkey)
        {
            if (makeCode != 0 && makeCode != 0xFF)
                return e0 ? $"S{makeCode:X2}+E0" : $"S{makeCode:X2}";
            return $"V{vkey:X2}";
        }

        public static string Describe(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try
            {
                if (id[0] == 'S')
                {
                    bool e0 = id.EndsWith("+E0", StringComparison.Ordinal);
                    string hex = id.Substring(1, e0 ? id.Length - 4 : id.Length - 1);
                    int scan = int.Parse(hex, NumberStyles.HexNumber);
                    int lParam = ((scan & 0xFF) << 16) | (e0 ? 1 << 24 : 0);
                    var sb = new StringBuilder(64);
                    int n = Native.GetKeyNameText(lParam, sb, sb.Capacity);
                    if (n > 0) return sb.ToString();
                    return "Scancode " + hex + (e0 ? " (erw.)" : "");
                }
                if (id[0] == 'V')
                {
                    int vk = int.Parse(id.Substring(1), NumberStyles.HexNumber);
                    return ((System.Windows.Forms.Keys)vk).ToString();
                }
            }
            catch { }
            return id;
        }
    }

    public static class DeviceIds
    {
        static readonly Regex UsbVidPid = new Regex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex BtVidPid = new Regex(@"VID&[0-9A-F]{4}([0-9A-F]{4})_PID&([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        const string HidInterfaceGuid = "{4d1e55b2-f16f-11cf-88cb-001111000030}";

        public const string KnownMacropadKey = "VID_1189&PID_8840";

        /// <summary>Stabile Geräteidentität aus einem Gerätepfad oder einer Hardware-ID.</summary>
        public static string KeyFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var m = UsbVidPid.Match(path);
            if (!m.Success) m = BtVidPid.Match(path);
            if (m.Success)
                return $"VID_{m.Groups[1].Value.ToUpperInvariant()}&PID_{m.Groups[2].Value.ToUpperInvariant()}";

            string p = path.TrimStart('\\', '?');
            var parts = p.Split('#');
            return (parts.Length >= 2 ? parts[0] + "#" + parts[1] : p).ToUpperInvariant();
        }

        public static bool IsVidPidKey(string key) =>
            key != null && key.Length == 17 && key.StartsWith("VID_", StringComparison.Ordinal) && key.Substring(8, 5) == "&PID_";

        public static string VidPidDisplay(string key) =>
            IsVidPidKey(key) ? $"VID 0x{key.Substring(4, 4)} · PID 0x{key.Substring(13, 4)}" : "keine USB-Kennung";

        public static string InterfaceOf(string path)
        {
            if (path == null) return null;
            var m = Regex.Match(path, @"MI_([0-9A-F]{2})", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Liest Hersteller und Produktname über die HID-Schnittstelle.</summary>
        public static string ProductName(string rawInputPath)
        {
            try
            {
                int i = rawInputPath.LastIndexOf("#{", StringComparison.Ordinal);
                string hidPath = i > 0 ? rawInputPath.Substring(0, i) + "#" + HidInterfaceGuid : rawInputPath;
                using SafeFileHandle h = Native.CreateFile(hidPath, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) return null;
                var buf = new byte[256];
                string product = Native.HidD_GetProductString(h, buf, (uint)buf.Length) ? Decode(buf) : null;
                Array.Clear(buf, 0, buf.Length);
                string maker = Native.HidD_GetManufacturerString(h, buf, (uint)buf.Length) ? Decode(buf) : null;
                if (string.IsNullOrWhiteSpace(product)) return null;
                if (!string.IsNullOrWhiteSpace(maker) && !product.StartsWith(maker, StringComparison.OrdinalIgnoreCase))
                    return maker + " " + product;
                return product;
            }
            catch
            {
                return null;
            }
        }

        static string Decode(byte[] b)
        {
            string s = Encoding.Unicode.GetString(b);
            int z = s.IndexOf('\0');
            return (z >= 0 ? s.Substring(0, z) : s).Trim();
        }
    }
}
