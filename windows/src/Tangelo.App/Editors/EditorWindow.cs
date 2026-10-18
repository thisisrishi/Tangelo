using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Tangelo.Core;

namespace Tangelo.App.Editors;

/// Consistent editor chrome: header, content, footer with options and Cancel / primary action.
abstract class EditorWindow : Window
{
    protected readonly IReadOnlyList<string> Paths;
    protected readonly FileTool Tool;
    readonly ContentControl content = new();
    readonly StackPanel options = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly Button primary;

    public static void Open(FileTool tool, IReadOnlyList<string> paths)
    {
        EditorWindow w = tool switch
        {
            FileTool.Compress => new CompressEditor(paths),
            FileTool.Metadata => new MetadataEditor(paths),
            FileTool.BatchRename => new RenameEditor(paths),
            FileTool.EditImage => new AdjustEditor(paths),
            FileTool.AnnotateImage => new AnnotateEditor(paths),
            FileTool.FrameImage => new FrameEditor(paths),
            FileTool.CropImage => new CropImageEditor(paths),
            FileTool.ResizeImage => new ResizeEditor(paths),
            FileTool.RedactImage => new RedactImageEditor(paths),
            FileTool.CreateCollage => new CollageEditor(paths),
            FileTool.TrimVideo or FileTool.TrimAudio => new TrimEditor(tool, paths),
            FileTool.CropVideo => new CropVideoEditor(paths),
            FileTool.ChangeVideoSpeed => new SpeedEditor(paths),
            FileTool.SplitVideo => new SplitVideoEditor(paths),
            FileTool.VideoSnapshots => new SnapshotsEditor(paths),
            FileTool.RedactVideo => new RedactVideoEditor(paths),
            FileTool.VideoToGif => new GifEditor(paths),
            FileTool.NormalizeAudio => new NormalizeEditor(paths),
            FileTool.AudioChannels => new ChannelsEditor(paths),
            FileTool.RedactAudio => new BleepEditor(paths),
            FileTool.AudioToVideo => new VisualizerEditor(paths),
            FileTool.SplitPdf => new SplitPdfEditor(paths),
            FileTool.OrganizePdf => new OrganizePdfEditor(paths),
            _ => throw new TangeloException($"{tool.Title()} has no editor."),
        };
        w.Show();
        w.Activate();
    }

    protected EditorWindow(FileTool tool, IReadOnlyList<string> paths, string primaryTitle)
    {
        Tool = tool;
        Paths = paths;
        Title = $"{tool.Title()} — {(paths.Count == 1 ? System.IO.Path.GetFileName(paths[0]) : $"{paths.Count} files")}";
        Width = 900; Height = 640; MinWidth = 620; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Surface;

        var header = Ui.Row(12, Ui.Glyph(tool.Glyph(), 22, Theme.Accent), Ui.Column(1, Ui.Text(tool.Title(), 16, FontWeights.SemiBold), Ui.Caption(tool.Subtitle(), false)));
        header.Margin = new Thickness(20, 16, 20, 12);
        primary = Ui.Button(primaryTitle, () => { if (Apply()) Close(); }, true);
        primary.IsDefault = true;
        var cancel = Ui.Button("Cancel", Close);
        cancel.IsCancel = true;
        var footer = new DockPanel { Margin = new Thickness(20, 12, 20, 14) };
        var buttons = Ui.Row(8, cancel, primary);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(options);
        var line = new Rectangle { Height = 1, Fill = Theme.Border };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(line, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(line);
        content.Margin = new Thickness(20, 0, 20, 0);
        dock.Children.Add(content);
        Content = dock;
        SourceInitialized += (_, _) => Native.ApplyModernChrome(this, Theme.Dark);
    }

    protected void SetContent(UIElement e) => content.Content = e;
    protected void SetOptions(params UIElement[] items) { options.Children.Clear(); foreach (var i in items) { if (i is FrameworkElement f) f.Margin = new Thickness(0, 0, 14, 0); options.Children.Add(i); } }
    protected bool CanApply { set => primary.IsEnabled = value; }
    protected string PrimaryTitle { set => primary.Content = value; }

    /// Start the job; return true to close the editor.
    protected abstract bool Apply();

    protected static string FileName(string p) => System.IO.Path.GetFileName(p);
}

/// Image + overlay sharing the image's pixel coordinate space (scaled uniformly to fit).
sealed class ImageCanvas : Grid
{
    readonly Image image = new() { Stretch = Stretch.Fill };
    readonly Grid surface = new();
    readonly Viewbox box = new() { Stretch = Stretch.Uniform };

