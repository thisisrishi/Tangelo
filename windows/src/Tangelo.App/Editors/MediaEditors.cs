using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ImageMagick;
using TextAlignment = System.Windows.TextAlignment;
using Tangelo.Core;

namespace Tangelo.App.Editors;

/// Base for single-file media editors: player, optional waveform, range slider.
abstract class MediaEditor : EditorWindow
{
    protected readonly MediaStrip Strip;
    protected readonly WaveformView Wave = new();
    protected readonly RangeSlider Range = new();
    protected double Duration;
    protected readonly bool IsVideo;

    protected MediaEditor(FileTool tool, IReadOnlyList<string> paths, string primary, bool waveform) : base(tool, paths, primary)
    {
        IsVideo = Formats.CategoryOf(paths[0]) == FileCategory.Video;
        Strip = new MediaStrip(paths[0], IsVideo);
        Strip.Tick += t => { Range.Playhead = t; Range.Redraw(); OnTick(t); };
        Range.Scrubbed += t => Strip.Seek(t);
        _ = LoadInfo();
        if (waveform) _ = Task.Run(() => Media.Envelope(paths[0], 600)).ContinueWith(t => Dispatcher.Invoke(() => { if (!t.IsFaulted) Wave.SetLevels(t.Result); }));
        Wave.Height = IsVideo ? 50 : 140;
        this.OnKey(Key.Space, Strip.Toggle);
    }

    async Task LoadInfo()
    {
        try
        {
            var info = await Media.InfoAsync(Paths[0]);
            Duration = info.Duration;
            Range.Maximum = Duration; Range.Start = 0; Range.End = Duration;
            Range.Redraw();
            OnLoaded(info);
        }
        catch (Exception e) { App.Alert("Could not read the file", e.Message); }
    }

    protected virtual void OnLoaded(MediaInfo info) { }
    protected virtual void OnTick(double t) { }

    protected UIElement Layout(params UIElement[] below)
    {
        var dock = new DockPanel();
        var bottom = Ui.Column(8, below);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        if (IsVideo) dock.Children.Add(Strip);
        else { DockPanel.SetDock(Strip, Dock.Top); dock.Children.Add(Strip); }
        return dock;
    }

    protected void UpdateHighlight(IEnumerable<(double, double)> ranges)
    {
        Wave.Highlights.Clear();
        if (Duration > 0) Wave.Highlights.AddRange(ranges.Select(r => (r.Item1 / Duration, r.Item2 / Duration)));
        Wave.Redraw();
    }

    protected void Run(string title, Func<Task<IEnumerable<string>>> op) => Jobs.Run(title, Paths, _ => op());
}

sealed class TrimEditor : MediaEditor
{
    readonly TextBlock readout = Ui.Caption("", false);
    bool trimSilence;
    double threshold = -45;

    public TrimEditor(FileTool tool, IReadOnlyList<string> paths) : base(tool, paths, "Save Trimmed Copy", true)
    {
        Range.Changed += Update;
        var play = Ui.Button(Ui.Row(6, Ui.Glyph("", 12), Ui.Text("Play Selected Range", 12)), () => Strip.PlayRange(Range.Start, Range.End));
        var row = new DockPanel();
        DockPanel.SetDock(play, Dock.Right);
        row.Children.Add(play);
        row.Children.Add(readout);
        var extras = IsVideo ? new UIElement[0] : [Ui.Row(10,
            Ui.Toggle("Trim silent ends automatically", false, v => trimSilence = v),
            Ui.Slider("Threshold", -70, -20, threshold, v => threshold = v, v => $"{v:0} dB", 1))];
        SetContent(Layout([Wave, Range, row, .. extras]));
    }

    void Update()
    {
        readout.Text = $"{MediaStrip.Fmt(Range.Start)} – {MediaStrip.Fmt(Range.End)}  ·  {MediaStrip.Fmt(Range.End - Range.Start)}";
        UpdateHighlight([(Range.Start, Range.End)]);
    }

