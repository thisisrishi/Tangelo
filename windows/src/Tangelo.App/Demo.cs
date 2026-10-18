using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tangelo.App.Editors;
using Tangelo.Core;

namespace Tangelo.App;

/// `Tangelo.exe --demo <what> [files…] [--snap out.png]` opens a window without dragging and can
/// render it to a PNG (used by CI to capture screenshots).
static class Demo
{
    public static void Run(string[] args)
    {
        var what = args.Length > 0 ? args[0] : "wheel-conversions";
        var snapIndex = Array.IndexOf(args, "--snap");
        var files = args.Skip(1).TakeWhile(a => a != "--snap").Select(Path.GetFullPath).ToList();
        var snap = snapIndex >= 0 && snapIndex + 1 < args.Length ? args[snapIndex + 1] : null;
        var center = new Point(SystemParameters.WorkArea.Width / 2, SystemParameters.WorkArea.Height / 2);

        Window? window = what switch
        {
            "wheel-conversions" => WheelWindow.ShowWith(WheelKind.Conversions, files, center),
            "wheel-tools" => WheelWindow.ShowWith(WheelKind.Tools, files, center),
            "wheel-actions" => WheelWindow.ShowWith(WheelKind.Actions, files, center),
            "float" => FloatManager.NewFloat(files, center),
            "tray" => ShowTray(files),
            "settings" => ShowSettings(),
            "welcome" => ShowWelcome(),
            _ when Enum.TryParse<FileTool>(what, true, out var tool) => OpenEditor(tool, files),
            _ => null,
        };
        if (window == null) { Application.Current.Shutdown(2); return; }
        if (what.StartsWith("wheel")) typeof(WheelWindow).GetMethod("Highlight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(window, [(int?)1]);
        if (snap == null) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { Capture(window, snap); }
            finally { Application.Current.Shutdown(0); }
        };
        timer.Start();
    }

    static Window ShowTray(List<string> files)
    {
        if (files.Count > 0) TrayPopup.ShowFor(files); else TrayPopup.Toggle();
        return Application.Current.Windows.OfType<TrayPopup>().First();
    }

    static Window ShowSettings()
    {
        SettingsWindow.ShowSingle();
        return Application.Current.Windows.OfType<SettingsWindow>().First();
    }

    static Window ShowWelcome()
    {
        WelcomeWindow.ShowOnce();
        return Application.Current.Windows.OfType<WelcomeWindow>().First();
    }

    static Window OpenEditor(FileTool tool, List<string> files)
    {
        EditorWindow.Open(tool, files);
        return Application.Current.Windows.OfType<EditorWindow>().Last();
    }

    static void Capture(Window w, string path)
    {
        var content = (FrameworkElement)w.Content;
        var dpi = VisualTreeHelper.GetDpi(content);
        int width = (int)Math.Max(1, content.ActualWidth * dpi.DpiScaleX), height = (int)Math.Max(1, content.ActualHeight * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        // Paint the window background first so transparent/Mica windows are legible.
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            var bg = w.AllowsTransparency ? (Brush)new SolidColorBrush(Color.FromRgb(0x5A, 0x6B, 0x7C)) : (w.Background ?? Theme.Surface);
            ctx.DrawRectangle(bg, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            ctx.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        rtb.Render(dv);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
