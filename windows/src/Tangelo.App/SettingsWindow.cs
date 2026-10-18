using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tangelo.Core;

namespace Tangelo.App;

sealed class SettingsWindow : Window
{
    static SettingsWindow? instance;
    static Settings S => Settings.Current;

    public static void ShowSingle(int tab = 0)
    {
        if (instance == null) { instance = new SettingsWindow(); instance.Closed += (_, _) => instance = null; }
        ((TabControl)instance.Content).SelectedIndex = tab;
        instance.Show();
        instance.Activate();
    }

    SettingsWindow()
    {
        Title = "Tangelo Settings";
        Width = 680; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Surface;
        var tabs = new TabControl { Margin = new Thickness(12) };
        tabs.Items.Add(Tab("General", General()));
        tabs.Items.Add(Tab("Shortcuts", Shortcuts()));
        tabs.Items.Add(Tab("Conversions", Conversions()));
        tabs.Items.Add(Tab("Helpers", Helpers()));
        tabs.Items.Add(Tab("Formats", FormatsTab()));
        tabs.Items.Add(Tab("History", HistoryTab()));
        Content = tabs;
        Closed += (_, _) => S.Save();
        SourceInitialized += (_, _) => Native.ApplyModernChrome(this, Theme.Dark);
    }

    static TabItem Tab(string header, UIElement content) => new() { Header = header, Content = Ui.Scroll(new Border { Padding = new Thickness(16), Child = content }) };

    static void Save() => S.Save();

    static UIElement Section(string title, params UIElement[] rows) =>
        Ui.Column(4, [Ui.Text(title, 13, FontWeights.SemiBold), .. rows]);

    UIElement General()
    {
        var outputs = Ui.Combo(Enum.GetValues<OutputLocation>(), S.OutputLocation, o => o switch
        {
            OutputLocation.SameFolder => "Next to the original",
            OutputLocation.Custom => "Custom folder…",
            _ => o.ToString(),
        }, o =>
        {
            if (o == OutputLocation.Custom && Ui.PickFolder("Choose where Tangelo saves results") is { } dir) S.CustomOutputPath = dir;
            S.OutputLocation = o == OutputLocation.Custom && S.CustomOutputPath.Length == 0 ? OutputLocation.SameFolder : o;
            Save();
        });
        return Ui.Column(14,
            Section("Startup",
                Ui.Toggle("Launch Tangelo when I sign in", S.LaunchAtLogin, v => { S.LaunchAtLogin = v; Save(); Integration.SetLaunchAtLogin(v); }),
                Ui.Toggle("Add \"Tangelo…\" to the File Explorer right-click menu", S.ExplorerMenu, v => { S.ExplorerMenu = v; Save(); Integration.SetExplorerMenu(v); })),
            Section("Results",
                Ui.Labeled("Save results", outputs),
                Ui.Toggle("Show results in File Explorer", S.RevealOutputs, v => { S.RevealOutputs = v; Save(); }),
                Ui.Toggle("Sound feedback", S.SoundFeedback, v => { S.SoundFeedback = v; Save(); })),
            Section("Floats",
                Ui.Toggle("Shake while dragging to create a Float", S.ShakeToFloat, v => { S.ShakeToFloat = v; Save(); }),
                Ui.Toggle("Restore Floats after restarting", S.RestoreFloats, v => { S.RestoreFloats = v; Save(); }),
                Ui.Toggle("Remove files from a Float after dragging them out", S.RemoveAfterDragOut, v => { S.RemoveAfterDragOut = v; Save(); })),
            Section("Terminal",
                Ui.Labeled("Open folders in", Ui.Combo(["Windows Terminal", "PowerShell", "Command Prompt"], S.Terminal, t => t, t => { S.Terminal = t; Save(); }))));
    }

