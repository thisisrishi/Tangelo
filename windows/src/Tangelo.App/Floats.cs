using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tangelo.Core;

namespace Tangelo.App;

static class FloatManager
{
    static readonly List<FloatWindow> floats = [];
    static bool restoring;

    public static IReadOnlyList<FloatWindow> All => floats;

    public static FloatWindow NewFloat(IEnumerable<string> paths, Point? at = null)
    {
        var w = new FloatWindow();
        w.Add(paths);
        var p = at ?? Native.CursorDips();
        w.Left = p.X - 130; w.Top = p.Y - 160;
        floats.Add(w);
        w.Closed += (_, _) => { floats.Remove(w); Save(); };
        w.Show();
        Save();
        return w;
    }

    /// Created by shaking mid-drag; closes itself if nothing is dropped.
    public static void NewTransientFloat()
    {
        var w = NewFloat([]);
        w.Transient = true;
    }

    public static void DragEnded()
    {
        foreach (var f in floats.Where(f => f.Transient).ToList())
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            t.Tick += (_, _) => { t.Stop(); if (f.Transient && f.Count == 0) f.Close(); };
            t.Start();
        }
    }

    public static void Save()
    {
        if (restoring) return;
        Settings.Current.Floats = floats.Where(f => f.Count > 0).Select(f => new Settings.FloatState { Paths = f.Paths.ToList(), Left = f.Left, Top = f.Top }).ToList();
        Settings.Current.Save();
    }

    public static void Restore()
    {
        if (!Settings.Current.RestoreFloats) return;
        restoring = true;
        foreach (var s in Settings.Current.Floats)
        {
            var paths = s.Paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (paths.Count == 0) continue;
            NewFloat(paths, new Point(s.Left + 130, s.Top + 160));
        }
        restoring = false;
    }
}

sealed class FloatWindow : Window
{
    readonly ObservableCollection<Tile> tiles = [];
    readonly ListBox list;
    readonly TextBlock header = Ui.Text("Float", 12, FontWeights.SemiBold);
    readonly TextBlock sub = Ui.Caption("", false);
    readonly Border shell;
    readonly Grid emptyState;
    bool collapsed;
    Point? dragStart;

    public bool Transient { get; set; }
    public int Count => tiles.Count;
    public IEnumerable<string> Paths => tiles.Select(t => t.Path);

