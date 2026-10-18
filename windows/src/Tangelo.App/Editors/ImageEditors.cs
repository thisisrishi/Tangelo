using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ImageMagick;
using Tangelo.Core;

namespace Tangelo.App.Editors;

static class Preview
{
    public static MagickImage Load(string path, uint max = 1000)
    {
        var img = Images.Load(path);
        if (Math.Max(img.Width, img.Height) > max) img.Resize(new MagickGeometry(max, max));
        return img;
    }
}

sealed class AdjustEditor : EditorWindow
{
    ImageAdjustments adj = new();
    MagickImage? source;
    readonly ImageCanvas canvas = new();
    readonly Action refresh;

    public AdjustEditor(IReadOnlyList<string> paths) : base(FileTool.EditImage, paths, paths.Count > 1 ? $"Save {paths.Count} Copies" : "Save Edited Copy")
    {
        refresh = Debounce.Create(() => source == null ? null : Ui.ToBitmap(Images.Adjust(source, adj)), b => canvas.SetImage(b));
        var sliders = Ui.Column(2,
            S("Exposure", -2, 2, 0, v => adj = adj with { Exposure = v }),
            S("Contrast", 0.5, 1.5, 1, v => adj = adj with { Contrast = v }),
            S("Saturation", 0, 2, 1, v => adj = adj with { Saturation = v }),
            S("Warmth", -1, 1, 0, v => adj = adj with { Warmth = v }),
            S("Tint", -1, 1, 0, v => adj = adj with { Tint = v }),
            S("Highlights", 0, 1, 1, v => adj = adj with { Highlights = v }),
            S("Shadows", -1, 1, 0, v => adj = adj with { Shadows = v }),
            S("Sharpen", 0, 2, 0, v => adj = adj with { Sharpness = v }),
            S("Noise reduction", 0, 1, 0, v => adj = adj with { NoiseReduction = v }),
            S("Vignette", 0, 1, 0, v => adj = adj with { Vignette = v }),
            S("Grain", 0, 1, 0, v => adj = adj with { Grain = v }),
            Ui.Toggle("Black & white", false, v => { adj = adj with { BlackAndWhite = v }; refresh(); }));
        var side = Ui.Scroll(sliders); side.Width = 300;
        var grid = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        grid.Children.Add(side);
        grid.Children.Add(canvas);
        SetContent(grid);
        SetOptions(Ui.Caption(paths.Count > 1 ? $"Applies to all {paths.Count} images." : "Press and hold the preview to compare.", false));
        canvas.PreviewMouseLeftButtonDown += (_, _) => { if (source != null) canvas.SetImage(Ui.ToBitmap(source)); };
        canvas.PreviewMouseLeftButtonUp += (_, _) => refresh();
        Task.Run(() => source = Preview.Load(paths[0])).ContinueWith(_ => Dispatcher.Invoke(refresh));
    }

    FrameworkElement S(string label, double min, double max, double value, Action<double> set) =>
        Ui.Slider(label, min, max, value, v => { set(v); refresh(); }, v => v.ToString("0.00"));

    protected override bool Apply()
    {
        var a = adj;
        Jobs.Batch(Tool, Paths, p => { using var img = Images.Load(p); using var r = Images.Adjust(img, a); return Images.SaveLike(r, p, "edited"); });
        return true;
    }
}

sealed class AnnotateEditor : EditorWindow
{
    readonly List<Annotation> annotations = [];
    readonly ImageCanvas canvas = new();
    readonly Canvas surface = new() { Background = Brushes.Transparent };
    MagickImage? source;
    AnnotationKind kind = AnnotationKind.Arrow;
    string color = "#FF3B30";
    double width = 0.006, zoom = 2;
    string text = "Text";
    Annotation? drawing;
    int? selected;
    (Point Start, IReadOnlyList<(double X, double Y)> Origin)? moving;
    readonly Action refresh;