    protected override void OnLoaded(MediaInfo info) => Update();

    protected override bool Apply()
    {
        var (a, b, ts, th, p) = (Range.Start, Range.End, trimSilence, threshold, Paths[0]);
        Run($"Trimming {FileName(p)}", async () => [IsVideo ? await Media.Trim(p, a, b) : await Media.TrimAudio(p, a, b, ts, th)]);
        return true;
    }
}

/// Shows one video frame with a rectangle overlay; scrub with the slider.
abstract class VideoFrameEditor : EditorWindow
{
    protected readonly ImageCanvas Canvas = new();
    protected readonly RectOverlay Overlay = new();
    protected readonly Slider Scrub = new() { Minimum = 0, Maximum = 1 };
    protected readonly TextBlock TimeText = Ui.Caption("0:00.00", false);
    protected double Duration;
    protected MediaInfo? Info;
    readonly Action loadFrame;
    MagickImage? frame;
    protected MagickImage? CurrentFrame => frame;

    protected VideoFrameEditor(FileTool tool, IReadOnlyList<string> paths, string primary) : base(tool, paths, primary)
    {
        Canvas.SetOverlay(Overlay);
        loadFrame = Debounce.Create(() =>
        {
            var tmp = Path.Combine(Output.TempDirectory(), "frame.png");
            Media.Frame(paths[0], Scrub.Value, tmp, 1280).GetAwaiter().GetResult();
            return new MagickImage(tmp);
        }, img => { frame?.Dispose(); frame = img; Canvas.SetImage(RenderFrame(img)); Overlay.Redraw(); }, 80);
        Scrub.ValueChanged += (_, e) => { TimeText.Text = MediaStrip.Fmt(e.NewValue); loadFrame(); OnScrub(e.NewValue); };
        Overlay.Changed += () => { OnRectsChanged(); if (frame != null) Canvas.SetImage(RenderFrame(frame)); };
        _ = Init();
    }

    async Task Init()
    {
        Info = await Media.InfoAsync(Paths[0]);
        Duration = Info.Duration;
        Scrub.Maximum = Math.Max(Duration - 0.05, 0.01);
        loadFrame();
        OnRectsChanged();
    }

    protected virtual System.Windows.Media.Imaging.BitmapSource RenderFrame(MagickImage img) => Ui.ToBitmap(img);
    protected virtual void OnScrub(double t) { }
    protected virtual void OnRectsChanged() { }

    protected UIElement Layout(UIElement? top, params UIElement[] below)
    {
        var dock = new DockPanel();
        if (top != null) { DockPanel.SetDock(top, Dock.Top); if (top is FrameworkElement f) f.Margin = new Thickness(0, 0, 0, 10); dock.Children.Add(top); }
        var scrubRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(TimeText, Dock.Right);
        TimeText.Width = 64; TimeText.TextAlignment = TextAlignment.Right;
        scrubRow.Children.Add(TimeText);
        scrubRow.Children.Add(Scrub);
        var bottom = Ui.Column(6, [scrubRow, .. below]);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(Canvas);
        return dock;
    }
}

sealed class CropVideoEditor : VideoFrameEditor
{
    readonly TextBlock size = Ui.Caption("", false);

    public CropVideoEditor(IReadOnlyList<string> paths) : base(FileTool.CropVideo, paths, "Save Cropped Copy")
    {
        Overlay.Single = true; Overlay.DimOutside = true;
        Overlay.Rects.Add(new Redaction(new RectF(0.1, 0.1, 0.8, 0.8)));
        SetContent(Layout(null));
        SetOptions(size);
    }

    protected override void OnRectsChanged()
    {
        CanApply = Overlay.Rects.Count > 0;
        if (Info != null && Overlay.Rects.Count > 0) size.Text = $"{(int)(Overlay.Rects[0].Rect.W * Info.Width)} × {(int)(Overlay.Rects[0].Rect.H * Info.Height)} px";
    }

