using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Tangelo.Core;

namespace Tangelo.App;

/// The radial wheel shown under the cursor mid-drag. Petals are filled when the drag enters,
/// because Windows only reveals another app's dragged files to a drop target.
sealed class WheelWindow : Window
{
    const double Size = 470, Inner = 62, Outer = 210;
    static WheelWindow? instance;

    readonly Canvas canvas = new() { Width = Size, Height = Size };
    readonly ScaleTransform scale = new(1, 1, Size / 2, Size / 2);
    readonly TextBlock centerGlyph = Ui.Glyph("", 22, Theme.Subtle);
    readonly TextBlock centerTitle = Ui.Text("", 12, FontWeights.SemiBold);
    readonly TextBlock centerKind = Ui.Text("", 10, brush: Theme.Subtle);
    readonly List<(Path Petal, FrameworkElement Label)> petals = [];
    readonly DispatcherTimer guard = new() { Interval = TimeSpan.FromMilliseconds(1400) };

    Border? center;
    string summary = "";
    WheelKind kind;

    /// Built once: rebuilding would re-parent the label TextBlocks.
    Border MakeCenter()
    {
        var b = new Border
        {
            Width = Inner * 2 - 8, Height = Inner * 2 - 8, CornerRadius = new CornerRadius(Inner),
            Background = Theme.Surface, BorderBrush = Theme.Border, BorderThickness = new Thickness(1),
            Child = Ui.Column(2, centerGlyph, centerTitle, centerKind),
        };
        ((StackPanel)b.Child).VerticalAlignment = VerticalAlignment.Center;
        foreach (var t in new[] { centerTitle, centerKind }) { t.HorizontalAlignment = HorizontalAlignment.Center; t.MaxWidth = Inner * 2 - 16; }
        Canvas.SetLeft(b, Size / 2 - Inner + 4); Canvas.SetTop(b, Size / 2 - Inner + 4);
        return b;
    }
    List<Actions.Item> items = [];
    List<string> files = [];
    int? highlighted;
    bool entered;

    public static bool IsShowing => instance?.IsVisible == true;

    WheelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = Height = Size;
        AllowDrop = true;
        canvas.RenderTransform = scale;
        Content = canvas;
        SourceInitialized += (_, _) => Native.MakeNonActivating(this);
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) => Highlight(null);
        Drop += OnDrop;
        // Hide if no file drag reaches us soon after appearing (e.g. a text-selection drag).
        guard.Tick += (_, _) => { guard.Stop(); if (!entered) Hide(); };
    }

    public static void ShowAtCursor(WheelKind kind)
    {
        instance ??= new WheelWindow();
        instance.Present(kind, Native.CursorDips());
    }

    /// For demos/screenshots: show with known files.
    public static WheelWindow ShowWith(WheelKind kind, IReadOnlyList<string> paths, Point at)
    {
        instance ??= new WheelWindow();
        instance.Present(kind, at);
        instance.Load(paths.ToList());
        instance.entered = true;
        return instance;
    }

    public static void DragEnded()
    {
        if (!IsShowing) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        t.Tick += (_, _) => { t.Stop(); instance?.Hide(); };
        t.Start();
    }

    void Present(WheelKind k, Point at)
    {
        kind = k;
        entered = false;
        files = [];
        items = [];
        Build();
        centerGlyph.Text = "";
        centerTitle.Text = summary = "Move to load";
        centerKind.Text = KindTitle;
        Left = at.X - Size / 2;
        Top = at.Y - Size / 2;
        Show();
        scale.ScaleX = scale.ScaleY = 0.7;
        var anim = new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        guard.Start();
    }

    string KindTitle => kind switch { WheelKind.Conversions => "Convert", WheelKind.Tools => "Tools", _ => "Actions" };

    void Load(List<string> paths)
    {
        files = paths;
        items = Actions.Items(kind, paths);
        if (items.Count > 12)
        {
            var all = items;
            items = [.. all.Take(11), new Actions.Item("more", "More…", "", p => ChooserWindow.Show(KindTitle, all, p))];
        }
        var cats = paths.Select(Core.Formats.CategoryOf).Distinct().ToList();
        centerGlyph.Text = cats.Count == 1 ? cats[0].Glyph() : "";
        centerTitle.Text = summary = items.Count == 0 ? "No options" : paths.Count == 1 ? System.IO.Path.GetExtension(paths[0]).TrimStart('.').ToUpperInvariant() : $"{paths.Count} files";
        Build();
    }

    void Build()
    {
        canvas.Children.Clear();
        petals.Clear();
        var disc = new Ellipse
        {
            Width = Outer * 2 + 10, Height = Outer * 2 + 10,
            Fill = Theme.SurfaceTranslucent, Stroke = Theme.Border, StrokeThickness = 1,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.3 },
        };
        Canvas.SetLeft(disc, Size / 2 - Outer - 5); Canvas.SetTop(disc, Size / 2 - Outer - 5);
        canvas.Children.Add(disc);
        int n = Math.Max(items.Count, 1);
        for (int i = 0; i < items.Count; i++)
        {
            var petal = new Path { Data = Wedge(i, n, Inner + 4, Outer), Fill = Theme.Card, Stroke = Theme.Border, StrokeThickness = 1 };
            canvas.Children.Add(petal);
            var mid = i * 2 * Math.PI / n;
            var r = (Inner + Outer) / 2 + 6;
            var label = Ui.Column(3, Ui.Glyph(items[i].Glyph, 18), Ui.Text(items[i].Title, 11, FontWeights.SemiBold));
            label.Width = 80;
            foreach (var c in label.Children.OfType<TextBlock>()) c.TextAlignment = TextAlignment.Center;
            ((TextBlock)label.Children[1]).HorizontalAlignment = HorizontalAlignment.Center;
            label.Measure(new Size(80, 80));
            Canvas.SetLeft(label, Size / 2 + r * Math.Sin(mid) - 40);
            Canvas.SetTop(label, Size / 2 - r * Math.Cos(mid) - label.DesiredSize.Height / 2);
            canvas.Children.Add(label);
            petals.Add((petal, label));
        }
        center ??= MakeCenter();
        canvas.Children.Add(center);
        Highlight(highlighted);
    }

    static Geometry Wedge(int index, int count, double inner, double outer)
    {
        double seg = 2 * Math.PI / count, gap = count > 1 ? 0.035 : 0;
        double a0 = index * seg - seg / 2 + gap, a1 = index * seg + seg / 2 - gap;
        Point P(double r, double a) => new(Size / 2 + r * Math.Sin(a), Size / 2 - r * Math.Cos(a));
        bool large = a1 - a0 > Math.PI;
        var fig = new PathFigure { StartPoint = P(outer, a0), IsClosed = true };
        fig.Segments.Add(new ArcSegment(P(outer, a1), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(P(inner, a1), true));
        fig.Segments.Add(new ArcSegment(P(inner, a0), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
        var g = new PathGeometry([fig]);
        g.Freeze();
        return g;
    }

    int? IndexAt(Point p)
    {
        double dx = p.X - Size / 2, dy = Size / 2 - p.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (items.Count == 0 || dist < Inner || dist > Outer + 30) return null;
        var angle = Math.Atan2(dx, dy);
        if (angle < 0) angle += 2 * Math.PI;
        var seg = 2 * Math.PI / items.Count;
        return (int)((angle + seg / 2) % (2 * Math.PI) / seg) % items.Count;
    }

    void Highlight(int? i)
    {
        if (highlighted == i && petals.Count > 0 && (i == null || petals[i.Value].Petal.Fill == Theme.Accent)) return;
        highlighted = i;
        for (int k = 0; k < petals.Count; k++)
        {
            var on = k == i;
            petals[k].Petal.Fill = on ? Theme.Accent : Theme.Card;
            petals[k].Petal.Data = Wedge(k, petals.Count, Inner + 4, on ? Outer + 8 : Outer);
            foreach (var t in ((StackPanel)petals[k].Label).Children.OfType<TextBlock>()) t.Foreground = on ? Brushes.White : Theme.Text;
        }
        centerTitle.Text = i is { } idx && idx < items.Count ? items[idx].Title : summary;
    }

    static List<string> FilesFrom(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] f ? f.ToList() : [];

    void OnDragEnter(object sender, DragEventArgs e)
    {
        entered = true;
        var paths = FilesFrom(e);
        if (paths.Count == 0) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (!paths.SequenceEqual(files)) Load(paths);
        OnDragOver(sender, e);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        var i = IndexAt(e.GetPosition(canvas));
        if (i != highlighted) Highlight(i);
        e.Effects = i == null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        var i = IndexAt(e.GetPosition(canvas));
        var paths = FilesFrom(e);
        Hide();
        if (i is { } idx && idx < items.Count && paths.Count > 0)
        {
            var item = items[idx];
            Dispatcher.BeginInvoke(() => item.Run(paths));
        }
        e.Handled = true;
    }
}

/// Full list of options (overflow from the wheel, Explorer menu, tray drop zone).
sealed class ChooserWindow : Window
{
    public static void Show(string title, IReadOnlyList<Actions.Item> items, IReadOnlyList<string> paths)
    {
        var w = new ChooserWindow(title, items, paths);
        w.Show();
        w.Activate();
    }

    ChooserWindow(string title, IReadOnlyList<Actions.Item> items, IReadOnlyList<string> paths)
    {
        Title = paths.Count == 1 ? System.IO.Path.GetFileName(paths[0]) : $"{paths.Count} files";
        Width = 460; Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Surface;
        var grid = new WrapPanel { Margin = new Thickness(16) };
        foreach (var item in items)
        {
            var b = new Button
            {
                Width = 96, Height = 74, Margin = new Thickness(4),
                Content = Ui.Column(4, Ui.Glyph(item.Glyph, 20), Ui.Text(item.Title, 11, FontWeights.SemiBold)),
            };
            b.Click += (_, _) => { Close(); item.Run(paths); };
            grid.Children.Add(b);
        }
        Content = new DockPanel { Children = { WithDock(Ui.Text(title, 16, FontWeights.SemiBold), Dock.Top, new Thickness(20, 16, 20, 0)), Ui.Scroll(grid) } };
        SourceInitialized += (_, _) => Native.ApplyModernChrome(this, Theme.Dark);
        this.OnKey(System.Windows.Input.Key.Escape, Close);
    }

    static UIElement WithDock(FrameworkElement e, Dock d, Thickness margin) { DockPanel.SetDock(e, d); e.Margin = margin; return e; }
}
