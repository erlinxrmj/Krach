using System;
using System.Drawing;
using System.Windows.Forms;
using Krach.Core;

namespace Krach.Services
{
    /// <summary>Symbol im Infobereich – Krach läuft so im Hintergrund weiter.</summary>
    public sealed class TrayIcon : IDisposable
    {
        readonly NotifyIcon _icon;
        readonly ToolStripMenuItem _emergency;

        public TrayIcon(App app, Controller c)
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false };
            menu.Items.Add("Krach öffnen", null, (s, e) => app.ShowMain());
            menu.Items.Add("Alle Sounds stoppen", null, (s, e) => c.Audio.StopAll());
            _emergency = new ToolStripMenuItem("Notaus: Makropad-Eingabe aus", null, (s, e) => c.ToggleEmergency());
            menu.Items.Add(_emergency);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Beenden", null, (s, e) => app.ExitApp());
            menu.Opening += (s, e) =>
                _emergency.Text = c.Input.Emergency ? "Makropad-Eingabe wieder aktivieren" : "Notaus: Makropad-Eingabe aus";

            _icon = new NotifyIcon
            {
                Text = "Krach Soundboard",
                Icon = LoadIcon(),
                ContextMenuStrip = menu,
                Visible = true
            };
            _icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) app.ShowMain(); };
        }

        static Icon LoadIcon()
        {
            try
            {
                var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/krach.ico"));
                if (res != null) return new Icon(res.Stream);
            }
            catch { }
            return SystemIcons.Application;
        }

        public void Balloon(string title, string text)
        {
            try { _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info); } catch { }
        }

        public void Dispose()
        {
            try
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
            catch { }
        }
    }
}