    public ImageCanvas()
    {
        surface.Children.Add(image);
        box.Child = surface;
        Children.Add(box);
    }

    public void SetImage(BitmapSource? bmp)
    {
        image.Source = bmp;
        if (bmp != null) { surface.Width = bmp.PixelWidth; surface.Height = bmp.PixelHeight; }
    }

    public void SetOverlay(FrameworkElement overlay)
    {
        while (surface.Children.Count > 1) surface.Children.RemoveAt(1);
        surface.Children.Add(overlay);
    }

    public double PixelWidth => surface.Width;
    public double PixelHeight => surface.Height;
}

/// Draw, move and resize normalized rectangles over an ImageCanvas surface.
sealed class RectOverlay : Canvas
{
    public readonly List<Redaction> Rects = [];
    public int? Selected;
    public bool Single, DimOutside;
    public double? Aspect; // locked width/height in pixels
    public event Action? Changed;

    Point? drawStart;
    (int Index, Point Start, RectF Origin, bool Resize)? moving;

    public RectOverlay()
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += (_, _) => { drawStart = null; moving = null; ReleaseMouseCapture(); Rects.RemoveAll(r => r.Rect.W < 0.004 || r.Rect.H < 0.004); Redraw(); Changed?.Invoke(); };
        SizeChanged += (_, _) => Redraw();
    }

    double W => ActualWidth > 0 ? ActualWidth : 1;
    double H => ActualHeight > 0 ? ActualHeight : 1;

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        CaptureMouse();
        for (int i = Rects.Count - 1; i >= 0; i--)
        {
            var r = Rects[i].Rect;
            var rect = new Rect(r.X * W, r.Y * H, r.W * W, r.H * H);
            var handle = new Rect(rect.Right - 14, rect.Bottom - 14, 28, 28);
            if (handle.Contains(p) || rect.Contains(p))
            {
                Selected = i;
                moving = (i, p, r, handle.Contains(p));
                Redraw();
                return;
            }
        }
        if (Single) Rects.Clear();
        drawStart = p;
        Rects.Add(new Redaction(new RectF(p.X / W, p.Y / H, 0, 0)));
        Selected = Rects.Count - 1;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (drawStart is { } s && Selected is { } i)
        {
            double x0 = Math.Clamp(Math.Min(s.X, p.X), 0, W), y0 = Math.Clamp(Math.Min(s.Y, p.Y), 0, H);
            double w = Math.Min(Math.Abs(p.X - s.X), W - x0), h = Math.Min(Math.Abs(p.Y - s.Y), H - y0);
            if (Aspect is { } a) h = Math.Min(w / a, H - y0);
            Rects[i] = Rects[i] with { Rect = new RectF(x0 / W, y0 / H, w / W, h / H) };
        }
        else if (moving is { } m)
        {
            var o = m.Origin;
            double dx = (p.X - m.Start.X) / W, dy = (p.Y - m.Start.Y) / H;
            RectF nr;
            if (m.Resize)
            {
                double nw = Math.Clamp(o.W + dx, 0.01, 1 - o.X), nh = Math.Clamp(o.H + dy, 0.01, 1 - o.Y);
                if (Aspect is { } a) nh = Math.Min(nw * W / a / H, 1 - o.Y);
                nr = o with { W = nw, H = nh };
            }
            else nr = o with { X = Math.Clamp(o.X + dx, 0, 1 - o.W), Y = Math.Clamp(o.Y + dy, 0, 1 - o.H) };
            Rects[m.Index] = Rects[m.Index] with { Rect = nr };
        }
        else return;
        Redraw();
        Changed?.Invoke();
    }

    public void DeleteSelected()
    {
        if (Selected is { } i && i < Rects.Count) { Rects.RemoveAt(i); Selected = null; Redraw(); Changed?.Invoke(); }
    }

    public void Redraw()
    {
        Children.Clear();
        var stroke = Math.Max(1.5, W / 400);
        if (DimOutside && Rects.Count > 0)
        {
            var r = Rects[0].Rect;
            var geo = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, W, H)), new RectangleGeometry(new Rect(r.X * W, r.Y * H, r.W * W, r.H * H)));
            Children.Add(new Path { Data = geo, Fill = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)) });
        }
        for (int i = 0; i < Rects.Count; i++)
        {
            var r = Rects[i].Rect;
            var sel = i == Selected;
            var rect = new Rectangle
            {
                Width = r.W * W, Height = r.H * H, Stroke = sel ? Theme.Accent : Brushes.White, StrokeThickness = stroke,
                StrokeDashArray = sel ? null : [4, 3], Fill = DimOutside ? Brushes.Transparent : Theme.AccentSoft,
            };
            SetLeft(rect, r.X * W); SetTop(rect, r.Y * H);
            Children.Add(rect);
            var handle = new Ellipse { Width = stroke * 8, Height = stroke * 8, Fill = Theme.Accent };
            SetLeft(handle, r.Right * W - stroke * 4); SetTop(handle, r.Bottom * H - stroke * 4);
            Children.Add(handle);
        }
    }
}