    public AnnotateEditor(IReadOnlyList<string> paths) : base(FileTool.AnnotateImage, paths, "Save Annotated Copy")
    {
        refresh = Debounce.Create(() =>
        {
            if (source == null) return null;
            var all = drawing != null ? [.. annotations, drawing] : annotations.ToList();
            using var r = Images.Annotate(source, all);
            return Ui.ToBitmap(r);
        }, b => canvas.SetImage(b), 30);

        var kinds = Ui.Segmented(Enum.GetValues<AnnotationKind>(), kind, k => k.ToString(), k => kind = k);
        var swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Ui.ParseColor(color)), Cursor = Cursors.Hand, ToolTip = "Annotation color" };
        swatch.MouseLeftButtonUp += (_, _) => { if (Ui.PickColor(color) is { } c) { color = c; swatch.Background = new SolidColorBrush(Ui.ParseColor(c)); } };
        var widthSlider = new Slider { Minimum = 0.002, Maximum = 0.02, Value = width, Width = 110, ToolTip = "Line width" };
        widthSlider.ValueChanged += (_, e) => width = e.NewValue;
        var textField = Ui.Field(text, v => text = v, 120);
        var zoomCombo = Ui.Combo([2.0, 3.0, 4.0], zoom, z => $"{z:0}×", z => zoom = z);
        var toolbar = Ui.Row(8, kinds, swatch, widthSlider, Ui.Caption("Text", false), textField, zoomCombo,
            Ui.IconButton("", () => { if (annotations.Count > 0) { annotations.RemoveAt(annotations.Count - 1); refresh(); } }, "Undo last annotation"),
            Ui.IconButton("", DeleteSelected, "Delete selected annotation"));
        toolbar.Margin = new Thickness(0, 0, 0, 10);

        canvas.SetOverlay(surface);
        surface.MouseLeftButtonDown += OnDown;
        surface.MouseMove += OnMove;
        surface.MouseLeftButtonUp += OnUp;
        var dock = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);
        dock.Children.Add(canvas);
        SetContent(dock);
        SetOptions(Ui.Caption("Drag to draw; drag an annotation to move it; Delete removes the selected one.", false));
        this.OnKey(Key.Delete, DeleteSelected);
        this.OnKey(Key.Z, () => { if (annotations.Count > 0) { annotations.RemoveAt(annotations.Count - 1); refresh(); } }, ModifierKeys.Control);
        CanApply = false;
        Task.Run(() => source = Preview.Load(paths[0], 1400)).ContinueWith(_ => Dispatcher.Invoke(refresh));
    }

    (double X, double Y) Norm(Point p) => (p.X / Math.Max(canvas.PixelWidth, 1), p.Y / Math.Max(canvas.PixelHeight, 1));

    Rect Bounds(Annotation a)
    {
        double W = canvas.PixelWidth, H = canvas.PixelHeight;
        var xs = a.Points.Select(p => p.X * W).ToList(); var ys = a.Points.Select(p => p.Y * H).ToList();
        var r = new Rect(new Point(xs.Min(), ys.Min()), new Point(xs.Max(), ys.Max()));
        if (a.Kind == AnnotationKind.Text) r = new Rect(r.X, r.Y, Math.Max(40, a.Text.Length * a.FontSize * W * 0.6), a.FontSize * W * 1.3);
        if (a.Kind == AnnotationKind.Number) { var rad = a.FontSize * W; r = new Rect(r.X - rad, r.Y - rad, rad * 2, rad * 2); }
        if (a.Kind == AnnotationKind.Magnifier) { var rad = Math.Max(r.Width, r.Height) / 2; r = new Rect(xs[0] - rad, ys[0] - rad, rad * 2, rad * 2); }
        r.Inflate(8, 8);
        return r;
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(surface);
        surface.CaptureMouse();
        for (int i = annotations.Count - 1; i >= 0; i--)
            if (Bounds(annotations[i]).Contains(p)) { selected = i; moving = (p, annotations[i].Points); return; }
        selected = null;
        var n = Norm(p);
        drawing = new Annotation(kind, [n], color, width, text, Zoom: zoom, Number: annotations.Count(a => a.Kind == AnnotationKind.Number) + 1);
        refresh();
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(surface);
        if (moving is { } m && selected is { } i)
        {
            double dx = (p.X - m.Start.X) / canvas.PixelWidth, dy = (p.Y - m.Start.Y) / canvas.PixelHeight;
            annotations[i] = annotations[i] with { Points = m.Origin.Select(o => (o.X + dx, o.Y + dy)).ToList() };
            refresh();
        }
        else if (drawing != null)
        {
            var n = Norm(p);
            drawing = drawing.Kind switch
            {
                AnnotationKind.Pen => drawing with { Points = [.. drawing.Points, n] },
                AnnotationKind.Text or AnnotationKind.Number => drawing with { Points = [n] },
                _ => drawing with { Points = [drawing.Points[0], n] },
            };
            refresh();
        }
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        surface.ReleaseMouseCapture();
        if (drawing is { } d)
        {
            var single = d.Kind is AnnotationKind.Text or AnnotationKind.Number;
            var big = d.Points.Count > 1 && Math.Abs(d.Points[^1].X - d.Points[0].X) + Math.Abs(d.Points[^1].Y - d.Points[0].Y) > 0.008;
            if (single || big) { annotations.Add(d); selected = annotations.Count - 1; }
        }
        drawing = null; moving = null;
        CanApply = annotations.Count > 0;
        refresh();
    }

    void DeleteSelected()
    {
        if (selected is { } i && i < annotations.Count) { annotations.RemoveAt(i); selected = null; CanApply = annotations.Count > 0; refresh(); }
    }

    protected override bool Apply()
    {
        var list = annotations.ToList();
        Jobs.Batch(Tool, Paths, p => { using var img = Images.Load(p); using var r = Images.Annotate(img, list); return Images.SaveLike(r, p, "annotated"); });
        return true;
    }
}