    UIElement Shortcuts()
    {
        string[] choices = ["Shift", "Ctrl", "Alt", "Ctrl+Shift", "Ctrl+Alt", "Alt+Shift", "Win"];
        var warning = Ui.Caption("", true);
        warning.Foreground = Theme.Accent;
        void Check()
        {
            var masks = new[] { S.ConversionsModifier, S.ToolsModifier, S.ActionsModifier }.Select(DragMonitor.Parse).ToList();
            warning.Text = masks.Distinct().Count() < masks.Count ? "Two wheels share the same shortcut; only the first will open." : "";
        }
        FrameworkElement Row(string title, Func<string> get, Action<string> set, Func<bool> enabled, Action<bool> setEnabled)
        {
            var g = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var combo = Ui.Combo(choices, get(), c => c, c => { set(c); Save(); Check(); });
            DockPanel.SetDock(combo, Dock.Right);
            g.Children.Add(combo);
            g.Children.Add(Ui.Toggle(title, enabled(), v => { setEnabled(v); Save(); }));
            return g;
        }
        Check();
        return Ui.Column(10,
            Ui.Caption("Tap the chosen modifier while dragging files to open a wheel under the cursor. Drop on a petal to run it; drop in the middle to cancel."),
            Row("Conversion wheel", () => S.ConversionsModifier, v => S.ConversionsModifier = v, () => S.ConversionsWheelEnabled, v => S.ConversionsWheelEnabled = v),
            Row("Tools wheel", () => S.ToolsModifier, v => S.ToolsModifier = v, () => S.ToolsWheelEnabled, v => S.ToolsWheelEnabled = v),
            Row("Actions wheel", () => S.ActionsModifier, v => S.ActionsModifier = v, () => S.ActionsWheelEnabled, v => S.ActionsWheelEnabled = v),
            warning,
            Ui.Caption("Tip: holding Shift, Ctrl or Alt also changes File Explorer's move/copy/shortcut behaviour while dragging. Tapping is enough; you don't need to hold the key."));
    }

    UIElement Conversions() => Ui.Column(14,
        Section("Images",
            Ui.Slider("Lossy quality", 30, 100, S.ImageQuality, v => { S.ImageQuality = (int)v; Save(); }, v => $"{v:0}%", 1),
            Ui.Toggle("Preserve metadata (EXIF, GPS) when converting", S.PreserveMetadata, v => { S.PreserveMetadata = v; Save(); })),
        Section("Audio",
            Ui.Labeled("Bitrate", Ui.Combo([96, 128, 192, 256, 320], S.AudioBitrate, b => $"{b} kbps", b => { S.AudioBitrate = b; Save(); }))),
        Section("Video",
            Ui.Toggle("Prefer HEVC (smaller files, newer devices)", S.PreferHevc, v => { S.PreferHevc = v; Save(); }),
            Ui.Labeled("GIF frame rate", Ui.Combo([8, 10, 12, 15, 20], S.GifFps, f => $"{f} fps", f => { S.GifFps = f; Save(); })),
            Ui.Labeled("GIF width", Ui.Combo([320, 480, 640, 800], S.GifWidth, w => $"{w} px", w => { S.GifWidth = w; Save(); }))));

    UIElement Helpers()
    {
        FrameworkElement Row(string title, bool ok, string hint)
        {
            var d = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var g = Ui.Glyph(ok ? "" : "", 13, ok ? new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43)) : Theme.Subtle);
            g.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(g, Dock.Left);
            d.Children.Add(g);
            if (!ok) { var h = Ui.Caption(hint, false); DockPanel.SetDock(h, Dock.Right); d.Children.Add(h); }
            d.Children.Add(Ui.Text(title, 13));
            return d;
        }
        var ffmpegPath = Ui.Field(S.FFmpegPath, v => { S.FFmpegPath = v; Save(); }, 360);
        return Ui.Column(14,
            Section("Built in",
                Row("Images: JPG, PNG, WebP, AVIF, TIFF, GIF, BMP, ICO, PDF (HEIC/SVG input)", true, ""),
                Row("Documents: DOCX, ODT, RTF, HTML, TXT, Markdown, EPUB, PDF", true, ""),
                Row("Spreadsheets: XLSX, ODS, CSV, TSV, JSON · Subtitles · Archives", true, ""),
                Row("Background removal (on-device model)", Core.Background.Available, "Model missing — reinstall"),
                Row("Text recognition and face detection (Windows)", Core.Platform.Ocr != null, "Add an OCR language in Windows Settings")),
            Section("Helpers",
                Row("ffmpeg — all audio and video tools", Core.Helpers.FFmpeg != null, "Bundled with the installer · winget install Gyan.FFmpeg"),
                Ui.Labeled("Custom ffmpeg.exe", ffmpegPath),
                Row("LibreOffice — PPT/PPTX, DOC, XLS", Core.Helpers.LibreOffice != null, "winget install TheDocumentFoundation.LibreOffice")));
    }

    UIElement FormatsTab()
    {
        var col = Ui.Column(10, Ui.Text("Format compatibility", 14, FontWeights.SemiBold));
        foreach (var c in new[] { FileCategory.Image, FileCategory.Vector, FileCategory.Video, FileCategory.Audio, FileCategory.Document, FileCategory.Pdf, FileCategory.Spreadsheet, FileCategory.Presentation, FileCategory.Subtitle, FileCategory.Archive })
            col.Children.Add(Ui.Column(2, Ui.Row(6, Ui.Glyph(c.Glyph(), 14, Theme.Accent), Ui.Text(c.Title(), 13, FontWeights.SemiBold)),
                Ui.Caption("→ " + string.Join(" · ", Core.Formats.Targets(c).Select(f => f.Label())))));
        col.Children.Add(Ui.Text("Tools", 14, FontWeights.SemiBold));
        foreach (var t in Enum.GetValues<FileTool>())
            col.Children.Add(Ui.Row(8, Ui.Glyph(t.Glyph(), 14, Theme.Accent), Ui.Column(0, Ui.Text(t.Title(), 13, FontWeights.SemiBold), Ui.Caption(t.Subtitle(), false))));
        return col;
    }

    UIElement HistoryTab()
    {
        var list = new StackPanel();
        void Render()
        {
            list.Children.Clear();
            var entries = History.Entries;
            if (entries.Count == 0) list.Children.Add(Ui.Caption("No operations yet."));
            foreach (var e in entries)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var right = Ui.Row(4, Ui.Caption(e.Date.ToString("g"), false));
                if (e.Error == null)
                {
                    right.Children.Add(Ui.Button("Show", () => Explorer.Reveal(e.Outputs)));
                    right.Children.Add(Ui.Button("Undo", () => { TrayPopup.Undo(e); Render(); }));
                }
                DockPanel.SetDock(right, Dock.Right);
                row.Children.Add(right);
                row.Children.Add(Ui.Column(1, Ui.Text(e.Title, 13), Ui.Caption(e.Error ?? string.Join(", ", e.Outputs.Select(Path.GetFileName)), false)));
                list.Children.Add(row);
            }
        }
        Render();
        return Ui.Column(10, Ui.Button("Clear History", () => { History.Clear(); Render(); }), list);
    }
}