    protected override bool Apply()
    {
        var (r, p) = (Overlay.Rects[0].Rect, Paths[0]);
        Jobs.Run($"Cropping {FileName(p)}", Paths, async _ => [await Media.Crop(p, r)]);
        return true;
    }
}

sealed class RedactVideoEditor : VideoFrameEditor
{
    RedactionStyle style = RedactionStyle.Blur;
    readonly RangeSlider rangeSlider = new();

    public RedactVideoEditor(IReadOnlyList<string> paths) : base(FileTool.RedactVideo, paths, "Save Redacted Copy")
    {
        var bar = Ui.Row(8,
            Ui.Segmented(Enum.GetValues<RedactionStyle>(), style, s => s.ToString(), s => { style = s; if (CurrentFrame != null) Canvas.SetImage(RenderFrame(CurrentFrame)); }),
            Ui.Button("Detect Faces in Frame", DetectFaces),
            Ui.IconButton("", Overlay.DeleteSelected, "Delete redaction"));
        rangeSlider.Changed += () =>
        {
            if (Overlay.Selected is { } i && i < Overlay.Rects.Count) Overlay.Rects[i] = Overlay.Rects[i] with { Start = rangeSlider.Start, End = rangeSlider.End };
            if (CurrentFrame != null) Canvas.SetImage(RenderFrame(CurrentFrame));
        };
        var rangeRow = new DockPanel();
        var whole = Ui.Button("Show for Entire Video", () => { rangeSlider.Start = 0; rangeSlider.End = Duration; rangeSlider.Redraw(); ApplyRange(); });
        DockPanel.SetDock(whole, Dock.Right);
        var label = Ui.Caption("Selected rectangle visible", false); label.Width = 170;
        DockPanel.SetDock(label, Dock.Left);
        rangeRow.Children.Add(label);
        rangeRow.Children.Add(whole);
        rangeRow.Children.Add(rangeSlider);
        SetContent(Layout(bar, rangeRow));
        SetOptions(Ui.Caption("Draw rectangles to cover areas, then set when each one shows.", false));
        this.OnKey(Key.Delete, Overlay.DeleteSelected);
        CanApply = false;
    }

    void ApplyRange()
    {
        if (Overlay.Selected is { } i && i < Overlay.Rects.Count) Overlay.Rects[i] = Overlay.Rects[i] with { Start = rangeSlider.Start, End = rangeSlider.End };
    }

    protected override void OnRectsChanged()
    {
        if (Duration > 0) { rangeSlider.Maximum = Duration; if (rangeSlider.End <= 0.05 || rangeSlider.End > Duration) rangeSlider.End = Duration; }
        // New rectangles default to the whole video.
        for (int i = 0; i < Overlay.Rects.Count; i++)
            if (Overlay.Rects[i].Start == null) Overlay.Rects[i] = Overlay.Rects[i] with { Start = 0, End = Duration };
        if (Overlay.Selected is { } s && s < Overlay.Rects.Count) { rangeSlider.Start = Overlay.Rects[s].Start ?? 0; rangeSlider.End = Overlay.Rects[s].End ?? Duration; }
        rangeSlider.Playhead = Scrub.Value;
        rangeSlider.Redraw();
        CanApply = Overlay.Rects.Count > 0;
    }

    protected override void OnScrub(double t) { rangeSlider.Playhead = t; rangeSlider.Redraw(); }

    protected override System.Windows.Media.Imaging.BitmapSource RenderFrame(MagickImage img)
    {
        var t = Scrub.Value;
        var active = Overlay.Rects.Where(r => (r.Start ?? 0) <= t && t <= (r.End ?? double.MaxValue)).Select(r => r.Rect).ToList();
        if (active.Count == 0) return Ui.ToBitmap(img);
        using var red = Images.Redact(img, active, style);
        return Ui.ToBitmap(red);
    }