sealed class FrameEditor : EditorWindow
{
    FrameOptions opts = new();
    MagickImage? source;
    readonly ImageCanvas canvas = new();
    readonly Action refresh;

    public FrameEditor(IReadOnlyList<string> paths) : base(FileTool.FrameImage, paths, "Save with Background")
    {
        refresh = Debounce.Create(() => { if (source == null) return null; using var r = Images.Frame(source, opts); return Ui.ToBitmap(r); }, b => canvas.SetImage(b));
        var swatches = new WrapPanel { Width = 260 };
        foreach (var p in GradientPreset.All)
        {
            var b = new Border
            {
                Width = 52, Height = 40, Margin = new Thickness(3), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, ToolTip = p.Name,
                Background = new LinearGradientBrush(Ui.ParseColor(p.From), Ui.ParseColor(p.To), 45),
            };
            b.MouseLeftButtonUp += (_, _) => { opts = opts with { Preset = p, SolidColor = null, BackgroundImage = null, Transparent = false }; refresh(); };
            swatches.Children.Add(b);
        }
        var side = Ui.Column(10,
            Ui.Text("Background", 13, FontWeights.SemiBold), swatches,
            Ui.Row(8,
                Ui.Button("Solid colour…", () => { if (Ui.PickColor(opts.SolidColor ?? "#FFFFFF") is { } c) { opts = opts with { SolidColor = c, BackgroundImage = null, Transparent = false }; refresh(); } }),
                Ui.Button("Image…", () => { if (Ui.PickFile("Images|*.png;*.jpg;*.jpeg;*.webp;*.heic", "Choose a background image") is { } f) { opts = opts with { BackgroundImage = f, Transparent = false }; refresh(); } })),
            Ui.Toggle("Transparent", false, v => { opts = opts with { Transparent = v }; refresh(); }),
            Ui.Labeled("Canvas", Ui.Combo(Enum.GetValues<CanvasRatio>(), CanvasRatio.Auto, r => r switch
            {
                CanvasRatio.Square => "1:1", CanvasRatio.Wide => "16:9", CanvasRatio.Classic => "4:3", CanvasRatio.Portrait => "4:5", CanvasRatio.Story => "9:16", _ => "Auto",
            }, r => { opts = opts with { Ratio = r }; refresh(); }), 70),
            Ui.Slider("Padding", 0, 0.3, opts.Padding, v => { opts = opts with { Padding = v }; refresh(); }, v => $"{v * 100:0}%"),
            Ui.Slider("Corners", 0, 0.08, opts.CornerRadius, v => { opts = opts with { CornerRadius = v }; refresh(); }, v => $"{v * 100:0.#}%"),
            Ui.Slider("Shadow", 0, 1, opts.Shadow, v => { opts = opts with { Shadow = v }; refresh(); }, v => $"{v * 100:0}%"));
        var scroll = Ui.Scroll(side); scroll.Width = 290;
        var dock = new DockPanel();
        DockPanel.SetDock(scroll, Dock.Right);
        dock.Children.Add(scroll);
        dock.Children.Add(canvas);
        SetContent(dock);
        if (paths.Count > 1) SetOptions(Ui.Caption($"Applies to all {paths.Count} images.", false));
        Task.Run(() => source = Preview.Load(paths[0], 900)).ContinueWith(_ => Dispatcher.Invoke(refresh));
    }