    public sealed class Tile
    {
        public required string Path { get; init; }
        public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));
        public BitmapSource? Thumb { get; set; }
    }

    public FloatWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Width = 260; Height = 320; MinWidth = 180; MinHeight = 120;
        AllowDrop = true;

        list = new ListBox
        {
            ItemsSource = tiles, SelectionMode = SelectionMode.Extended, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ItemTemplate = TileTemplate(),
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel))),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, System.Windows.Controls.ScrollBarVisibility.Disabled);
        list.SelectionChanged += (_, _) => UpdateHeader();
        list.MouseDoubleClick += (_, _) => { foreach (var t in Selected) Explorer.Open(t); };
        list.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(this);
        list.PreviewMouseMove += OnTileDrag;
        list.ContextMenuOpening += (_, e) => { list.ContextMenu = BuildMenu(SelectedOrAll); };
        list.ContextMenu = new ContextMenu();

        var dragAll = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = Theme.AccentSoft, Cursor = Cursors.SizeAll,
            Child = Ui.Glyph("", 14, Theme.Accent), ToolTip = "Drag selected (or all) files out",
        };
        dragAll.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && Count > 0) DragOut(SelectedOrAll); };

        var top = new DockPanel { Margin = new Thickness(10, 8, 6, 8) };
        var buttons = Ui.Row(0,
            Ui.IconButton("", () => { var m = BuildMenu(SelectedOrAll, includeManage: true); m.PlacementTarget = this; m.IsOpen = true; }, "More"),
            Ui.IconButton("", ToggleCollapse, "Collapse / expand"),
            Ui.IconButton("", Close, "Close Float without deleting files"));
        DockPanel.SetDock(dragAll, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        top.Children.Add(dragAll);
        top.Children.Add(buttons);
        var titles = Ui.Column(0, header, sub); titles.Margin = new Thickness(8, 0, 0, 0); titles.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(titles);
        top.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); FloatManager.Save(); };

        emptyState = new Grid { Children = { Ui.Column(6, Ui.Glyph("", 28, Theme.Subtle), Ui.Caption("Drop files here", false)) } };
        ((StackPanel)emptyState.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        ((StackPanel)emptyState.Children[0]).HorizontalAlignment = HorizontalAlignment.Center;

        var body = new Grid { Children = { list, emptyState } };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(body);
        shell = new Border
        {
            CornerRadius = new CornerRadius(14), Background = Theme.SurfaceTranslucent, BorderBrush = Theme.Border, BorderThickness = new Thickness(1),
            Child = dock, Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.3 },
        };
        Content = shell;

        DragEnter += (_, e) => { shell.BorderBrush = Theme.Accent; shell.BorderThickness = new Thickness(2); Transient = Transient && Count == 0; };
        DragLeave += (_, _) => { shell.BorderBrush = Theme.Border; shell.BorderThickness = new Thickness(1); };
        Drop += (_, e) =>
        {
            shell.BorderBrush = Theme.Border; shell.BorderThickness = new Thickness(1);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { Add(files); Transient = false; }
        };
        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => Native.MakeToolWindow(this);
        LocationChanged += (_, _) => FloatManager.Save();
        UpdateHeader();
    }

    static DataTemplate TileTemplate()
    {
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(WidthProperty, 78.0);
        root.SetValue(MarginProperty, new Thickness(2));
        var img = new FrameworkElementFactory(typeof(Image));
        img.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(Tile.Thumb)));
        img.SetValue(WidthProperty, 56.0); img.SetValue(HeightProperty, 56.0);
        img.SetValue(Image.StretchProperty, Stretch.Uniform);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Tile.Name)));
        text.SetValue(TextBlock.FontSizeProperty, 10.0);
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        text.SetValue(TextBlock.MaxHeightProperty, 28.0);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(TextBlock.ForegroundProperty, Theme.Text);
        root.AppendChild(img);
        root.AppendChild(text);
        return new DataTemplate { VisualTree = root };
    }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (tiles.Any(t => string.Equals(t.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            var thumb = Formats.CategoryOf(p) == FileCategory.Image ? Ui.LoadBitmap(p, 112) ?? Native.Thumbnail(p, 112) : Native.Thumbnail(p, 112);
            tiles.Add(new Tile { Path = p, Thumb = thumb });
        }
        UpdateHeader();
        FloatManager.Save();
    }

    public void Remove(IEnumerable<string> paths)
    {
        var set = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tiles.Where(t => set.Contains(t.Path)).ToList()) tiles.Remove(t);
        UpdateHeader();
        FloatManager.Save();
    }

    List<string> Selected => list.SelectedItems.Cast<Tile>().Select(t => t.Path).ToList();
    List<string> SelectedOrAll => Selected.Count > 0 ? Selected : tiles.Select(t => t.Path).ToList();

    void UpdateHeader()
    {
        header.Text = Count == 0 ? "Float" : $"{Count} item{(Count == 1 ? "" : "s")}";
        sub.Text = list.SelectedItems.Count > 0 ? $"{list.SelectedItems.Count} selected" : "";
        sub.Visibility = sub.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        emptyState.Visibility = Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ToggleCollapse()
    {
        collapsed = !collapsed;
        if (collapsed) { Tag = Height; Height = 66; ResizeMode = ResizeMode.NoResize; }
        else { Height = Tag is double h ? h : 320; ResizeMode = ResizeMode.CanResizeWithGrip; }
    }

    void OnTileDrag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragStart is not { } start) return;
        var d = e.GetPosition(this) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (Selected.Count == 0) return;
        dragStart = null;
        DragOut(Selected);
    }

    void DragOut(List<string> paths)
    {
        var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
        var result = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        if (result != DragDropEffects.None && Settings.Current.RemoveAfterDragOut) Remove(paths);
        else if (result == DragDropEffects.Move) Remove(paths.Where(p => !File.Exists(p) && !Directory.Exists(p)));
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: PreviewWindow.Show(SelectedOrAll); e.Handled = true; break;
            case Key.Delete or Key.Back: Remove(Selected); e.Handled = true; break;
            case Key.Escape: list.UnselectAll(); e.Handled = true; break;
            case Key.A when Keyboard.Modifiers == ModifierKeys.Control: list.SelectAll(); e.Handled = true; break;
        }
    }

    ContextMenu BuildMenu(List<string> paths, bool includeManage = false)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action a, string? glyph = null)
        {
            var mi = new MenuItem { Header = header };
            if (glyph != null) mi.Icon = Ui.Glyph(glyph, 14);
            mi.Click += (_, _) => a();
            return mi;
        }
        if (paths.Count > 0)
        {
            menu.Items.Add(Item("Open", () => paths.ForEach(Explorer.Open), ""));
            menu.Items.Add(Item("Preview", () => PreviewWindow.Show(paths), ""));
            menu.Items.Add(Item("Show in Explorer", () => Explorer.Reveal(paths), ""));
            menu.Items.Add(new Separator());
            foreach (var (kind, label) in new[] { (WheelKind.Conversions, "Convert to"), (WheelKind.Tools, "Tools"), (WheelKind.Actions, "Actions") })
            {
                var sub = new MenuItem { Header = label };
                foreach (var it in Actions.Items(kind, paths).Where(i => i.Id != nameof(FileAction.Float)))
                    sub.Items.Add(Item(it.Title, () => it.Run(paths), it.Glyph));
                if (sub.Items.Count > 0) menu.Items.Add(sub);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Remove from Float", () => Remove(paths), ""));
        }
        if (includeManage)
        {
            menu.Items.Add(Item("Select All", () => list.SelectAll()));
            menu.Items.Add(Item("Clear Float", () => Remove(tiles.Select(t => t.Path).ToList())));
        }
        return menu;
    }
}