    async void DetectFaces()
    {
        if (CurrentFrame == null) return;
        if (Core.Platform.Faces == null) { App.Alert("Detection unavailable", "Windows face detection isn't available on this PC."); return; }
        var found = await Core.Platform.Faces(CurrentFrame);
        if (found.Count == 0) { App.Alert("Nothing found", "No faces were detected in this frame."); return; }
        Overlay.Rects.AddRange(found.Select(r => new Redaction(r, 0, Duration)));
        Overlay.Redraw();
        OnRectsChanged();
        Canvas.SetImage(RenderFrame(CurrentFrame));
    }

    protected override bool Apply()
    {
        var (rects, s, p) = (Overlay.Rects.ToList(), style, Paths[0]);
        Jobs.Run($"Redacting {FileName(p)}", Paths, async _ => [await Media.Redact(p, rects, s)]);
        return true;
    }
}

sealed class SpeedEditor : EditorWindow
{
    double speed = 2;
    readonly TextBlock big = Ui.Text("2×", 54, FontWeights.Bold, Theme.Accent);

    public SpeedEditor(IReadOnlyList<string> paths) : base(FileTool.ChangeVideoSpeed, paths, "Change Video Speed")
    {
        big.HorizontalAlignment = HorizontalAlignment.Center;
        var slider = new Slider { Minimum = 0.25, Maximum = 4, Value = speed, TickFrequency = 0.05, IsSnapToTickEnabled = true, Width = 460 };
        slider.ValueChanged += (_, e) => Set(e.NewValue);
        var presets = Ui.Row(6, new[] { 0.25, 0.5, 0.75, 1.25, 1.5, 2, 3, 4 }.Select(p => (UIElement)Ui.Button($"{p:0.##}×", () => { slider.Value = p; })).ToArray());
        presets.HorizontalAlignment = HorizontalAlignment.Center;
        var col = Ui.Column(20, big, slider, presets, Ui.Caption("Audio pitch is preserved."));
        col.HorizontalAlignment = HorizontalAlignment.Center; col.VerticalAlignment = VerticalAlignment.Center;
        SetContent(col);
        SetOptions(Ui.Caption($"{paths.Count} video{(paths.Count == 1 ? "" : "s")}", false));
    }

    void Set(double v) { speed = v; big.Text = $"{v:0.##}×"; CanApply = Math.Abs(v - 1) > 0.001; }

    protected override bool Apply()
    {
        var s = speed;
        Jobs.Batch(Tool, Paths, async p => (IEnumerable<string>)[await Media.ChangeSpeed(p, s)]);
        return true;
    }
}

sealed class SplitVideoEditor : MediaEditor
{
    readonly List<double> points = [];
    readonly HashSet<int> removed = [];
    readonly StackPanel clipList = new();
    bool joinKept;

    public SplitVideoEditor(IReadOnlyList<string> paths) : base(FileTool.SplitVideo, paths, "Export Clips", false)
    {
        var add = Ui.Button(Ui.Row(6, Ui.Glyph("", 12), Ui.Text("Split at Playhead", 12)), AddPoint);
        var mode = Ui.Segmented([false, true], false, j => j ? "Join kept clips" : "Separate clips", j => { joinKept = j; PrimaryTitle = j ? "Export One Video" : "Export Clips"; });
        var side = Ui.Scroll(clipList); side.Width = 240;
        var main = Layout(Range, add);
        var dock = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        side.Margin = new Thickness(12, 0, 0, 0);
        dock.Children.Add(side);
        dock.Children.Add(main);
        SetContent(dock);
        SetOptions(mode);
        CanApply = false;
    }

    List<(double A, double B)> Clips()
    {
        var b = new List<double> { 0 };
        b.AddRange(points.Order());
        b.Add(Duration);
        return Enumerable.Range(0, b.Count - 1).Select(i => (b[i], b[i + 1])).ToList();
    }

    void AddPoint()
    {
        var t = Strip.Position;
        if (t < 0.05 || t > Duration - 0.05 || points.Any(p => Math.Abs(p - t) < 0.05)) return;
        points.Add(t);
        removed.Clear();
        Render();
    }