    protected override bool Apply()
    {
        var o = opts;
        Jobs.Batch(Tool, Paths, p => { using var img = Images.Load(p); using var r = Images.Frame(img, o); return Images.SaveLike(r, p, "framed", forcePng: true); });
        return true;
    }
}

sealed class CropImageEditor : EditorWindow
{
    readonly ImageCanvas canvas = new();
    readonly RectOverlay overlay = new() { Single = true, DimOutside = true };
    readonly TextBlock size = Ui.Caption("", false);
    MagickImage? source;
    static readonly (string Name, double? Ratio)[] Ratios = [("Free", null), ("1:1", 1), ("4:3", 4.0 / 3), ("3:2", 1.5), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16), ("4:5", 0.8)];

    public CropImageEditor(IReadOnlyList<string> paths) : base(FileTool.CropImage, paths, paths.Count > 1 ? $"Crop {paths.Count} Images" : "Save Cropped Copy")
    {
        overlay.Rects.Add(new Redaction(new RectF(0.1, 0.1, 0.8, 0.8)));
        overlay.Changed += Update;
        canvas.SetOverlay(overlay);
        var ratios = Ui.Segmented(Ratios, Ratios[0], r => r.Name, r => SetRatio(r.Ratio));
        ratios.Margin = new Thickness(0, 0, 0, 10);
        var dock = new DockPanel();
        DockPanel.SetDock(ratios, Dock.Top);
        dock.Children.Add(ratios);
        dock.Children.Add(canvas);
        SetContent(dock);
        SetOptions(size, Ui.Caption(paths.Count > 1 ? "Uses the same share of each image." : "", false));
        Task.Run(() => source = Preview.Load(paths[0], 1600)).ContinueWith(_ => Dispatcher.Invoke(() => { canvas.SetImage(Ui.ToBitmap(source!)); overlay.Redraw(); Update(); }));
    }

    void SetRatio(double? r)
    {
        overlay.Aspect = r;
        if (r is not { } ratio || source == null) return;
        double imgAspect = (double)source.Width / source.Height, w = 0.9, h = w * imgAspect / ratio;
        if (h > 0.9) { h = 0.9; w = h * ratio / imgAspect; }
        overlay.Rects.Clear();
        overlay.Rects.Add(new Redaction(new RectF((1 - w) / 2, (1 - h) / 2, w, h)));
        overlay.Redraw();
        Update();
    }

    void Update()
    {
        CanApply = overlay.Rects.Count > 0;
        if (overlay.Rects.Count > 0 && Paths.Count == 1)
        {
            using var info = new MagickImage();
            info.Ping(Paths[0]);
            var r = overlay.Rects[0].Rect;
            size.Text = $"{(int)(r.W * info.Width)} × {(int)(r.H * info.Height)} px";
        }
    }

    protected override bool Apply()
    {
        var r = overlay.Rects[0].Rect;
        Jobs.Batch(Tool, Paths, p => { using var img = Images.Load(p); using var c = Images.Crop(img, r); return Images.SaveLike(c, p, "cropped"); });
        return true;
    }
}

sealed class ResizeEditor : EditorWindow
{
    enum Mode { Percent, LongestSide, Width, Height, Exact }
    Mode mode = Mode.Percent;
    double percent = 50, pixels = 1920, exactW = 1920, exactH = 1080;
    ConversionFormat? format;
    readonly ContentControl modeControls = new();