sealed class WelcomeWindow : Window
{
    public static void ShowOnce()
    {
        var w = new WelcomeWindow();
        w.Show();
        w.Activate();
    }

    WelcomeWindow()
    {
        Title = "Welcome to Tangelo";
        Width = 560; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Surface;
        var s = Settings.Current;
        FrameworkElement Step(string n, string title, string detail)
        {
            var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Theme.AccentSoft, Child = Ui.Text(n, 13, FontWeights.Bold, Theme.Accent) };
            ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center; ((TextBlock)badge.Child).VerticalAlignment = VerticalAlignment.Center;
            var text = Ui.Column(1, Ui.Text(title, 14, FontWeights.SemiBold), Ui.Caption(detail));
            text.MaxWidth = 420;
            return Ui.Row(12, badge, text);
        }
        var icon = Ui.Brand(72);
        var title = Ui.Text("Welcome to Tangelo", 26, FontWeights.Bold); title.HorizontalAlignment = HorizontalAlignment.Center;
        var sub = Ui.Caption("Convert, edit and move files right where you drag them.", false); sub.HorizontalAlignment = HorizontalAlignment.Center;
        var steps = Ui.Card(Ui.Column(14,
            Step("1", "Drag files", "Start dragging any files in File Explorer or on your desktop."),
            Step("2", "Tap a modifier", $"{s.ConversionsModifier} opens conversions, {s.ToolsModifier} opens tools, {s.ActionsModifier} opens actions."),
            Step("3", "Drop on a petal", "Results appear next to the original. Originals are never changed."),
            Step("✦", "Shake to Float", "Shake while dragging to park files on a floating shelf.")), 18);
        var tip = Ui.Caption("Find Tangelo in the notification area (system tray). Right-click files in File Explorer → Tangelo… for every option.");
        var go = Ui.Button("Get Started", () => { s.DidOnboard = true; s.Save(); Integration.Sync(); Close(); }, true);
        go.HorizontalAlignment = HorizontalAlignment.Center; go.Padding = new Thickness(28, 8, 28, 8);
        Content = new Border
        {
            Padding = new Thickness(30),
            Child = Ui.Column(14, icon, title, sub, steps,
                Ui.Toggle("Launch Tangelo when I sign in", s.LaunchAtLogin, v => s.LaunchAtLogin = v),
                Ui.Toggle("Add \"Tangelo…\" to the File Explorer right-click menu", s.ExplorerMenu, v => s.ExplorerMenu = v),
                tip, go),
        };
        SourceInitialized += (_, _) => Native.ApplyModernChrome(this, Theme.Dark);
    }
}