    void Render()
    {
        clipList.Children.Clear();
        var clips = Clips();
        for (int i = 0; i < clips.Count; i++)
        {
            var idx = i;
            var (a, b) = clips[i];
            var keep = Ui.Toggle("", !removed.Contains(i), v => { if (v) removed.Remove(idx); else removed.Add(idx); CanApply = points.Count > 0 && removed.Count < Clips().Count; });
            var row = Ui.Row(6, keep, Ui.Column(0, Ui.Text($"Clip {i + 1}", 12, FontWeights.SemiBold), Ui.Caption($"{MediaStrip.Fmt(a)} – {MediaStrip.Fmt(b)}", false)),
                Ui.IconButton("", () => Strip.PlayRange(a, b), "Play clip"));
            clipList.Children.Add(row);
        }
        if (points.Count > 0) clipList.Children.Add(Ui.Button("Clear Split Points", () => { points.Clear(); removed.Clear(); Render(); }));
        CanApply = points.Count > 0 && removed.Count < clips.Count;
    }

    protected override void OnLoaded(MediaInfo info) { Range.Start = 0; Range.End = Duration; Render(); }

    protected override bool Apply()
    {
        var (pts, rem, join, p) = (points.ToList(), removed.ToHashSet(), joinKept, Paths[0]);
        Run($"Splitting {FileName(p)}", async () => await Media.Split(p, pts, rem, join));
        return true;
    }
}

sealed class SnapshotsEditor : MediaEditor
{
    readonly List<double> times = [];
    readonly StackPanel list = new();
    double interval = 5;

    public SnapshotsEditor(IReadOnlyList<string> paths) : base(FileTool.VideoSnapshots, paths, "Save Frames", false)
    {
        var add = Ui.Button(Ui.Row(6, Ui.Glyph("", 12), Ui.Text("Add Frame at Playhead", 12)), () => Add(Strip.Position));
        var every = Ui.Row(6, Ui.Caption("Every", false), Ui.Field("5", v => double.TryParse(v, out interval), 50), Ui.Caption("s", false),
            Ui.Button("Add Frames", () => { if (interval > 0.1) for (double t = 0; t < Duration; t += interval) Add(t); }));
        var side = Ui.Scroll(list); side.Width = 200; side.Margin = new Thickness(12, 0, 0, 0);
        var dock = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        dock.Children.Add(side);
        dock.Children.Add(Layout(Ui.Row(12, add, every)));
        SetContent(dock);
        SetOptions(Ui.Caption("Frames are saved as PNG.", false));
        CanApply = false;
    }

    void Add(double t)
    {
        if (times.Any(x => Math.Abs(x - t) < 0.01)) return;
        times.Add(t); times.Sort();
        Render();
    }

    void Render()
    {
        list.Children.Clear();
        foreach (var t in times)
            list.Children.Add(Ui.Row(4, Ui.Text(MediaStrip.Fmt(t), 12), Ui.IconButton("", () => Strip.Seek(t), "Preview frame"),
                Ui.IconButton("", () => { times.Remove(t); Render(); }, "Remove frame")));
        PrimaryTitle = $"Save {times.Count} Frame{(times.Count == 1 ? "" : "s")}";
        CanApply = times.Count > 0;
    }

    protected override bool Apply()
    {
        var (t, p) = (times.ToList(), Paths[0]);
        Run("Saving snapshots", async () => await Media.Snapshots(p, t));
        return true;
    }
}

sealed class GifEditor : MediaEditor
{
    int fps = 12, width = 480;
    readonly TextBlock readout = Ui.Caption("", false);

    public GifEditor(IReadOnlyList<string> paths) : base(FileTool.VideoToGif, paths, "Create GIF", false)
    {
        Range.Changed += Update;
        SetContent(Layout(Range, Ui.Row(12, readout,
            Ui.Labeled("Frame rate", Ui.Combo([8, 10, 12, 15, 20, 24], fps, f => $"{f} fps", f => { fps = f; Update(); }), 80),
            Ui.Labeled("Width", Ui.Combo([320, 480, 640, 800, 1080], width, w => $"{w} px", w => width = w), 50))));
    }