    public ResizeEditor(IReadOnlyList<string> paths) : base(FileTool.ResizeImage, paths, "Resize")
    {
        var formats = new ConversionFormat?[] { null, ConversionFormat.Jpg, ConversionFormat.Png, ConversionFormat.Webp, ConversionFormat.Avif };
        var files = Ui.Column(2, paths.Select(p => (UIElement)Ui.Caption(FileName(p), false)).ToArray());
        SetContent(Ui.Column(12,
            Ui.Segmented(Enum.GetValues<Mode>(), mode, m => m switch { Mode.LongestSide => "Longest side", _ => m.ToString() }, m => { mode = m; Render(); }),
            modeControls,
            Ui.Labeled("Format", Ui.Combo(formats, null, f => f?.Label() ?? "Keep original", f => format = f)),
            Ui.Text("Files", 13, FontWeights.SemiBold), files));
        Render();
    }

    void Render() => modeControls.Content = mode switch
    {
        Mode.Percent => Ui.Slider("Scale", 5, 200, percent, v => percent = v, v => $"{v:0}%", 5),
        Mode.Exact => Ui.Row(8, Ui.Field(exactW.ToString(), v => double.TryParse(v, out exactW), 90), Ui.Text("×"), Ui.Field(exactH.ToString(), v => double.TryParse(v, out exactH), 90), Ui.Caption("px", false)),
        _ => Ui.Row(8, Ui.Field(pixels.ToString(), v => double.TryParse(v, out pixels), 90), Ui.Caption("px", false)),
    };

    protected override bool Apply()
    {
        var (m, pc, px, w, h, fmt) = (mode, percent, pixels, exactW, exactH, format);
        Jobs.Batch(Tool, Paths, p =>
        {
            using var img = Images.Load(p);
            double iw = img.Width, ih = img.Height;
            (double W, double H) size = m switch
            {
                Mode.Percent => (iw * pc / 100, ih * pc / 100),
                Mode.LongestSide => (iw * px / Math.Max(iw, ih), ih * px / Math.Max(iw, ih)),
                Mode.Width => (px, ih * px / iw),
                Mode.Height => (iw * px / ih, px),
                _ => (w, h),
            };
            using var r = Images.Resize(img, (uint)Math.Max(1, size.W), (uint)Math.Max(1, size.H));
            var f = fmt ?? Formats.From(p) ?? ConversionFormat.Png;
            if (f is ConversionFormat.Heic or ConversionFormat.Svg) f = ConversionFormat.Png;
            var output = Output.PathFor(p, f.Extension(), $"{r.Width}×{r.Height}");
            Images.Save(r, output, f, 90);
            return output;
        });
        return true;
    }
}

sealed class RedactImageEditor : EditorWindow
{
    readonly ImageCanvas canvas = new();
    readonly RectOverlay overlay = new();
    RedactionStyle style = RedactionStyle.Blur;
    MagickImage? source;
    readonly Action refresh;

    public RedactImageEditor(IReadOnlyList<string> paths) : base(FileTool.RedactImage, paths, "Save Redacted Copy")
    {
        refresh = Debounce.Create(() => source == null ? null : Ui.ToBitmap(Images.Redact(source, overlay.Rects.Select(r => r.Rect), style)), b => canvas.SetImage(b), 60);
        overlay.Changed += () => { CanApply = overlay.Rects.Count > 0; refresh(); };
        canvas.SetOverlay(overlay);
        var bar = Ui.Row(8,
            Ui.Segmented(Enum.GetValues<RedactionStyle>(), style, s => s.ToString(), s => { style = s; refresh(); }),
            Ui.Button("Detect Faces", () => Detect(Core.Platform.Faces, "No faces were detected.")),
            Ui.Button("Detect Text", () => Detect(Core.Platform.TextRegions, "No text was detected.")),
            Ui.IconButton("", overlay.DeleteSelected, "Delete redaction"));
        bar.Margin = new Thickness(0, 0, 0, 10);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(canvas);
        SetContent(dock);
        SetOptions(Ui.Caption("Blur softens details. Use Solid for sensitive information. Redacted copies never keep metadata.", false));
        this.OnKey(Key.Delete, overlay.DeleteSelected);
        CanApply = false;
        Task.Run(() => source = Preview.Load(paths[0], 1400)).ContinueWith(_ => Dispatcher.Invoke(() => { canvas.SetImage(Ui.ToBitmap(source!)); overlay.Redraw(); }));
    }

