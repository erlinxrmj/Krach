using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Krach.Audio;
using Krach.Controls;
using Krach.Core;
using Krach.Input;
using Krach.Services;
using NAudio.CoreAudioApi;
using Shapes = System.Windows.Shapes;

namespace Krach
{
    public partial class MainWindow : Window
    {
        const string DragFormat = "KrachSoundId";

        sealed class Option<T>
        {
            public T Value { get; set; }
            public string Text { get; set; }
            public override string ToString() => Text;
        }

        readonly Controller C;
        readonly Border[] _slots = new Border[9];
        readonly TextBlock[] _slotName = new TextBlock[9];
        readonly TextBlock[] _slotKey = new TextBlock[9];
        readonly TextBlock[] _slotIcon = new TextBlock[9];
        readonly Border[] _slotPlay = new Border[9];
        readonly int[] _slotState = Enumerable.Repeat(-1, 9).ToArray();
        readonly string[] _slotBorderKey = new string[9];
        readonly Shapes.Ellipse[] _knobBody = new Shapes.Ellipse[3];
        readonly RotateTransform[] _knobRot = new RotateTransform[3];
        readonly TextBlock[] _knobAction = new TextBlock[3];
        readonly DispatcherTimer _timer;
        readonly DispatcherTimer _messageTimer;
        readonly SoundItem _noneItem = new SoundItem { Id = null, Name = "— kein Sound —" };

        int _selSlot;
        bool _loading = true;
        bool _messageSticky;
        Point _dragStart;
        SoundItem _dragItem;
        float _lvBoard, _lvMonitor, _lvMic, _lvChat;
        List<AudioDeviceEntry> _render = new List<AudioDeviceEntry>();
        List<AudioDeviceEntry> _capture = new List<AudioDeviceEntry>();

        static readonly Option<KnobTurnAction>[] TurnOptions =
        {
            new Option<KnobTurnAction> { Value = KnobTurnAction.None, Text = "Nichts" },
            new Option<KnobTurnAction> { Value = KnobTurnAction.SoundVolume, Text = "Soundboard-Lautstärke" },
            new Option<KnobTurnAction> { Value = KnobTurnAction.MicVolume, Text = "Mikrofon-Lautstärke" },
            new Option<KnobTurnAction> { Value = KnobTurnAction.MonitorVolume, Text = "Mithör-Lautstärke" },
            new Option<KnobTurnAction> { Value = KnobTurnAction.SwitchPage, Text = "Seite wechseln" },
            new Option<KnobTurnAction> { Value = KnobTurnAction.SelectSound, Text = "Sound auswählen" },
            new Option<KnobTurnAction> { Value = KnobTurnAction.SwitchVoiceEffect, Text = "Stimmeffekt wechseln" },
        };

        static readonly Option<KnobPressAction>[] PressOptions =
        {
            new Option<KnobPressAction> { Value = KnobPressAction.None, Text = "Nichts" },
            new Option<KnobPressAction> { Value = KnobPressAction.StopAll, Text = "Alle Sounds stoppen" },
            new Option<KnobPressAction> { Value = KnobPressAction.PauseResumeAll, Text = "Pause / Weiter" },
            new Option<KnobPressAction> { Value = KnobPressAction.ToggleMicMute, Text = "Mikrofon stumm an/aus" },
            new Option<KnobPressAction> { Value = KnobPressAction.NextPage, Text = "Nächste Seite" },
            new Option<KnobPressAction> { Value = KnobPressAction.PlaySelected, Text = "Ausgewählten Sound abspielen" },
            new Option<KnobPressAction> { Value = KnobPressAction.ToggleVoiceEffect, Text = "Stimmeffekt an/aus" },
        };

        public MainWindow(Controller controller)
        {
            C = controller;
            InitializeComponent();

            BuildSlots();
            BuildKnobs();
            BuildKnobConfig();
            BuildEffects();

            SlotEditor.Edited += _ => C.SaveSoon();
            SlotEditor.VisualChanged += _ => C.NotifyChanged();
            LibEditor.Edited += _ => C.SaveSoon();
            LibEditor.VisualChanged += _ => C.NotifyChanged();

            C.Changed += RefreshAll;
            C.LiveChanged += RefreshLive;
            C.LearnChanged += RefreshLearn;
            C.Message += m => ShowMessage(m, false);
            C.Input.StateChanged += () => { RefreshStatus(); RefreshPadPage(); };
            C.Input.DevicesChanged += () => { RefreshDeviceList(); RefreshStatus(); RefreshSetupHint(); };
            C.Input.KeyObserved += OnKeyObserved;
            C.Input.DeviceDetected += OnDeviceDetected;
            C.Audio.StateChanged += RefreshAudioPage;
            C.Audio.DevicesChanged += RefreshAudioPage;

            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Normal, Tick, Dispatcher);
            _timer.Stop();
            _messageTimer = new DispatcherTimer(TimeSpan.FromSeconds(8), DispatcherPriority.Normal, (s, e) =>
            {
                _messageTimer.Stop();
                if (!_messageSticky) MessageBar.Visibility = Visibility.Collapsed;
            }, Dispatcher);
            _messageTimer.Stop();

            DataPathText.Text = "Sounds und Einstellungen liegen lokal hier: " + AppPaths.DataDir;
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            AboutText.Text = $"Krach Soundboard {ver?.ToString(3)} · entwickelt von Erlin Ramaj\nKostenlos, ohne Abo, ohne Cloud und ohne Registrierung. Alle Daten bleiben auf deinem PC.";

            SourceInitialized += (s, e) =>
            {
                try
                {
                    int on = 1;
                    Native_SetDark(new WindowInteropHelper(this).Handle, on);
                }
                catch { }
            };
            Closing += (s, e) => App.Current.OnWindowClosing(e);
            IsVisibleChanged += (s, e) => { if (IsVisible) _timer.Start(); else _timer.Stop(); };

            _loading = false;
            NavBoard.IsChecked = true;
            RefreshAll();
            if (string.IsNullOrEmpty(C.S.DeviceKey)) NavPad.IsChecked = true;
        }

        static void Native_SetDark(IntPtr hwnd, int on)
        {
            Input.Native.DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
        }

        // =====================================================================
        //  Aufbau
        // =====================================================================

        static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
        static FontFamily IconFont => (FontFamily)Application.Current.FindResource("IconFont");