    protected override void OnLoaded(MediaInfo info) { Range.End = Math.Min(Duration, 6); Range.Redraw(); Update(); }

    void Update() => readout.Text = $"{MediaStrip.Fmt(Range.Start)} – {MediaStrip.Fmt(Range.End)} · ≈{(int)((Range.End - Range.Start) * fps)} frames";

    protected override bool Apply()
    {
        var (a, b, f, w, p) = (Range.Start, Range.End, fps, width, Paths[0]);
        Run("Creating GIF", async () => { var o = Output.PathFor(p, "gif"); await Media.Gif(p, o, f, w, a, b); return [o]; });
        return true;
    }
}

sealed class NormalizeEditor : EditorWindow
{
    double target = -16, peak = -1;

    public NormalizeEditor(IReadOnlyList<string> paths) : base(FileTool.NormalizeAudio, paths, "Normalize Volume")
    {
        (string, double)[] targets = [("Podcast / streaming (−16 LUFS)", -16), ("Music (−14 LUFS)", -14), ("Broadcast (−23 LUFS)", -23), ("Loud (−11 LUFS)", -11)];
        SetContent(Ui.Column(12,
            Ui.Labeled("Target loudness", Ui.Combo(targets, targets[0], t => t.Item1, t => target = t.Item2)),
            Ui.Slider("Peak ceiling", -6, 0, peak, v => peak = v, v => $"{v:0.0} dB", 0.5),
            Ui.Caption("Videos keep their picture; only the audio track is replaced."),
            Ui.Text("Files", 13, FontWeights.SemiBold),
            Ui.Column(2, paths.Select(p => (UIElement)Ui.Caption(FileName(p), false)).ToArray())));
    }

    protected override bool Apply()
    {
        var (t, p) = (target, peak);
        Jobs.Batch(Tool, Paths, async f => (IEnumerable<string>)[await Media.Normalize(f, t, p)]);
        return true;
    }
}

sealed class ChannelsEditor : EditorWindow
{
    int count = 1;
    double left = 1, right = 1;

    public ChannelsEditor(IReadOnlyList<string> paths) : base(FileTool.AudioChannels, paths, "Convert Audio Channels")
    {
        Slider? l = null, r = null;
        FrameworkElement S(string name, double v, Action<double> set, Action<Slider> capture)
        {
            var row = Ui.Slider(name, 0, 2, v, set, x => $"{x * 100:0}%");
            capture(((DockPanel)((Grid)row).Children[1]).Children.OfType<Slider>().First());
            return row;
        }
        var leftRow = S("Left volume", left, v => left = v, s => l = s);
        var rightRow = S("Right volume", right, v => right = v, s => r = s);
        SetContent(Ui.Column(12,
            Ui.Segmented([1, 2], count, c => c == 1 ? "Mono" : "Stereo", c => count = c),
            leftRow, rightRow,
            Ui.Row(6,
                Ui.Button("Left only", () => { l!.Value = 1; r!.Value = 0; }),
                Ui.Button("Right only", () => { l!.Value = 0; r!.Value = 1; }),
                Ui.Button("Swap", () => { (l!.Value, r!.Value) = (r!.Value, l.Value); }),
                Ui.Button("Reset", () => { l!.Value = 1; r!.Value = 1; })),
            Ui.Text("Files", 13, FontWeights.SemiBold),
            Ui.Column(2, paths.Select(p => (UIElement)Ui.Caption(FileName(p), false)).ToArray())));
    }

    protected override bool Apply()
    {
        var (c, l, r) = (count, left, right);
        Jobs.Batch(Tool, Paths, async f => (IEnumerable<string>)[await Media.Channels(f, c, l, r)]);
        return true;
    }
}

sealed class BleepEditor : MediaEditor
{
    readonly List<(double A, double B)> ranges = [];
    readonly WrapPanel chips = new();
    bool tone = true;