    async void Detect(Func<MagickImage, Task<IReadOnlyList<RectF>>>? detector, string none)
    {
        if (source == null) return;
        if (detector == null) { App.Alert("Detection unavailable", "Windows face and text detection aren't available on this PC."); return; }
        var found = await detector(source);
        if (found.Count == 0) { App.Alert("Nothing found", none); return; }
        overlay.Rects.AddRange(found.Select(r => new Redaction(r)));
        overlay.Redraw();
        CanApply = true;
        refresh();
    }

    protected override bool Apply()
    {
        var rects = overlay.Rects.Select(r => r.Rect).ToList();
        var s = style;
        Jobs.Batch(Tool, Paths, p => { using var img = Images.Load(p); using var r = Images.Redact(img, rects, s); r.Strip(); return Images.SaveLike(r, p, "redacted"); });
        return true;
    }
}

sealed class CollageEditor : EditorWindow
{
    CollageOptions opts = new();
    List<string> order;
    List<MagickImage> thumbs = [];
    readonly ImageCanvas canvas = new();
    readonly Action refresh;

    public CollageEditor(IReadOnlyList<string> paths) : base(FileTool.CreateCollage, paths, "Create Collage")
    {
        order = paths.ToList();
        refresh = Debounce.Create(() =>
        {
            if (thumbs.Count == 0) return null;
            var o = opts with { CellSize = 300, Spacing = opts.Spacing * 300 / 800, Padding = opts.Padding * 300 / 800, CornerRadius = opts.CornerRadius * 300 / 800 };
            using var r = Images.Collage(thumbs, o);
            return Ui.ToBitmap(r);
        }, b => canvas.SetImage(b));
        var side = Ui.Column(10,
            Ui.Labeled("Layout", Ui.Combo(Enum.GetValues<CollageStyle>(), opts.Style, s => s.ToString(), s => { opts = opts with { Style = s }; refresh(); }), 80),
            Ui.Toggle("Fill cells (crop edges)", true, v => { opts = opts with { Fill = v }; refresh(); }),
            Ui.Slider("Spacing", 0, 60, opts.Spacing, v => { opts = opts with { Spacing = (int)v }; refresh(); }, v => $"{v:0}"),
            Ui.Slider("Padding", 0, 120, opts.Padding, v => { opts = opts with { Padding = (int)v }; refresh(); }, v => $"{v:0}"),
            Ui.Slider("Corners", 0, 80, opts.CornerRadius, v => { opts = opts with { CornerRadius = (int)v }; refresh(); }, v => $"{v:0}"),
            Ui.Row(8,
                Ui.Button("Background…", () => { if (Ui.PickColor(opts.Background) is { } c) { opts = opts with { Background = c }; refresh(); } }),
                Ui.Button("Shuffle", () => Reorder(order.OrderBy(_ => Random.Shared.Next()).ToList())),
                Ui.Button("Reverse", () => Reorder(Enumerable.Reverse(order).ToList()))));
        side.Width = 300;
        var dock = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        dock.Children.Add(side);
        dock.Children.Add(canvas);
        SetContent(dock);
        SetOptions(Ui.Caption($"{paths.Count} images", false));
        Reorder(order);
    }

    void Reorder(List<string> next)
    {
        order = next;
        Task.Run(() => order.Select(p => Preview.Load(p, 600)).ToList()).ContinueWith(t => Dispatcher.Invoke(() => { thumbs.ForEach(i => i.Dispose()); thumbs = t.Result; refresh(); }));
    }

    protected override bool Apply()
    {
        var (o, list) = (opts, order.ToList());
        Jobs.Run("Creating collage", list, _ =>
        {
            var images = list.Select(p => { var i = Images.Load(p); if (Math.Max(i.Width, i.Height) > 2000) i.Resize(new MagickGeometry(2000, 2000)); return i; }).ToList();
            try
            {
                using var r = Images.Collage(images, o);
                var output = Output.Unique(Output.DirectoryFor(list[0]), "Collage", "png");
                r.Write(output, MagickFormat.Png);
                return Task.FromResult<IEnumerable<string>>([output]);
            }
            finally { images.ForEach(i => i.Dispose()); }
        });
        return true;
    }
}
