using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MagickImage = ImageMagick.MagickImage;
using MagickFormat = ImageMagick.MagickFormat;
using Microsoft.Win32;

namespace Tangelo.App;

/// Colours, fonts and small builders so windows can be composed in code.
static class Theme
{
    public static readonly Color AccentColor = Color.FromRgb(0xFF, 0x52, 0x00);
    public static readonly SolidColorBrush Accent = Freeze(new SolidColorBrush(AccentColor));
    public static readonly SolidColorBrush AccentSoft = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0x52, 0x00)));

    public static bool Dark
    {
        get
        {
            try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0; }
            catch { return false; }
        }
    }

    public static SolidColorBrush Surface => Freeze(new SolidColorBrush(Dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF9, 0xF9, 0xF9)));
    public static SolidColorBrush SurfaceTranslucent => Freeze(new SolidColorBrush(Dark ? Color.FromArgb(0xF0, 0x26, 0x26, 0x26) : Color.FromArgb(0xF2, 0xFB, 0xFB, 0xFB)));
    public static SolidColorBrush Card => Freeze(new SolidColorBrush(Dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0C, 0, 0, 0)));
    public static SolidColorBrush Text => Freeze(new SolidColorBrush(Dark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A)));
    public static SolidColorBrush Subtle => Freeze(new SolidColorBrush(Dark ? Color.FromRgb(0xA8, 0xA8, 0xA8) : Color.FromRgb(0x60, 0x60, 0x60)));
    public static SolidColorBrush Border => Freeze(new SolidColorBrush(Dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x20, 0, 0, 0)));

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}

static class Ui
{
    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, Brush? brush = null, bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = Theme.Font,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = brush ?? Theme.Text,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Caption(string text, bool wrap = true) => Text(text, 12, brush: Theme.Subtle, wrap: wrap);

    public static TextBlock Glyph(string glyph, double size = 16, Brush? brush = null) => new()
    {
        Text = glyph, FontFamily = Theme.Icons, FontSize = size, Foreground = brush ?? Theme.Text,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    public static Button Button(object content, Action onClick, bool primary = false, string? tooltip = null)
    {
        var b = new Button { Content = content, Padding = new Thickness(14, 6, 14, 6), MinWidth = 72, ToolTip = tooltip };
        if (primary) { b.Background = Theme.Accent; b.Foreground = Brushes.White; b.BorderBrush = Theme.Accent; b.FontWeight = FontWeights.SemiBold; }
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button IconButton(string glyph, Action onClick, string tooltip)
    {
        var b = new Button { Content = Glyph(glyph, 14), Width = 32, Height = 30, Padding = new Thickness(0), ToolTip = tooltip, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static StackPanel Row(double spacing, params UIElement[] children) => Stack(Orientation.Horizontal, spacing, children);
    public static StackPanel Column(double spacing, params UIElement[] children) => Stack(Orientation.Vertical, spacing, children);

    static StackPanel Stack(Orientation o, double spacing, UIElement[] children)
    {
        var s = new StackPanel { Orientation = o };
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] is FrameworkElement fe && i > 0)
                fe.Margin = o == Orientation.Horizontal ? new Thickness(spacing, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom) : new Thickness(fe.Margin.Left, spacing, fe.Margin.Right, fe.Margin.Bottom);
            if (children[i] is FrameworkElement v && o == Orientation.Horizontal) v.VerticalAlignment = VerticalAlignment.Center;
            s.Children.Add(children[i]);
        }
        return s;
    }

    public static Border Card(UIElement child, double padding = 12) => new()
    {
        Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(8),
        Background = Theme.Card, BorderBrush = Theme.Border, BorderThickness = new Thickness(1),
    };

    public static FrameworkElement Labeled(string label, UIElement control, double labelWidth = 150)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var t = Text(label); t.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(t);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    public static FrameworkElement Slider(string label, double min, double max, double value, Action<double> changed, Func<double, string>? format = null, double step = 0)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center };
        if (step > 0) { s.TickFrequency = step; s.IsSnapToTickEnabled = true; }
        var readout = Text((format ?? (v => v.ToString("0.00")))(value), 12, brush: Theme.Subtle);
        readout.Width = 56; readout.TextAlignment = TextAlignment.Right; readout.VerticalAlignment = VerticalAlignment.Center;
        s.ValueChanged += (_, e) => { readout.Text = (format ?? (v => v.ToString("0.00")))(e.NewValue); changed(e.NewValue); };
        var dock = new DockPanel();
        DockPanel.SetDock(readout, Dock.Right);
        dock.Children.Add(readout);
        dock.Children.Add(s);
        return Labeled(label, dock);
    }

    public static CheckBox Toggle(string label, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }

    public static ComboBox Combo<T>(IEnumerable<T> items, T selected, Func<T, string> display, Action<T> changed)
    {
        var list = items.ToList();
        var c = new ComboBox { MinWidth = 160 };
        foreach (var i in list) c.Items.Add(new ComboBoxItem { Content = display(i), Tag = i });
        c.SelectedIndex = Math.Max(0, list.IndexOf(selected));
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is ComboBoxItem { Tag: T t }) changed(t); };
        return c;
    }

    public static FrameworkElement Segmented<T>(IEnumerable<T> items, T selected, Func<T, string> display, Action<T> changed)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<ToggleButton>();
        foreach (var item in items)
        {
            var b = new ToggleButton { Content = display(item), Padding = new Thickness(12, 5, 12, 5), IsChecked = Equals(item, selected), Margin = new Thickness(0, 0, 2, 0) };
            b.Click += (_, _) => { foreach (var o in buttons) o.IsChecked = o == b; changed(item); };
            buttons.Add(b);
            panel.Children.Add(b);
        }
        return panel;
    }

    public static TextBox Field(string value, Action<string> changed, double width = double.NaN)
    {
        var t = new TextBox { Text = value, Width = width, Padding = new Thickness(6, 4, 6, 4) };
        t.TextChanged += (_, _) => changed(t.Text);
        return t;
    }

    public static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    public static BitmapSource ToBitmap(MagickImage img)
    {
        using var copy = (MagickImage)img.Clone();
        var bytes = copy.ToByteArray(MagickFormat.Png32);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static BitmapSource? LoadBitmap(string path, int decodeWidth = 0)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// Shows the standard colour picker (WinForms) and returns "#RRGGBB" or null.
    public static string? PickColor(string current)
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.ColorTranslator.FromHtml(current) };
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}" : null;
    }

    public static string? PickFile(string filter, string title)
    {
        var d = new OpenFileDialog { Filter = filter, Title = title };
        return d.ShowDialog() == true ? d.FileName : null;
    }

    public static string? PickFolder(string title)
    {
        var d = new OpenFolderDialog { Title = title };
        return d.ShowDialog() == true ? d.FolderName : null;
    }

    public static void Copy(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return; } catch { Thread.Sleep(50); }
        }
    }

    public static void OnKey(this Window w, Key key, Action action, ModifierKeys mods = ModifierKeys.None) =>
        w.PreviewKeyDown += (_, e) => { if (e.Key == key && Keyboard.Modifiers == mods && e.OriginalSource is not TextBox) { action(); e.Handled = true; } };
}