    public BleepEditor(IReadOnlyList<string> paths) : base(FileTool.RedactAudio, paths, "Save Bleeped Copy", true)
    {
        Range.Changed += Highlight;
        var buttons = Ui.Row(8,
            Ui.Button(Ui.Row(6, Ui.Glyph("", 12), Ui.Text("Add Bleep", 12)), () => { ranges.Add((Range.Start, Range.End)); ranges.Sort(); Render(); }),
            Ui.Button(Ui.Row(6, Ui.Glyph("", 12), Ui.Text("Play", 12)), () => Strip.PlayRange(Range.Start, Range.End)),
            Ui.Button("Bleep Entire Audio", () => { ranges.Clear(); ranges.Add((0, Duration)); Render(); }));
        SetContent(Layout(Wave, Range, buttons, chips));
        SetOptions(Ui.Segmented([true, false], true, t => t ? "Tone" : "Silence", t => tone = t));
        CanApply = false;
    }

    protected override void OnLoaded(MediaInfo info) { Range.End = Math.Min(Duration, 1); Range.Redraw(); Highlight(); }

    void Highlight() => UpdateHighlight([.. ranges, (Range.Start, Range.End)]);

    void Render()
    {
        chips.Children.Clear();
        for (int i = 0; i < ranges.Count; i++)
        {
            var idx = i;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(0x30, 0xE0, 0x30, 0x30)), Padding = new Thickness(10, 3, 4, 3), Margin = new Thickness(0, 0, 6, 6),
                Child = Ui.Row(4, Ui.Text($"{MediaStrip.Fmt(ranges[i].A)}–{MediaStrip.Fmt(ranges[i].B)}", 11), Ui.IconButton("", () => { ranges.RemoveAt(idx); Render(); }, "Remove")),
            };
            chips.Children.Add(chip);
        }
        CanApply = ranges.Count > 0;
        Highlight();
    }

    protected override bool Apply()
    {
        var (r, t, p) = (ranges.ToList(), tone, Paths[0]);
        Run($"Bleeping {FileName(p)}", async () => [await Media.Bleep(p, r, t)]);
        return true;
    }
}

sealed class VisualizerEditor : EditorWindow
{
    Media.VisualizerOptions opts;
    readonly Border accentSwatch = new() { Width = 22, Height = 22, CornerRadius = new CornerRadius(11) };
    readonly Border bgSwatch = new() { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderBrush = Theme.Border, BorderThickness = new Thickness(1) };
    readonly ImageCanvas preview = new();
    readonly Action refresh;
    static readonly (string Name, int W, int H)[] Sizes = [("Landscape 720p", 1280, 720), ("Landscape 1080p", 1920, 1080), ("Square 1080", 1080, 1080), ("Story 1080×1920", 1080, 1920)];

    public VisualizerEditor(IReadOnlyList<string> paths) : base(FileTool.AudioToVideo, paths, "Create Visualization")
    {
        opts = new Media.VisualizerOptions(Title: Path.GetFileNameWithoutExtension(paths[0]));
        refresh = Debounce.Create(RenderPreview, b => preview.SetImage(b));
        var accent = Ui.Button(Ui.Row(6, accentSwatch, Ui.Text("Accent", 12)), () => { if (Ui.PickColor(opts.Accent) is { } c) { opts = opts with { Accent = c }; Sync(); } });
        var bg = Ui.Button(Ui.Row(6, bgSwatch, Ui.Text("Background", 12)), () => { if (Ui.PickColor(opts.Background) is { } c) { opts = opts with { Background = c }; Sync(); } });
        var art = Ui.Button("Artwork…", () => { if (Ui.PickFile("Images|*.png;*.jpg;*.jpeg;*.webp", "Choose an image for the visualization") is { } f) { opts = opts with { ImagePath = f }; Sync(); } });
        var side = Ui.Column(10,
            Ui.Labeled("Style", Ui.Combo(Enum.GetValues<VisualizerStyle>(), opts.Style, s => s.ToString(), s => { opts = opts with { Style = s }; Sync(); }), 70),
            Ui.Labeled("Size", Ui.Combo(Sizes, Sizes[0], s => s.Name, s => { opts = opts with { Width = s.W, Height = s.H }; Sync(); }), 70),
            Ui.Row(8, accent, bg), art,
            Ui.Labeled("Title", Ui.Field(opts.Title, v => { opts = opts with { Title = v }; Sync(); }), 70));
        side.Width = 300;
        var dock = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        side.Margin = new Thickness(12, 0, 0, 0);
        dock.Children.Add(side);
        dock.Children.Add(preview);
        SetContent(dock);
        SetOptions(Ui.Caption("Renders an MP4 with your audio.", false));
        Sync();
    }

