using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Tangelo.Core;

namespace Tangelo.App;

/// Per-user (HKCU) Explorer menu entries and launch-at-login; no admin rights needed.
static class Integration
{
    static string Exe => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    public static void Sync()
    {
        var s = Settings.Current;
        if (s.IsNew)
        {
            // Respect the installer's choices (or their absence in the portable build).
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            using var menu = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\Tangelo");
            s.LaunchAtLogin = run?.GetValue("Tangelo") != null;
            s.ExplorerMenu = menu != null;
            s.Save();
        }
        try { SetExplorerMenu(Settings.Current.ExplorerMenu); } catch { }
        try { SetLaunchAtLogin(Settings.Current.LaunchAtLogin); } catch { }
    }

    public static void SetExplorerMenu(bool on)
    {
        foreach (var root in new[] { @"Software\Classes\*\shell\Tangelo", @"Software\Classes\Directory\shell\Tangelo" })
        {
            if (!on) { Registry.CurrentUser.DeleteSubKeyTree(root, false); continue; }
            using var key = Registry.CurrentUser.CreateSubKey(root);
            key.SetValue("", "Tangelo…");
            key.SetValue("Icon", $"\"{Exe}\",0");
            key.SetValue("MultiSelectModel", "Player");
            using var cmd = key.CreateSubKey("command");
            cmd.SetValue("", $"\"{Exe}\" \"%1\"");
        }
    }

    public static void SetLaunchAtLogin(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (on) key.SetValue("Tangelo", $"\"{Exe}\""); else key.DeleteValue("Tangelo", false);
    }
}

static class ResultWindows
{
    public static void ShowCodes(List<(string Path, string Code)> codes)
    {
        if (codes.Count == 1) Ui.Copy(codes[0].Code);
        var list = new StackPanel { Margin = new Thickness(16) };
        if (codes.Count == 0)
            list.Children.Add(Ui.Column(8, Ui.Glyph("", 32, Theme.Subtle), Ui.Text("No QR codes found", 15, FontWeights.SemiBold)));
        foreach (var (path, code) in codes)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var buttons = Ui.Row(6);
            if (Uri.TryCreate(code, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http"))
                buttons.Children.Add(Ui.Button("Open", () => Explorer.Open(code)));
            buttons.Children.Add(Ui.Button("Copy", () => Ui.Copy(code), true));
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            var t = Ui.Text(code, 13, wrap: true); t.MaxWidth = 340;
            row.Children.Add(Ui.Column(2, t, Ui.Caption(Path.GetFileName(path), false)));
            list.Children.Add(row);
        }
        Present(codes.Count == 1 ? "QR code — copied to clipboard" : "QR codes", Ui.Scroll(list), 500, 360);
    }

    public static void ShowText(string text, string source)
    {
        var box = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12), FontSize = 13 };
        var words = Ui.Caption($"{text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length} words", false);
        var footer = new DockPanel { Margin = new Thickness(12, 0, 12, 12) };
        var buttons = Ui.Row(8,
            Ui.Button("Copy", () => Ui.Copy(box.Text)),
            Ui.Button("Save as Text File", () =>
            {
                var o = Output.PathFor(source, "txt", "text");
                File.WriteAllText(o, box.Text);
                Explorer.Reveal([o]);
            }, true));
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(words);
        var dock = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(box);
        Present("Recognized text — copied to clipboard", dock, 620, 480);
    }

    static void Present(string title, UIElement content, double w, double h)
    {
        var win = new Window { Title = title, Width = w, Height = h, Content = content, Background = Theme.Surface, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        win.SourceInitialized += (_, _) => Native.ApplyModernChrome(win, Theme.Dark);
        win.Show();
        win.Activate();
    }
}