/// Lightweight preview (Space in a Float, Preview action): Explorer's large thumbnail plus details.
sealed class PreviewWindow : Window
{
    readonly IReadOnlyList<string> paths;
    int index;
    readonly Image image = new() { Stretch = Stretch.Uniform };
    readonly TextBlock name = Ui.Text("", 14, FontWeights.SemiBold);
    readonly TextBlock info = Ui.Caption("", false);

    public static void Show(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var w = new PreviewWindow(paths);
        w.Show();
        w.Activate();
    }

    PreviewWindow(IReadOnlyList<string> paths)
    {
        this.paths = paths;
        Width = 720; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Surface;
        var footer = new DockPanel { Margin = new Thickness(16, 10, 16, 14) };
        var buttons = Ui.Row(8, Ui.Button("Open", () => Explorer.Open(paths[index]), true), Ui.Button("Show in Explorer", () => Explorer.Reveal([paths[index]])));
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(Ui.Column(2, name, info));
        var dock = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(new Border { Child = image, Margin = new Thickness(16, 16, 16, 0) });
        Content = dock;
        this.OnKey(Key.Escape, Close);
        this.OnKey(Key.Space, Close);
        this.OnKey(Key.Right, () => Go(1));
        this.OnKey(Key.Left, () => Go(-1));
        SourceInitialized += (_, _) => Native.ApplyModernChrome(this, Theme.Dark);
        Go(0);
    }

    void Go(int delta)
    {
        index = (index + delta + paths.Count) % paths.Count;
        var p = paths[index];
        Title = $"{Path.GetFileName(p)}{(paths.Count > 1 ? $"  ({index + 1}/{paths.Count})" : "")}";
        name.Text = Path.GetFileName(p);
        var cat = Formats.CategoryOf(p);
        image.Source = cat is FileCategory.Image ? Ui.LoadBitmap(p, 1400) ?? Native.Thumbnail(p, 512) : Native.Thumbnail(p, 512);
        info.Text = $"{cat.Title()} · {Output.FormatBytes(Output.SizeOf(p))} · {File.GetLastWriteTime(p):g}";
    }
}
