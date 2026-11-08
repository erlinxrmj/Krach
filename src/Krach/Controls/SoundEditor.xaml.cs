using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Krach.Core;

namespace Krach.Controls
{
    /// <summary>Bearbeitet Name, Lautstärke, Modus, Schleife und Farbe eines Sounds.</summary>
    public partial class SoundEditor : UserControl
    {
        SoundItem _item;
        bool _loading;

        /// <summary>Wert geändert, der nur gespeichert werden muss.</summary>
        public event Action<SoundItem> Edited;
        /// <summary>Name oder Farbe geändert – Listen/Tasten neu zeichnen.</summary>
        public event Action<SoundItem> VisualChanged;

        public SoundEditor()
        {
            InitializeComponent();
            ModeBox.Items.Add("Start / Stopp (nochmal drücken stoppt)");
            ModeBox.Items.Add("Neustart bei jedem Druck");
            ModeBox.Items.Add("Überlagern (mehrfach gleichzeitig)");
            foreach (var hex in Controller.Palette)
            {
                var sw = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Margin = new Thickness(0, 0, 8, 8),
                    Background = BrushFrom(hex),
                    BorderBrush = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Tag = hex
                };
                sw.MouseLeftButtonUp += Swatch_Click;
                ColorPanel.Children.Add(sw);
            }
            SetItem(null);
        }

        public SoundItem Item => _item;

        public void SetItem(SoundItem item)
        {
            _loading = true;
            _item = item;
            IsEnabled = item != null;
            Opacity = item != null ? 1 : 0.5;
            NameBox.Text = item?.Name ?? "";
            VolSlider.Value = item?.Volume ?? 1.0;
            VolText.Text = $"{Math.Round(VolSlider.Value * 100)} %";
            ModeBox.SelectedIndex = item == null ? 0 : (int)item.Mode;
            LoopBox.IsChecked = item?.Loop == true;
            FileText.Text = item == null ? "Kein Sound ausgewählt." :
                (File.Exists(item.FilePath) ? Path.GetFileName(item.FilePath) : "⚠ Datei fehlt: " + item.FilePath);
            UpdateSwatches();
            _loading = false;
        }

        void UpdateSwatches()
        {
            foreach (Border b in ColorPanel.Children)
                b.BorderThickness = new Thickness(_item != null && string.Equals((string)b.Tag, _item.Color, StringComparison.OrdinalIgnoreCase) ? 3 : 0);
        }

        void CommitName()
        {
            if (_loading || _item == null) return;
            string n = NameBox.Text.Trim();
            if (n.Length == 0) { NameBox.Text = _item.Name; return; }
            if (n == _item.Name) return;
            _item.Name = n;
            VisualChanged?.Invoke(_item);
        }

        void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitName(); e.Handled = true; }
        }

        void NameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitName();

        void VolSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolText != null) VolText.Text = $"{Math.Round(e.NewValue * 100)} %";
            if (_loading || _item == null) return;
            _item.Volume = Math.Round(e.NewValue, 2);
            Edited?.Invoke(_item);
        }

        void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _item == null || ModeBox.SelectedIndex < 0) return;
            _item.Mode = (TriggerMode)ModeBox.SelectedIndex;
            Edited?.Invoke(_item);
        }

        void LoopBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _item == null) return;
            _item.Loop = LoopBox.IsChecked == true;
            VisualChanged?.Invoke(_item);
        }

        void Swatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_item == null) return;
            _item.Color = (string)((Border)sender).Tag;
            UpdateSwatches();
            VisualChanged?.Invoke(_item);
        }

        public static Brush BrushFrom(string hex)
        {
            try
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                b.Freeze();
                return b;
            }
            catch
            {
                return Brushes.SlateBlue;
            }
        }
    }
}