    void Sync()
    {
        accentSwatch.Background = new SolidColorBrush(Ui.ParseColor(opts.Accent));
        bgSwatch.Background = new SolidColorBrush(Ui.ParseColor(opts.Background));
        refresh();
    }

    /// Static approximation of a frame: background, title and stylised bars in the accent colour.
    System.Windows.Media.Imaging.BitmapSource RenderPreview()
    {
        double scale = 480.0 / Math.Max(opts.Width, opts.Height);
        uint w = (uint)(opts.Width * scale), h = (uint)(opts.Height * scale);
        using var img = opts.ImagePath is { } p && File.Exists(p) ? Images.Load(p) : new MagickImage(new MagickColor(opts.Background), w, h);
        img.Resize(new MagickGeometry(w, h) { FillArea = true });
        img.Extent(w, h, Gravity.Center);
        if (opts.ImagePath != null) img.Evaluate(Channels.RGB, EvaluateOperator.Multiply, 0.6);
        var d = new ImageMagick.Drawing.Drawables().FillColor(new MagickColor(opts.Accent)).StrokeColor(new MagickColor(opts.Accent));
        int n = 48;
        for (int i = 0; i < n; i++)
        {
            double level = 0.25 + 0.6 * Math.Abs(Math.Sin(i * 0.37) * Math.Cos(i * 0.11));
            double bw = w * 0.8 / n, x = w * 0.1 + i * bw, bh = level * h * 0.4;
            switch (opts.Style)
            {
                case VisualizerStyle.Circle:
                    double a = i * 2 * Math.PI / n, r = Math.Min(w, h) * 0.18, len = r * 0.2 + level * r;
                    d.StrokeWidth(2).Line(w / 2 + Math.Cos(a) * r, h / 2 + Math.Sin(a) * r, w / 2 + Math.Cos(a) * (r + len), h / 2 + Math.Sin(a) * (r + len));
                    break;
                case VisualizerStyle.Wave:
                    if (i > 0) { double prev = 0.25 + 0.6 * Math.Abs(Math.Sin((i - 1) * 0.37) * Math.Cos((i - 1) * 0.11)); d.StrokeWidth(2).Line(x - bw, h / 2 - prev * h * 0.2 * ((i - 1) % 2 == 0 ? 1 : -1), x, h / 2 - level * h * 0.2 * (i % 2 == 0 ? 1 : -1)); }
                    break;
                case VisualizerStyle.Mirror:
                    d.StrokeWidth(0).Rectangle(x + bw * 0.15, h / 2 - bh / 2, x + bw * 0.85, h / 2 + bh / 2);
                    break;
                default:
                    d.StrokeWidth(0).Rectangle(x + bw * 0.15, h * 0.75 - bh, x + bw * 0.85, h * 0.75);
                    break;
            }
        }
        d.Draw(img);
        if (!string.IsNullOrWhiteSpace(opts.Title))
            new ImageMagick.Drawing.Drawables().FontPointSize(h / 18.0).FillColor(MagickColors.White).Text(w * 0.06, h * 0.08 + h / 18.0, opts.Title).Draw(img);
        return Ui.ToBitmap(img);
    }

    protected override bool Apply()
    {
        var (o, p) = (opts, Paths[0]);
        Jobs.Run("Creating visualization", Paths, async _ => [await Media.Visualize(p, o)]);
        return true;
    }
}