        void BuildSlots()
        {
            var faint = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255));
            for (int i = 0; i < 9; i++)
            {
                int idx = i;
                var key = new TextBlock { FontSize = 11, Foreground = faint, Margin = new Thickness(12, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                var num = new TextBlock { Text = (i + 1).ToString(), FontSize = 11, Foreground = faint, Margin = new Thickness(0, 10, 12, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
                var name = new TextBlock
                {
                    FontSize = 17,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 26, 12, 30),
                    MaxHeight = 70
                };
                var icon = new TextBlock { FontFamily = IconFont, FontSize = 14, Foreground = Brushes.White, Margin = new Thickness(0, 0, 12, 12), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
                var play = new Border
                {
                    Width = 32,
                    Height = 32,
                    CornerRadius = new CornerRadius(16),
                    Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(10, 0, 0, 10),
                    Cursor = Cursors.Hand,
                    ToolTip = "Abspielen / Stoppen",
                    Child = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                play.MouseLeftButtonDown += (s, e) => { C.TriggerSlot(idx); SelectSlot(idx); e.Handled = true; };

                var grid = new Grid();
                grid.Children.Add(name);
                grid.Children.Add(key);
                grid.Children.Add(num);
                grid.Children.Add(icon);
                grid.Children.Add(play);

                var b = new Border
                {
                    CornerRadius = new CornerRadius(18),
                    Margin = new Thickness(7),
                    BorderThickness = new Thickness(2.5),
                    BorderBrush = Brushes.Transparent,
                    Child = grid,
                    AllowDrop = true,
                    Cursor = Cursors.Hand,
                    SnapsToDevicePixels = true
                };
                b.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount == 2) C.TriggerSlot(idx);
                    SelectSlot(idx);
                };
                b.DragOver += Slot_DragOver;
                b.Drop += (s, e) => Slot_Drop(idx, e);
                b.ContextMenu = BuildSlotMenu(idx);

                _slots[i] = b;
                _slotName[i] = name;
                _slotKey[i] = key;
                _slotIcon[i] = icon;
                _slotPlay[i] = play;
                SlotGrid.Children.Add(b);
            }
        }

        ContextMenu BuildSlotMenu(int idx)
        {
            var m = new ContextMenu();
            void Add(string text, Action a)
            {
                var mi = new MenuItem { Header = text };
                mi.Click += (s, e) => a();
                m.Items.Add(mi);
            }
            Add("Abspielen / Stoppen", () => C.TriggerSlot(idx));
            Add("Bearbeiten", () => SelectSlot(idx));
            Add("Taste anlernen", () => { SelectSlot(idx); C.StartLearn(idx, true); });
            Add("Sound von der Taste entfernen", () => C.AssignSlot(idx, null));
            return m;
        }

        void BuildKnobs()
        {
            for (int k = 0; k < 3; k++)
            {
                int idx = k;
                var body = new Shapes.Ellipse
                {
                    Width = 70,
                    Height = 70,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x52)),
                    StrokeThickness = 2,
                    Fill = new RadialGradientBrush(Color.FromRgb(0x30, 0x36, 0x44), Color.FromRgb(0x17, 0x1A, 0x22))
                };
                var rot = new RotateTransform(0);
                var marker = new Border
                {
                    Width = 4,
                    Height = 16,
                    CornerRadius = new CornerRadius(2),
                    Background = Res("AccentGradient"),
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 7, 0, 0),
                    RenderTransformOrigin = new Point(0.5, (35.0 - 7.0) / 16.0),
                    RenderTransform = rot
                };
                var dial = new Grid { Width = 70, Height = 70, Cursor = Cursors.Hand, ToolTip = "Klicken: Drücken-Aktion · Mausrad: Drehen" };
                dial.Children.Add(body);
                dial.Children.Add(marker);
                dial.MouseLeftButtonDown += (s, e) => C.Press(idx);
                dial.MouseWheel += (s, e) => { C.Turn(idx, e.Delta > 0 ? 1 : -1); e.Handled = true; };

                var title = new TextBlock { Text = $"Regler {k + 1}", FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
                var action = new TextBlock { FontSize = 10.5, Foreground = Res("SubBrush"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2, 0, 0), Width = 150, TextWrapping = TextWrapping.Wrap };
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                sp.Children.Add(dial);
                sp.Children.Add(title);
                sp.Children.Add(action);
                KnobGrid.Children.Add(sp);

                _knobBody[k] = body;
                _knobRot[k] = rot;
                _knobAction[k] = action;
            }
        }

        void BuildKnobConfig()
        {
            var g = KnobConfigGrid;
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(g, new TextBlock { Text = "Drehen", Style = (Style)FindResource("Label") }, 0, 1);
            AddCell(g, new TextBlock { Text = "Drücken", Style = (Style)FindResource("Label") }, 0, 3);
            for (int k = 0; k < 3; k++)
            {
                int idx = k;
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AddCell(g, new TextBlock { Text = $"Regler {k + 1}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, k + 1, 0);

                var turn = new ComboBox { ItemsSource = TurnOptions, Margin = new Thickness(0, 0, 0, 10), Tag = "turn" + k };
                turn.SelectedItem = TurnOptions.FirstOrDefault(o => o.Value == C.S.Knobs[k].Turn);
                turn.SelectionChanged += (s, e) =>
                {
                    if (_loading || !(turn.SelectedItem is Option<KnobTurnAction> o)) return;
                    C.S.Knobs[idx].Turn = o.Value;
                    C.SaveSoon();
                    RefreshKnobs();
                };
                AddCell(g, turn, k + 1, 1);

                var press = new ComboBox { ItemsSource = PressOptions, Margin = new Thickness(0, 0, 0, 10), Tag = "press" + k };
                press.SelectedItem = PressOptions.FirstOrDefault(o => o.Value == C.S.Knobs[k].Press);
                press.SelectionChanged += (s, e) =>
                {
                    if (_loading || !(press.SelectedItem is Option<KnobPressAction> o)) return;
                    C.S.Knobs[idx].Press = o.Value;
                    C.SaveSoon();
                    RefreshKnobs();
                };
                AddCell(g, press, k + 1, 3);
            }
        }

        static void AddCell(Grid g, UIElement el, int row, int col)
        {
            Grid.SetRow(el, row);
            Grid.SetColumn(el, col);
            g.Children.Add(el);
        }

        void BuildEffects()
        {
            foreach (VoiceEffect fx in Enum.GetValues(typeof(VoiceEffect)))
            {
                if (fx == VoiceEffect.None) continue;
                var rb = new RadioButton
                {
                    Content = Controller.EffectName(fx),
                    Style = (Style)FindResource("PageTab"),
                    GroupName = "fx",
                    Tag = fx,
                    Margin = new Thickness(0, 0, 8, 8)
                };
                rb.Checked += (s, e) =>
                {
                    if (_loading) return;
                    C.SetVoiceEffect((VoiceEffect)rb.Tag, true);
                    RefreshEffects();
                };
                FxPanel.Children.Add(rb);
            }
        }

        // =====================================================================
        //  Aktualisierung
        // =====================================================================

        void RefreshAll()
        {
            bool was = _loading;
            _loading = true;
            try
            {
                RefreshPages();
                RefreshSlots();
                RefreshSlotDetail();
                RefreshLibraryLists();
                RefreshKnobs();
                RefreshDeviceList();
                RefreshPadPage();
                RefreshAssignments();
                RefreshLearn();
                RefreshAudioPage();
                RefreshSettings();
                RefreshLive();
                RefreshStatus();
                RefreshSetupHint();
            }
            finally
            {
                _loading = was;
            }
        }

        void RefreshPages()
        {
            PagesPanel.Children.Clear();
            for (int i = 0; i < C.S.Pages.Count; i++)
            {
                int idx = i;
                var rb = new RadioButton { Content = C.S.Pages[i].Name, Style = (Style)FindResource("PageTab"), GroupName = "pages" };
                rb.IsChecked = i == C.S.CurrentPage;
                rb.Checked += (s, e) => { if (!_loading) C.SetPage(idx); };
                PagesPanel.Children.Add(rb);
            }
            if (!PageNameBox.IsKeyboardFocused) PageNameBox.Text = C.Page.Name;
        }

        void RefreshSlots()
        {
            var empty = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x27));
            for (int i = 0; i < 9; i++)
            {
                var item = C.SlotSound(i);
                if (item != null)
                {
                    var c = ToColor(item.Color);
                    _slots[i].Background = new LinearGradientBrush(c, Darken(c, 0.55), 45);
                    _slotName[i].Text = item.Name;
                    _slotName[i].Foreground = Brushes.White;
                    _slotName[i].FontSize = 17;
                    _slotPlay[i].Visibility = Visibility.Visible;
                    _slotIcon[i].Text = item.Loop ? "" : "";
                    if (!File.Exists(item.FilePath)) { _slotIcon[i].Text = ""; _slots[i].ToolTip = "Datei fehlt: " + item.FilePath; }
                    else _slots[i].ToolTip = null;
                }
                else
                {
                    _slots[i].Background = empty;
                    _slotName[i].Text = "Leer\nSound hierher ziehen";
                    _slotName[i].Foreground = Res("SubBrush");
                    _slotName[i].FontSize = 13;
                    _slotPlay[i].Visibility = Visibility.Collapsed;
                    _slotIcon[i].Text = "";
                    _slots[i].ToolTip = null;
                }
                string k = KeyNames.Describe(C.S.ButtonKeys[i]);
                _slotKey[i].Text = k != null ? "⌨ " + k : "nicht angelernt";
                _slotState[i] = -1;
                _slotBorderKey[i] = null;
            }
            UpdateSlotStates();
        }

        void UpdateSlotStates()
        {
            int playing = 0;
            for (int i = 0; i < 9; i++)
            {
                var item = C.SlotSound(i);
                int st = item == null ? 0 : C.Audio.StateOf(item.Id);
                if (st > 0) playing++;

                string borderKey;
                if (C.LearnTarget == i) borderKey = "learn";
                else if (st == 1) borderKey = "play";
                else if (st == 2) borderKey = "pause";
                else if (i == C.KnobSelectedSlot) borderKey = "knob";
                else if (i == _selSlot) borderKey = "sel";
                else borderKey = "none";

                if (borderKey != _slotBorderKey[i])
                {
                    _slotBorderKey[i] = borderKey;
                    _slots[i].BorderBrush = borderKey switch
                    {
                        "learn" => Res("WarnBrush"),
                        "play" => Brushes.White,
                        "pause" => Res("WarnBrush"),
                        "knob" => new SolidColorBrush(Color.FromRgb(0x00, 0xB8, 0xD9)),
                        "sel" => Res("AccentBrush"),
                        _ => Brushes.Transparent
                    };
                }
                if (st != _slotState[i])
                {
                    _slotState[i] = st;
                    _slots[i].Effect = st == 1 && item != null
                        ? new DropShadowEffect { Color = ToColor(item.Color), BlurRadius = 28, ShadowDepth = 0, Opacity = 0.9 }
                        : null;
                    ((TextBlock)_slotPlay[i].Child).Text = st == 1 ? "" : "";
                }
            }
            int total = C.Audio.PlayingCount;
            PlayingText.Text = total == 0 ? "Bereit" : total == 1 ? "1 Sound läuft" : $"{total} Sounds laufen";
            bool paused = C.Audio.AnyPaused;
            PauseIcon.Text = paused ? "" : "";
            PauseText.Text = paused ? "Weiter" : "Pause";
        }

        void SelectSlot(int idx)
        {
            _selSlot = idx;
            for (int i = 0; i < 9; i++) _slotBorderKey[i] = null;
            UpdateSlotStates();
            bool was = _loading;
            _loading = true;
            try { RefreshSlotDetail(); }
            finally { _loading = was; }
        }

        void RefreshSlotDetail()
        {
            SlotTitle.Text = $"{C.Page.Name} · Taste {_selSlot + 1}";
            string k = KeyNames.Describe(C.S.ButtonKeys[_selSlot]);
            SlotKeyText.Text = k != null ? $"Physische Makropad-Taste: {k}" : "Diese Taste ist noch nicht angelernt.";
            var list = new List<SoundItem> { _noneItem };
            list.AddRange(C.S.Sounds.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase));
            SlotSoundBox.ItemsSource = list;
            var item = C.SlotSound(_selSlot);
            SlotSoundBox.SelectedItem = item ?? _noneItem;
            SlotEditor.SetItem(item);
            SlotPlayButton.IsEnabled = item != null;
        }

        void RefreshLibraryLists()
        {
            var sorted = C.S.Sounds.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            MiniLibrary.ItemsSource = sorted;

            string q = SearchBox.Text.Trim();
            var filtered = q.Length == 0 ? sorted : sorted.Where(s => s.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            var sel = LibraryList.SelectedItem as SoundItem;
            LibraryList.ItemsSource = filtered;
            if (sel != null && filtered.Contains(sel)) LibraryList.SelectedItem = sel;
            else if (filtered.Count > 0 && LibraryList.SelectedItem == null) LibraryList.SelectedIndex = 0;

            LibraryEmpty.Visibility = C.S.Sounds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            string admin = Autostart.IsAdmin ? "  ·  Als Administrator gestartet: Windows sperrt Ziehen aus dem Explorer – nutze „Sounds hinzufügen“." : "";
            LibraryCount.Text = $"{C.S.Sounds.Count} Sound(s){admin}";
            RefreshLibEditor();
        }

        void RefreshLibEditor()
        {
            var item = LibraryList.SelectedItem as SoundItem;
            LibEditor.SetItem(item);
            LibUsage.Text = item == null ? "Wähle links einen Sound aus." : C.UsageOf(item);
            LibPlayButton.IsEnabled = LibAssignButton.IsEnabled = LibDeleteButton.IsEnabled = item != null;
            LibAssignButton.Content = $"Auf Taste {_selSlot + 1} legen";
        }

        void RefreshKnobs()
        {
            for (int k = 0; k < 3; k++)
            {
                var kc = C.S.Knobs[k];
                double v = kc.Turn switch
                {
                    KnobTurnAction.SoundVolume => C.S.SoundVolume,
                    KnobTurnAction.MicVolume => C.S.MicVolume / 2,
                    KnobTurnAction.MonitorVolume => C.S.MonitorVolume,
                    KnobTurnAction.SelectSound => C.KnobSelectedSlot < 0 ? 0 : C.KnobSelectedSlot / 8.0,
                    KnobTurnAction.SwitchPage => C.S.Pages.Count <= 1 ? 0 : (double)C.S.CurrentPage / (C.S.Pages.Count - 1),
                    KnobTurnAction.SwitchVoiceEffect => ((int)C.S.VoiceEffect - 1) / 6.0,
                    _ => 0.5
                };
                _knobRot[k].Angle = -135 + 270 * Math.Clamp(v, 0, 1);

                string turnText = TurnOptions.First(o => o.Value == kc.Turn).Text;
                string pressText = PressOptions.First(o => o.Value == kc.Press).Text;
                string extra = kc.Turn switch
                {
                    KnobTurnAction.SoundVolume => $" ({Math.Round(C.S.SoundVolume * 100)} %)",
                    KnobTurnAction.MicVolume => $" ({Math.Round(C.S.MicVolume * 100)} %)",
                    KnobTurnAction.MonitorVolume => $" ({Math.Round(C.S.MonitorVolume * 100)} %)",
                    KnobTurnAction.SwitchVoiceEffect => $" ({Controller.EffectName(C.S.VoiceEffect)}{(C.S.VoiceEffectOn ? "" : ", aus")})",
                    _ => ""
                };
                _knobAction[k].Text = $"↻ {turnText}{extra}\n● {pressText}";

                bool learning = C.LearnTarget >= 9 && (C.LearnTarget - 9) / 3 == k;
                _knobBody[k].Stroke = learning ? Res("WarnBrush") : new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x52));
                _knobBody[k].StrokeThickness = learning ? 3 : 2;
            }
        }

        void RefreshLive()
        {
            bool was = _loading;
            _loading = true;
            try
            {
                BoardVolume.Value = C.S.SoundVolume;
                BoardVolumeText.Text = $"{Math.Round(C.S.SoundVolume * 100)} %";
                MonitorVol.Value = C.S.MonitorVolume;
                MonitorVolText.Text = $"{Math.Round(C.S.MonitorVolume * 100)} %";
                MicVol.Value = C.S.MicVolume;
                MicVolText.Text = $"{Math.Round(C.S.MicVolume * 100)} %";
                MicMuteBox.IsChecked = C.S.MicMuted;
                RefreshEffects();
                RefreshKnobs();
                for (int i = 0; i < 9; i++) _slotBorderKey[i] = null;
                UpdateSlotStates();
            }
            finally { _loading = was; }
        }

        void RefreshEffects()
        {
            bool was = _loading;
            _loading = true;
            try
            {
                FxOnBox.IsChecked = C.S.VoiceEffectOn;
                foreach (RadioButton rb in FxPanel.Children)
                    rb.IsChecked = (VoiceEffect)rb.Tag == C.S.VoiceEffect;
            }
            finally { _loading = was; }
        }

        void RefreshStatus()
        {
            var inp = C.Input;
            EmergencyBanner.Visibility = inp.Emergency ? Visibility.Visible : Visibility.Collapsed;
            EmergencyText.Text = inp.Emergency ? "Eingabe wieder aktivieren" : "Notaus";

            var dev = C.S.DeviceKey == null ? null : inp.FindDevice(C.S.DeviceKey);
            if (inp.Emergency)
            {
                StatusLed.Fill = Res("DangerBrush");
                StatusTitle.Text = "Notaus aktiv";
                StatusText.Text = "Makropad wird ignoriert.";
            }
            else if (C.S.DeviceKey == null)
            {
                StatusLed.Fill = Res("SubBrush");
                StatusTitle.Text = "Kein Makropad";
                StatusText.Text = "Unter „Makropad“ auswählen.";
            }
            else if (dev == null)
            {
                StatusLed.Fill = Res("WarnBrush");
                StatusTitle.Text = C.S.DeviceName ?? "Makropad";
                StatusText.Text = "Nicht verbunden – wird automatisch erkannt, sobald du es einsteckst.";
            }
            else
            {
                bool blocking = inp.IcpActive && inp.IcpFiltered > 0;
                StatusLed.Fill = blocking || !C.S.BlockOriginalKeys ? Res("SuccessBrush") : Res("WarnBrush");
                StatusTitle.Text = dev.Name;
                StatusText.Text = blocking ? "Bereit · Makropad-Tasten werden abgefangen"
                    : C.S.BlockOriginalKeys ? "Bereit · Tasten werden NICHT blockiert (siehe „Makropad“)"
                    : "Bereit · ohne Blockieren";
            }
            HotkeyStatus.Text = inp.HotkeyOk
                ? "Das Notaus-Tastenkürzel Strg + Alt + F12 ist aktiv."
                : "Strg + Alt + F12 ist von einem anderen Programm belegt – nutze den Notaus-Knopf oder das Infobereich-Menü.";
        }

        void RefreshSetupHint()
        {
            string text = null, button = null;
            var dev = C.S.DeviceKey == null ? null : C.Input.FindDevice(C.S.DeviceKey);
            if (C.S.DeviceKey == null) { text = "Schritt 1: Wähle dein Makropad aus, damit nur dieses Gerät Sounds auslöst."; button = "Makropad wählen"; }
            else if (dev == null) { text = "Dein Makropad ist gerade nicht eingesteckt."; button = "Geräte ansehen"; }
            else if (!C.HasAnyButtonKeys) { text = "Schritt 2: Lerne einmal die 9 Tasten und 3 Regler an – dauert 30 Sekunden."; button = "Jetzt anlernen"; }
            else if (C.S.Sounds.Count == 0) { text = "Schritt 3: Zieh MP3-, WAV- oder OGG-Dateien direkt auf eine Taste."; button = "Sounds hinzufügen"; }

            SetupHint.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            SetupHintText.Text = text ?? "";
            SetupHintButton.Content = button ?? "";
        }

        void RefreshDeviceList()
        {
            var sel = DeviceList.SelectedItem as PadDevice;
            var list = C.Input.Devices.ToList();
            DeviceList.ItemsSource = list;
            DeviceList.SelectedItem = list.FirstOrDefault(d => d.Key == sel?.Key)
                                      ?? list.FirstOrDefault(d => d.IsSelected)
                                      ?? list.FirstOrDefault();
            RefreshDeviceDetails();
        }

        void RefreshDeviceDetails()
        {
            var d = DeviceList.SelectedItem as PadDevice;
            if (d == null)
            {
                DeviceDetails.Text = "Keine Tastatur gefunden.";
                UseDeviceButton.IsEnabled = false;
                return;
            }
            UseDeviceButton.IsEnabled = !d.IsSelected;
            UseDeviceButton.Content = d.IsSelected ? "Ist dein Makropad ✓" : "Dieses Gerät als Makropad verwenden";
            DeviceDetails.Text = $"Gerätekennung: {d.Key}\n" + string.Join("\n", d.Paths.Select(p => "HID-Pfad: " + p));
        }

        void RefreshPadPage()
        {
            bool was = _loading;
            _loading = true;
            try
            {
                BlockBox.IsChecked = C.S.BlockOriginalKeys;
                AdminButton.Visibility = Visibility.Collapsed;
                RetryIcpButton.Visibility = Visibility.Collapsed;
                var inp = C.Input;
                string text;
                Brush bg = Res("Card2Brush");

                if (inp.Emergency)
                {
                    text = "Notaus ist aktiv – es wird nichts erkannt und nichts blockiert.";
                    bg = Res("DangerSoftBrush");
                }
                else if (!C.S.BlockOriginalKeys)
                {
                    text = "Blockieren ist aus. Das Makropad löst Sounds aus, seine Tasten kommen aber zusätzlich normal am PC an.\nTipp: Mit der Software deines Makropads kannst du die Tasten auf F13–F24 legen – die benutzt kein Spiel.";
                }
                else if (!DeviceIds.IsVidPidKey(C.S.DeviceKey))
                {
                    text = C.S.DeviceKey == null
                        ? "Wähle oben zuerst dein Makropad aus."
                        : "Dieses Gerät hat keine USB-Kennung (VID/PID) und kann deshalb nicht sicher blockiert werden. Erkennung läuft trotzdem.";
                }
                else if (inp.IcpActive && inp.IcpFiltered > 0)
                {
                    text = $"✓ Aktiv: {inp.IcpFiltered} Schnittstelle(n) deines Makropads werden abgefangen. Alle anderen Tastaturen laufen ganz normal und unverändert.";
                    bg = Res("SuccessSoftBrush");
                }
                else if (inp.IcpActive)
                {
                    var ids = inp.InterceptionHardwareIds();
                    text = "Interception läuft, findet dein Makropad aber nicht. Ist es eingesteckt? Hinweis: Der Treiber erkennt neu eingesteckte Geräte manchmal erst nach einem PC-Neustart."
                           + (ids.Count > 0 ? "\nGefundene Geräte: " + string.Join(" | ", ids) : "");
                    bg = Res("WarnSoftBrush");
                    RetryIcpButton.Visibility = Visibility.Visible;
                }
                else
                {
                    bg = Res("WarnSoftBrush");
                    RetryIcpButton.Visibility = Visibility.Visible;
                    switch (inp.IcpStatus)
                    {
                        case InterceptionStatus.NotInstalled:
                            text = "Der Interception-Treiber wurde nicht gefunden. Krach erkennt das Makropad trotzdem, kann seine Tasten aber nicht blockieren. Installation: „install-interception.exe /install“ als Administrator ausführen und den PC neu starten.";
                            break;
                        case InterceptionStatus.AccessDenied:
                            text = "Windows verweigert den Zugriff auf den Interception-Treiber. Starte Krach einmal als Administrator.";
                            AdminButton.Visibility = Autostart.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
                            break;
                        case InterceptionStatus.Busy:
                            text = "Ein anderes Programm benutzt den Interception-Treiber gerade (z. B. dein HID-Monitor). Schließe es und klicke auf „Erneut versuchen“.";
                            break;
                        default:
                            text = "Interception konnte nicht gestartet werden. Krach erkennt das Makropad trotzdem (ohne Blockieren). Details im Protokoll.";
                            break;
                    }
                }
                IcpStatusText.Text = text;
                IcpStatusBorder.Background = bg;
                DetectText.Text = inp.DetectNext ? "Drücke jetzt eine Taste am Makropad …" : "Per Tastendruck erkennen";
            }
            finally { _loading = was; }
        }

        void RefreshAssignments()
        {
            AssignGrid.Children.Clear();
            for (int t = 0; t < Controller.TargetCount; t++)
            {
                int target = t;
                string key = KeyNames.Describe(C.GetTargetKey(t));
                bool active = C.LearnTarget == t;
                var name = new TextBlock { Text = Controller.TargetName(t), FontSize = 12, Foreground = Res("SubBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
                var val = new TextBlock { Text = key ?? "–", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
                var sp = new StackPanel();
                sp.Children.Add(name);
                sp.Children.Add(val);
                var b = new Border
                {
                    Child = sp,
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 8, 8),
                    Background = active ? Res("WarnSoftBrush") : Res("Card2Brush"),
                    BorderBrush = active ? Res("WarnBrush") : Brushes.Transparent,
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = "Klicken zum (Neu-)Anlernen · Rechtsklick zum Löschen"
                };
                b.MouseLeftButtonUp += (s, e) => C.StartLearn(target, true);
                b.MouseRightButtonUp += (s, e) => C.ClearTargetKey(target);
                AssignGrid.Children.Add(b);
            }
        }

        void RefreshLearn()
        {
            int t = C.LearnTarget;
            LearnPanel.Visibility = t >= 0 ? Visibility.Visible : Visibility.Collapsed;
            LearnAllButton.IsEnabled = t < 0;
            if (t >= 0)
            {
                LearnStep.Text = $"Schritt {t + 1} von {Controller.TargetCount}";
                LearnText.Text = "Drücke am Makropad: " + Controller.TargetName(t);
                if (PagePad.Visibility != Visibility.Visible)
                    ShowMessage("Drücke jetzt am Makropad: " + Controller.TargetName(t), true);
            }
            else if (_messageSticky)
            {
                _messageSticky = false;
                MessageBar.Visibility = Visibility.Collapsed;
            }
            RefreshAssignments();
            RefreshKnobs();
            for (int i = 0; i < 9; i++) _slotBorderKey[i] = null;
            UpdateSlotStates();
            if (t < 0)
            {
                RefreshSlots();
                RefreshSetupHint();
            }
        }

        void RefreshAudioPage()
        {
            bool was = _loading;
            _loading = true;
            try
            {
                _render = AudioDevices.List(DataFlow.Render);
                _capture = AudioDevices.List(DataFlow.Capture);
                var s = C.S;

                var mon = new List<AudioDeviceEntry> { new AudioDeviceEntry { Id = AudioDevices.DefaultId, Name = "Windows-Standard" } };
                mon.AddRange(_render.Where(d => !d.IsVirtual));
                mon.AddRange(_render.Where(d => d.IsVirtual));
                mon.Add(new AudioDeviceEntry { Id = AudioDevices.OffId, Name = "Aus (Sounds nicht selbst hören)" });
                MonitorBox.ItemsSource = mon;
                MonitorBox.SelectedItem = mon.FirstOrDefault(d => d.Id == s.MonitorDeviceId) ?? mon[0];

                var mics = new List<AudioDeviceEntry> { new AudioDeviceEntry { Id = AudioDevices.DefaultId, Name = "Windows-Standard-Mikrofon" } };
                mics.AddRange(_capture.Where(d => !d.IsVirtual));
                mics.AddRange(_capture.Where(d => d.IsVirtual));
                MicBox.ItemsSource = mics;
                MicBox.SelectedItem = mics.FirstOrDefault(d => d.Id == s.MicDeviceId) ?? mics[0];

                var mix = new List<AudioDeviceEntry> { new AudioDeviceEntry { Id = "", Name = "— bitte virtuelles Kabel wählen —" } };
                mix.AddRange(_render.Where(d => d.IsVirtual));
                mix.AddRange(_render.Where(d => !d.IsVirtual));
                MixBox.ItemsSource = mix;
                MixBox.SelectedItem = mix.FirstOrDefault(d => d.Id == s.MixDeviceId) ?? mix[0];

                MixEnabledBox.IsChecked = s.MixEnabled;
                MicIntoMixBox.IsChecked = s.MicIntoMix;
                SoundsIntoMixBox.IsChecked = s.SoundsIntoMix;
                MicBox.IsEnabled = MixBox.IsEnabled = MicVol.IsEnabled = s.MixEnabled;

                // Status des virtuellen Kabels
                var cable = AudioDevices.FindVirtualCableInput(_render);
                var selectedMix = _render.FirstOrDefault(d => d.Id == s.MixDeviceId);
                var partner = AudioDevices.FindPartnerCapture(selectedMix?.Name ?? cable?.Name, _capture);
                string partnerName = partner?.Name ?? "CABLE Output (VB-Audio Virtual Cable)";

                if (cable == null && selectedMix == null)
                {
                    CableText.Text = "Kein virtuelles Audiokabel gefunden. Installiere das kostenlose VB-CABLE und starte den PC neu – Krach erkennt es danach automatisch.";
                    CableBorder.Background = Res("WarnSoftBrush");
                    CableSetupButton.Visibility = Visibility.Collapsed;
                    CableDownloadButton.Visibility = Visibility.Visible;
                }
                else if (s.MixEnabled && selectedMix != null && C.Audio.ChatActive)
                {
                    CableText.Text = $"✓ Eingerichtet: Krach mischt in „{selectedMix.Name}“.";
                    CableBorder.Background = Res("SuccessSoftBrush");
                    CableSetupButton.Visibility = Visibility.Collapsed;
                    CableDownloadButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    CableText.Text = $"Virtuelles Kabel gefunden: „{(selectedMix ?? cable).Name}“. Ein Klick richtet alles ein.";
                    CableBorder.Background = Res("Card2Brush");
                    CableSetupButton.Visibility = Visibility.Visible;
                    CableDownloadButton.Visibility = Visibility.Collapsed;
                }

                DiscordGuide.Text =
                    "1. Discord → Benutzereinstellungen → Sprache & Video\n" +
                    $"2. Eingabegerät: „{partnerName}“\n" +
                    "3. Ausgabegerät: deine Kopfhörer (nicht das Kabel)\n" +
                    "4. Rauschunterdrückung auf „Keine“ und Echo-Unterdrückung aus – sonst filtert Discord die Sounds heraus\n" +
                    $"In Spielen genauso: als Mikrofon „{partnerName}“ wählen.";

                var problems = C.Audio.Problems.Distinct().ToList();
                if (s.MixEnabled && selectedMix != null && s.MonitorDeviceId == selectedMix.Id)
                    problems.Add("Mithören und Voice-Chat gehen auf dasselbe Gerät – wähle zum Mithören deine Kopfhörer.");
                AudioProblemsBorder.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                AudioProblems.Text = string.Join("\n", problems.Select(p => "⚠ " + p));

                RefreshLive();
            }
            finally { _loading = was; }
        }

        void RefreshSettings()
        {
            AutostartBox.IsChecked = C.S.Autostart;
            AutostartTrayBox.IsChecked = C.S.AutostartToTray;
            CloseTrayBox.IsChecked = C.S.CloseToTray;
            SingleBox.IsChecked = C.S.SingleSoundOnly;
        }

        // =====================================================================
        //  Timer: Pegel & Wiedergabestatus
        // =====================================================================

        void Tick(object sender, EventArgs e)
        {
            float mon = C.Audio.TakeMonitorPeak();
            float chat = C.Audio.TakeChatPeak();
            float mic = C.Audio.TakeMicPeak();
            _lvMonitor = Math.Max(mon, _lvMonitor * 0.82f);
            _lvChat = Math.Max(chat, _lvChat * 0.82f);
            _lvMic = Math.Max(mic, _lvMic * 0.82f);
            _lvBoard = C.Audio.MonitorActive ? _lvMonitor : _lvChat;

            if (PageBoard.Visibility == Visibility.Visible)
            {
                SetMeter(BoardMeterTrack, BoardMeterFill, _lvBoard);
                UpdateSlotStates();
            }
            if (PageAudio.Visibility == Visibility.Visible)
            {
                SetMeter(MonitorMeterTrack, MonitorMeterFill, _lvMonitor);
                SetMeter(MicMeterTrack, MicMeterFill, _lvMic);
                SetMeter(ChatMeterTrack, ChatMeterFill, _lvChat);
            }
        }

        static void SetMeter(Border track, Border fill, float level)
        {
            double w = track.ActualWidth * Math.Clamp(Math.Sqrt(level), 0, 1);
            fill.Width = w < 1 ? 0 : w;
        }

        // =====================================================================
        //  Allgemeine Ereignisse
        // =====================================================================

        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageBoard == null) return;
            PageBoard.Visibility = sender == NavBoard ? Visibility.Visible : Visibility.Collapsed;
            PageLibrary.Visibility = sender == NavLibrary ? Visibility.Visible : Visibility.Collapsed;
            PagePad.Visibility = sender == NavPad ? Visibility.Visible : Visibility.Collapsed;
            PageAudio.Visibility = sender == NavAudio ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = sender == NavSettings ? Visibility.Visible : Visibility.Collapsed;
            if (sender == NavPad && _messageSticky) { _messageSticky = false; MessageBar.Visibility = Visibility.Collapsed; }
            if (sender == NavLibrary) RefreshLibEditor();
        }

        void ShowMessage(string text, bool sticky)
        {
            _messageSticky = sticky;
            MessageText.Text = text;
            MessageBar.Visibility = Visibility.Visible;
            _messageTimer.Stop();
            if (!sticky) _messageTimer.Start();
        }

        void MessageClose_Click(object sender, RoutedEventArgs e)
        {
            _messageSticky = false;
            MessageBar.Visibility = Visibility.Collapsed;
        }

        void EmergencyButton_Click(object sender, RoutedEventArgs e)
        {
            C.ToggleEmergency();
            if (C.LearnTarget >= 0) C.CancelLearn();
        }

        void SetupHint_Click(object sender, RoutedEventArgs e)
        {
            string b = SetupHintButton.Content as string;
            if (b == "Jetzt anlernen") { NavPad.IsChecked = true; C.StartLearn(0, false); }
            else if (b == "Sounds hinzufügen") AddSounds_Click(sender, e);
            else NavPad.IsChecked = true;
        }

        // ---------------- Seiten ----------------

        void AddPage_Click(object sender, RoutedEventArgs e) => C.AddPage();

        void DeletePage_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, $"Seite „{C.Page.Name}“ löschen? Die Sounds bleiben in der Bibliothek.", "Seite löschen",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                C.DeletePage();
        }

        void PageNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { C.RenamePage(PageNameBox.Text); Keyboard.ClearFocus(); e.Handled = true; }
        }

        void PageNameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => C.RenamePage(PageNameBox.Text);

        // ---------------- Tasten / Slots ----------------

        void SlotPlay_Click(object sender, RoutedEventArgs e) => C.TriggerSlot(_selSlot);

        void SlotLearn_Click(object sender, RoutedEventArgs e) => C.StartLearn(_selSlot, true);

        void SlotClear_Click(object sender, RoutedEventArgs e) => C.AssignSlot(_selSlot, null);

        void SlotSoundBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !(SlotSoundBox.SelectedItem is SoundItem item)) return;
            C.AssignSlot(_selSlot, item == _noneItem ? null : item);
        }

        void Slot_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DragFormat) || e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void Slot_Drop(int idx, DragEventArgs e)
        {
            e.Handled = true;
            if (e.Data.GetDataPresent(DragFormat))
            {
                C.AssignSlot(idx, C.FindSound(e.Data.GetData(DragFormat) as string));
            }
            else if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                C.AssignFilesToSlots(idx, files);
            }
            SelectSlot(idx);
        }

        void Library_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(null);
            var container = ItemsControl.ContainerFromElement((ItemsControl)sender, e.OriginalSource as DependencyObject) as ListBoxItem;
            _dragItem = container?.DataContext as SoundItem;
        }

        void Library_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;
            var d = e.GetPosition(null) - _dragStart;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var item = _dragItem;
            _dragItem = null;
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DragFormat, item.Id), DragDropEffects.Copy);
        }

        void MiniLibrary_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (MiniLibrary.SelectedItem is SoundItem item) C.AssignSlot(_selSlot, item);
        }

        // ---------------- Wiedergabe ----------------

        void StopAll_Click(object sender, RoutedEventArgs e) => C.Audio.StopAll();

        void PauseAll_Click(object sender, RoutedEventArgs e) => C.Audio.TogglePauseAll();

        void BoardVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            C.SetSoundVolume(e.NewValue);
            BoardVolumeText.Text = $"{Math.Round(e.NewValue * 100)} %";
            RefreshKnobs();
        }

        // ---------------- Bibliothek ----------------

        void Files_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void Library_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                var added = C.AddFiles(files);
                if (added.Count > 0) LibraryList.SelectedItem = added.Last();
            }
        }

        void AddSounds_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Sounds hinzufügen",
                Multiselect = true,
                Filter = "Audiodateien|*.mp3;*.wav;*.ogg;*.flac;*.m4a;*.aac;*.wma|Alle Dateien|*.*"
            };
            if (dlg.ShowDialog(this) == true)
            {
                var added = C.AddFiles(dlg.FileNames);
                if (added.Count > 0)
                {
                    NavLibrary.IsChecked = true;
                    LibraryList.SelectedItem = added.Last();
                    ShowMessage($"{added.Count} Sound(s) hinzugefügt. Zieh sie im Soundboard auf eine Taste.", false);
                }
            }
        }

        void OpenSoundsFolder_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(AppPaths.SoundsDir);
            OpenPath(AppPaths.SoundsDir);
        }

        void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_loading) return;
            bool was = _loading;
            _loading = true;
            try { RefreshLibraryLists(); }
            finally { _loading = was; }
        }

        void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _loading = true;
            try { RefreshLibEditor(); }
            finally { _loading = false; }
        }

        void LibraryList_DoubleClick(object sender, MouseButtonEventArgs e) => LibPlay_Click(sender, e);

        void LibPlay_Click(object sender, RoutedEventArgs e)
        {
            if (!(LibraryList.SelectedItem is SoundItem item)) return;
            if (C.Audio.StateOf(item.Id) > 0) { C.Audio.StopSound(item.Id); return; }
            try { C.Audio.Play(item); }
            catch (Exception ex) { ShowMessage($"„{item.Name}“ konnte nicht abgespielt werden: {ex.Message}", false); }
        }

        void LibAssign_Click(object sender, RoutedEventArgs e)
        {
            if (!(LibraryList.SelectedItem is SoundItem item)) return;
            C.AssignSlot(_selSlot, item);
            ShowMessage($"„{item.Name}“ liegt jetzt auf {C.Page.Name}, Taste {_selSlot + 1}.", false);
        }

        void LibDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!(LibraryList.SelectedItem is SoundItem item)) return;
            if (MessageBox.Show(this, $"„{item.Name}“ endgültig aus der Bibliothek löschen?", "Sound löschen",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            LibraryList.SelectedItem = null;
            C.DeleteSound(item);
        }

        // ---------------- Makropad ----------------

        void OnKeyObserved(KeyObservation k)
        {
            if (!k.Down) return;
            LiveKey.Text = k.KeyName ?? k.KeyId;
            LiveDevice.Text = k.DeviceName;
            if (k.FromTarget)
            {
                string action = "Taste noch nicht angelernt.";
                int btn = Array.IndexOf(C.S.ButtonKeys, k.KeyId);
                if (C.LearnTarget >= 0) action = "Wird gerade angelernt.";
                else if (btn >= 0) action = C.SlotSound(btn) is SoundItem si ? $"Löst Taste {btn + 1} aus: „{si.Name}“" : $"Taste {btn + 1} (noch kein Sound)";
                else
                {
                    for (int t = 9; t < Controller.TargetCount; t++)
                        if (C.GetTargetKey(t) == k.KeyId) action = Controller.TargetName(t);
                }
                LiveResult.Text = "✓ Dein Makropad" + (k.ViaInterception ? " (blockiert)" : "") + " · " + action;
                LiveResult.Foreground = Res("SuccessBrush");
            }
            else
            {
                LiveResult.Text = "Andere Tastatur → wird ignoriert, löst keinen Sound aus.";
                LiveResult.Foreground = Res("SubBrush");
            }
        }

        void OnDeviceDetected(PadDevice dev)
        {
            C.SelectDevice(dev);
            ShowMessage($"Erkannt und ausgewählt: {dev.Name} ({DeviceIds.VidPidDisplay(dev.Key)}).", false);
            RefreshPadPage();
        }

        void Detect_Click(object sender, RoutedEventArgs e)
        {
            C.Input.DetectNext = !C.Input.DetectNext;
            RefreshPadPage();
        }

        void RefreshDevices_Click(object sender, RoutedEventArgs e) => C.Input.RefreshDevices();

        void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshDeviceDetails();

        void DeviceList_DoubleClick(object sender, MouseButtonEventArgs e) => UseDevice_Click(sender, e);

        void UseDevice_Click(object sender, RoutedEventArgs e)
        {
            if (!(DeviceList.SelectedItem is PadDevice d)) return;
            if (!d.IsMacropadGuess && d.Paths.Count > 0 && C.S.DeviceKey == null &&
                MessageBox.Show(this, $"„{d.Name}“ sieht nicht wie ein Makropad aus. Wenn das deine normale Tastatur ist, würden ihre Tasten Sounds auslösen (und beim Blockieren nicht mehr tippen).\n\nTrotzdem verwenden?",
                    "Gerät verwenden", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            C.SelectDevice(d);
            ShowMessage($"„{d.Name}“ ist jetzt dein Makropad.", false);
        }

        void ClearDevice_Click(object sender, RoutedEventArgs e) => C.SelectDevice(null);

        void BlockBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            C.SetBlocking(BlockBox.IsChecked == true);
        }

        void Admin_Click(object sender, RoutedEventArgs e) => App.Current.RestartElevated();

        void RetryIcp_Click(object sender, RoutedEventArgs e)
        {
            C.Input.Configure(C.S.DeviceKey, false);
            C.Input.Configure(C.S.DeviceKey, C.S.BlockOriginalKeys);
            RefreshPadPage();
        }

        void LearnAll_Click(object sender, RoutedEventArgs e) => C.StartLearn(0, false);
        void LearnSkip_Click(object sender, RoutedEventArgs e) => C.SkipLearn();
        void LearnCancel_Click(object sender, RoutedEventArgs e) => C.CancelLearn();

        // ---------------- Audio ----------------

        void AudioBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            C.S.MonitorDeviceId = (MonitorBox.SelectedItem as AudioDeviceEntry)?.Id ?? "";
            C.S.MicDeviceId = (MicBox.SelectedItem as AudioDeviceEntry)?.Id ?? "";
            C.S.MixDeviceId = (MixBox.SelectedItem as AudioDeviceEntry)?.Id ?? "";
            C.ApplyAudio();
        }

        void MonitorVol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            C.SetMonitorVolume(e.NewValue);
            MonitorVolText.Text = $"{Math.Round(e.NewValue * 100)} %";
            RefreshKnobs();
        }

        void MicVol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            C.SetMicVolume(e.NewValue);
            MicVolText.Text = $"{Math.Round(e.NewValue * 100)} %";
            RefreshKnobs();
        }

        void MicMute_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            C.SetMicMuted(MicMuteBox.IsChecked == true);
        }

        void MixEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            C.S.MixEnabled = MixEnabledBox.IsChecked == true;
            if (C.S.MixEnabled && string.IsNullOrEmpty(C.S.MixDeviceId))
            {
                var cable = AudioDevices.FindVirtualCableInput(_render);
                if (cable != null) C.S.MixDeviceId = cable.Id;
            }
            C.ApplyAudio();
        }

        void MixOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            C.S.MicIntoMix = MicIntoMixBox.IsChecked == true;
            C.S.SoundsIntoMix = SoundsIntoMixBox.IsChecked == true;
            C.ApplyAudio();
        }

        void FxOn_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            C.SetVoiceEffect(C.S.VoiceEffect, FxOnBox.IsChecked == true);
            if (FxOnBox.IsChecked == true && !(C.S.MixEnabled && C.S.MicIntoMix))
                ShowMessage("Der Stimmeffekt wirkt im Voice-Chat-Modus – aktiviere ihn oben und wähle dein Mikrofon.", false);
            RefreshKnobs();
        }

        void CableSetup_Click(object sender, RoutedEventArgs e)
        {
            var cable = _render.FirstOrDefault(d => d.Id == C.S.MixDeviceId && d.IsVirtual) ?? AudioDevices.FindVirtualCableInput(_render);
            if (cable == null) return;
            C.S.MixDeviceId = cable.Id;
            C.S.MixEnabled = true;
            C.S.MicIntoMix = true;
            C.S.SoundsIntoMix = true;
            // Falls Windows das Kabel als Standard-Ausgabe nutzt, zum Mithören echte Kopfhörer vorschlagen
            if (string.IsNullOrEmpty(C.S.MonitorDeviceId))
            {
                var realOut = _render.FirstOrDefault(d => !d.IsVirtual);
                if (realOut != null && IsDefaultRenderVirtual()) C.S.MonitorDeviceId = realOut.Id;
            }
            if (string.IsNullOrEmpty(C.S.MicDeviceId))
            {
                var realMic = _capture.FirstOrDefault(d => !d.IsVirtual);
                if (realMic != null && IsDefaultCaptureVirtual()) C.S.MicDeviceId = realMic.Id;
            }
            C.ApplyAudio();
            var partner = AudioDevices.FindPartnerCapture(cable.Name, _capture);
            ShowMessage($"Fertig! Wähle jetzt in Discord als Eingabegerät „{partner?.Name ?? "CABLE Output"}“.", false);
        }

        static bool IsDefaultRenderVirtual()
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return AudioDevices.IsVirtualName(d.FriendlyName);
            }
            catch { return false; }
        }

        static bool IsDefaultCaptureVirtual()
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                using var d = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                return AudioDevices.IsVirtualName(d.FriendlyName);
            }
            catch { return false; }
        }

        void CableDownload_Click(object sender, RoutedEventArgs e) => OpenPath("https://vb-audio.com/Cable/");

        // ---------------- Einstellungen ----------------

        void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool autostart = AutostartBox.IsChecked == true;
            if (autostart != C.S.Autostart)
            {
                C.S.Autostart = autostart;
                Autostart.Set(autostart);
            }
            C.S.AutostartToTray = AutostartTrayBox.IsChecked == true;
            C.S.CloseToTray = CloseTrayBox.IsChecked == true;
            C.S.SingleSoundOnly = SingleBox.IsChecked == true;
            C.Audio.SingleOnly = C.S.SingleSoundOnly;
            C.SaveSoon();
        }

        void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            OpenPath(AppPaths.DataDir);
        }

        void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(AppPaths.LogFile)) Log.Write("Protokoll angelegt.");
            OpenPath(AppPaths.LogFile);
        }

        static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("Öffnen fehlgeschlagen: " + ex.Message); }
        }

        // ---------------- Hilfen ----------------

        static Color ToColor(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return Color.FromRgb(0x7C, 0x5C, 0xFF); }
        }

        static Color Darken(Color c, double f) =>
            Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
    }
}