/// Two-thumb range slider with an optional playhead.
sealed class RangeSlider : Canvas
{
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 1;
    public double Start { get; set; }
    public double End { get; set; } = 1;
    public double? Playhead { get; set; }
    public event Action? Changed;
    public event Action<double>? Scrubbed;
    int dragging = -1;

    public RangeSlider()
    {
        Height = 28;
        Background = Brushes.Transparent;
        SizeChanged += (_, _) => Redraw();
        MouseLeftButtonDown += (_, e) =>
        {
            var x = e.GetPosition(this).X;
            dragging = Math.Abs(x - X(Start)) <= Math.Abs(x - X(End)) ? 0 : 1;
            CaptureMouse();
            Move(x);
        };
        MouseMove += (_, e) => { if (dragging >= 0) Move(e.GetPosition(this).X); };
        MouseLeftButtonUp += (_, _) => { dragging = -1; ReleaseMouseCapture(); };
    }

    double Span => Math.Max(Maximum - Minimum, 1e-6);
    double X(double v) => (v - Minimum) / Span * Math.Max(ActualWidth - 14, 1) + 7;

    void Move(double x)
    {
        var v = Minimum + Math.Clamp((x - 7) / Math.Max(ActualWidth - 14, 1), 0, 1) * Span;
        if (dragging == 0) Start = Math.Min(v, End - 0.05); else End = Math.Max(v, Start + 0.05);
        Redraw();
        Changed?.Invoke();
        Scrubbed?.Invoke(dragging == 0 ? Start : End);
    }

    public void Redraw()
    {
        Children.Clear();
        var track = new Rectangle { Width = Math.Max(ActualWidth - 14, 0), Height = 8, RadiusX = 4, RadiusY = 4, Fill = Theme.Card };
        SetLeft(track, 7); SetTop(track, 10);
        var sel = new Rectangle { Width = Math.Max(X(End) - X(Start), 0), Height = 8, Fill = Theme.AccentSoft };
        SetLeft(sel, X(Start)); SetTop(sel, 10);
        Children.Add(track); Children.Add(sel);
        if (Playhead is { } p)
        {
            var line = new Rectangle { Width = 2, Height = 26, Fill = Theme.Text };
            SetLeft(line, X(Math.Clamp(p, Minimum, Maximum)) - 1); SetTop(line, 1);
            Children.Add(line);
        }
        foreach (var v in new[] { Start, End })
        {
            var thumb = new Rectangle { Width = 14, Height = 28, RadiusX = 4, RadiusY = 4, Fill = Theme.Accent };
            SetLeft(thumb, X(v) - 7);
            Children.Add(thumb);
        }
    }
}

