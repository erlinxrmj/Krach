using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Krach.Audio;
using Krach.Core;
using Krach.Input;
using Krach.Services;

namespace Krach
{
    public partial class App : Application
    {
        const string MutexName = "Local\\Krach.Soundboard.Instanz";
        const string ShowEventName = "Local\\Krach.Soundboard.Anzeigen";

        Mutex _mutex;
        EventWaitHandle _showEvent;
        AudioEngine _audio;
        InputManager _input;
        TrayIcon _tray;
        MainWindow _window;
        bool _exiting;

        public Controller Controller { get; private set; }
        public static new App Current => (App)Application.Current;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _mutex = new Mutex(true, MutexName, out bool created);
            if (!created)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); }
                catch { MessageBox.Show("Krach läuft bereits (unten rechts im Infobereich).", "Krach", MessageBoxButton.OK, MessageBoxImage.Information); }
                _mutex.Dispose();
                _mutex = null;
                Shutdown();
                return;
            }

            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (s, a) =>
            {
                Log.Write("Absturz: " + a.ExceptionObject);
                try { _input?.Dispose(); } catch { } // Interception sofort freigeben
            };
            TaskScheduler.UnobservedTaskException += (s, a) => { Log.Write("Task-Fehler: " + a.Exception); a.SetObserved(); };

            Log.Write("Krach startet.");
            var settings = SettingsStore.Load();

            _audio = new AudioEngine(Dispatcher);
            try { _audio.Apply(settings); }
            catch (Exception ex) { Log.Write("Audio-Start: " + ex); }
            _audio.Preload(settings.Sounds.Select(s => s.FilePath));

            _input = new InputManager(Dispatcher);
            _input.RefreshDevices();
            if (string.IsNullOrEmpty(settings.DeviceKey))
            {
                var guess = _input.Devices.FirstOrDefault(d => d.Key == DeviceIds.KnownMacropadKey);
                if (guess != null)
                {
                    settings.DeviceKey = guess.Key;
                    settings.DeviceName = guess.Name;
                }
            }
            _input.Configure(settings.DeviceKey, settings.BlockOriginalKeys);
            _input.RefreshDevices();

            Controller = new Controller(settings, _audio, _input, Dispatcher);
            SettingsStore.Save(settings);

            _window = new MainWindow(Controller);
            _tray = new TrayIcon(this, Controller);

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var t = new Thread(() =>
            {
                while (true)
                {
                    try { _showEvent.WaitOne(); }
                    catch { return; }
                    Dispatcher.BeginInvoke(new Action(ShowMain));
                }
            }) { IsBackground = true, Name = "Krach-Anzeigen" };
            t.Start();

            bool autostart = e.Args.Any(a => a.Equals(Autostart.Argument, StringComparison.OrdinalIgnoreCase));
            if (!(autostart && settings.AutostartToTray)) ShowMain();
        }

        public void ShowMain()
        {
            if (_window == null) return;
            if (!_window.IsVisible) _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
            _window.Focus();
        }

        /// <summary>Fenster schließen: in den Infobereich oder ganz beenden.</summary>
        public void OnWindowClosing(CancelEventArgs e)
        {
            if (_exiting) return;
            if (Controller.S.CloseToTray)
            {
                e.Cancel = true;
                _window.Hide();
                if (!Controller.S.TrayHintShown)
                {
                    Controller.S.TrayHintShown = true;
                    Controller.SaveSoon();
                    _tray?.Balloon("Krach läuft weiter", "Das Soundboard arbeitet im Hintergrund. Beenden über das Symbol im Infobereich.");
                }
            }
            else
            {
                e.Cancel = true;
                ExitApp();
            }
        }

        public void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            try { Controller?.SaveNow(); } catch { }
            try { _input?.Dispose(); } catch { }
            try { _audio?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { _window?.Close(); } catch { }
            Log.Write("Krach beendet.");
            Shutdown();
        }

        /// <summary>Startet Krach mit Administratorrechten neu (nur nötig, falls Interception sie verlangt).</summary>
        public void RestartElevated()
        {
            try { Controller?.SaveNow(); } catch { }
            try { _input?.Dispose(); } catch { }
            ReleaseSingleInstance();
            try
            {
                Process.Start(new ProcessStartInfo(Autostart.ExePath) { UseShellExecute = true, Verb = "runas" });
            }
            catch (Exception ex)
            {
                Log.Write("Neustart als Admin abgebrochen: " + ex.Message);
                MessageBox.Show("Der Neustart als Administrator wurde abgebrochen. Krach wird beendet – bitte erneut öffnen.",
                    "Krach", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            _exiting = true;
            try { _audio?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            Shutdown();
        }

        void ReleaseSingleInstance()
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            _mutex = null;
        }

        void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Write("Fehler: " + e.Exception);
            e.Handled = true;
            MessageBox.Show("Es ist ein Fehler aufgetreten:\n\n" + e.Exception.Message + "\n\nDas Programm läuft weiter. Details stehen in der Protokolldatei.",
                "Krach", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _input?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            ReleaseSingleInstance();
            base.OnExit(e);
        }
    }
}