sealed class WaveformView : Canvas
{
    float[] levels = [];
    public List<(double From, double To)> Highlights { get; } = [];

    public WaveformView() { Height = 90; Background = Theme.Card; SizeChanged += (_, _) => Redraw(); }

    public void SetLevels(float[] l) { levels = l; Redraw(); }

    public void Redraw()
    {
        Children.Clear();
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        foreach (var (a, b) in Highlights)
        {
            var r = new Rectangle { Width = Math.Max(0, (b - a) * w), Height = h, Fill = new SolidColorBrush(Color.FromArgb(0x40, 0xE0, 0x30, 0x30)) };
            SetLeft(r, a * w);
            Children.Add(r);
        }
        if (levels.Length == 0) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var bw = w / levels.Length;
            for (int i = 0; i < levels.Length; i++)
            {
                var bh = Math.Max(1, levels[i] * h * 0.95);
                var x = i * bw;
                ctx.BeginFigure(new Point(x, (h - bh) / 2), true, true);
                ctx.LineTo(new Point(x + Math.Max(bw - 0.5, 0.5), (h - bh) / 2), false, false);
                ctx.LineTo(new Point(x + Math.Max(bw - 0.5, 0.5), (h + bh) / 2), false, false);
                ctx.LineTo(new Point(x, (h + bh) / 2), false, false);
            }
        }
        geo.Freeze();
        Children.Add(new Path { Data = geo, Fill = Theme.Accent, Opacity = 0.85 });
    }
}

/// MediaElement with play/pause, position readout and seeking.
sealed class MediaStrip : DockPanel
{
    public readonly MediaElement Player = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
    readonly TextBlock time = Ui.Caption("0:00.00", false);
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    double? stopAt;
    public event Action<double>? Tick;
    public double Duration { get; private set; }
    public double Position => Player.Position.TotalSeconds;

    public MediaStrip(string path, bool showVideo = true)
    {
        Player.Source = new Uri(path);
        Player.MediaOpened += (_, _) =>
        {
            Duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0;
            Player.Pause();
            Tick?.Invoke(0);
        };
        Player.Play();
        Player.Pause();
        var play = Ui.IconButton("", Toggle, "Play / pause (Space)");
        var bar = Ui.Row(6, play, time);
        bar.Margin = new Thickness(0, 6, 0, 0);
        DockPanel.SetDock(bar, Dock.Bottom);
        Children.Add(bar);
        var host = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(8), Child = Player, ClipToBounds = true };
        if (!showVideo) host.Height = 0;
        Children.Add(host);
        timer.Tick += (_, _) =>
        {
            var t = Position;
            time.Text = Fmt(t);
            if (stopAt is { } s && t >= s) { Player.Pause(); stopAt = null; }
            Tick?.Invoke(t);
        };
        timer.Start();
        Unloaded += (_, _) => { timer.Stop(); Player.Close(); };
    }

    public void Toggle()
    {
        if (timer.IsEnabled && Player.CanPause && isPlaying) { Player.Pause(); isPlaying = false; }
        else { Player.Play(); isPlaying = true; }
    }

    bool isPlaying;

    public void Seek(double t) { Player.Position = TimeSpan.FromSeconds(Math.Max(0, t)); time.Text = Fmt(t); }

    public void PlayRange(double from, double to) { Seek(from); stopAt = to; Player.Play(); isPlaying = true; }

    public static string Fmt(double t) => $"{(int)(t / 60)}:{t % 60:00.00}";
}

static class Debounce
{
    /// Runs `work` off the UI thread at most once per interval, delivering only the latest result.
    public static Action Create<T>(Func<T> work, Action<T> done, int ms = 120)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        int version = 0;
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            var v = ++version;
            try
            {
                var result = await Task.Run(work);
                if (v == version) done(result);
            }
            catch (Exception) { }
        };
        return () => { timer.Stop(); timer.Start(); };
    }
}
