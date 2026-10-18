using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tangelo.Core;
using Forms = System.Windows.Forms;

namespace Tangelo.App;

static class Tray
{
    static Forms.NotifyIcon? icon;

    public static void Install()
    {
        var stream = typeof(Tray).Assembly.GetManifestResourceStream("Tangelo.App.Assets.tray.ico");
        icon = new Forms.NotifyIcon
        {
            Icon = stream != null ? new System.Drawing.Icon(stream) : System.Drawing.SystemIcons.Application,
            Text = "Tangelo",
            Visible = true,
        };
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) TrayPopup.Toggle();
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("New Float", null, (_, _) => FloatManager.NewFloat([], new Point(SystemParameters.WorkArea.Right - 300, SystemParameters.WorkArea.Bottom - 360)));
        menu.Items.Add("Use Explorer Selection", null, (_, _) => { var s = ExplorerSelection.Get(); if (s.Count > 0) TrayPopup.ShowFor(s); });
        menu.Items.Add("Settings", null, (_, _) => SettingsWindow.ShowSingle());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Tangelo", null, (_, _) => App.Quit());
        icon.ContextMenuStrip = menu;
    }

    public static void Remove()
    {
        if (icon == null) return;
        icon.Visible = false;
        icon.Dispose();
        icon = null;
    }
}

/// The tray flyout: drop zone, chooser, recent operations.
sealed class TrayPopup : Window
{
    static TrayPopup? instance;
    readonly ContentControl body = new();
    readonly StackPanel recent = new();
    List<string> files = [];
    WheelKind tab = WheelKind.Conversions;

    public static void Toggle()
    {
        instance ??= new TrayPopup();
        if (instance.IsVisible) { instance.Hide(); return; }
        instance.files = [];
        instance.Present();
    }

    public static void ShowFor(IReadOnlyList<string> paths)
    {
        instance ??= new TrayPopup();
        instance.files = paths.ToList();
        instance.Present();
    }

    TrayPopup()
    {
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Theme.Surface;
        AllowDrop = true;
        Deactivated += (_, _) => Hide();
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] f) { files = f.ToList(); Render(); } };

        var header = Ui.Row(8, Ui.Brand(22), Ui.Text("Tangelo", 15, FontWeights.SemiBold));
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = Ui.Row(0,
            Ui.IconButton("", () => { Hide(); SettingsWindow.ShowSingle(); }, "Settings"),
            Ui.IconButton("", App.Quit, "Quit Tangelo"));
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(right);
        footer.Children.Add(Ui.Row(6,
            Ui.Button(Ui.Row(6, Ui.Glyph("", 13), Ui.Text("New Float", 12)), () => { Hide(); FloatManager.NewFloat([], new Point(Left - 280, Top)); }),
            Ui.Button(Ui.Row(6, Ui.Glyph("", 13), Ui.Text("Explorer Selection", 12)), UseExplorerSelection)));

        Content = new Border
        {
            Padding = new Thickness(16), BorderBrush = Theme.Border, BorderThickness = new Thickness(1),
            Child = Ui.Column(12, header, body, recent, footer),
        };
        SourceInitialized += (_, _) => Native.ApplyModernChrome(this, Theme.Dark);
        this.OnKey(System.Windows.Input.Key.Escape, Hide);
        History.Changed += () => Dispatcher.BeginInvoke(RenderRecent);
    }

    void Present()
    {
        Render();
        RenderRecent();
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - ActualHeight - 12;
        Activate();
    }

    void UseExplorerSelection()
    {
        var sel = ExplorerSelection.Get();
        if (sel.Count == 0) { App.Alert("Nothing selected", "Select files in a File Explorer window first."); return; }
        files = sel;
        Render();
    }

    void Render()
    {
        if (files.Count == 0)
        {
            var zone = new Border
            {
                BorderBrush = Theme.Subtle, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 22, 16, 22),
                Child = Ui.Column(6,
                    Ui.Glyph("", 26, Theme.Subtle),
                    Center(Ui.Text("Drop files to convert or edit", 13, FontWeights.SemiBold)),
                    Center(Ui.Caption($"Or while dragging anywhere, tap {Settings.Current.ConversionsModifier} to convert · {Settings.Current.ToolsModifier} for tools · {Settings.Current.ActionsModifier} for actions. Shake to Float."))),
            };
            body.Content = zone;
            return;
        }
        var title = new DockPanel();
        var clear = Ui.IconButton("", () => { files = []; Render(); }, "Clear");
        DockPanel.SetDock(clear, Dock.Right);
        title.Children.Add(clear);
        title.Children.Add(Ui.Text(files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} files", 13, FontWeights.SemiBold));
        var tabs = Ui.Segmented([WheelKind.Conversions, WheelKind.Tools, WheelKind.Actions], tab, k => k == WheelKind.Conversions ? "Convert" : k.ToString(), k => { tab = k; Render(); });
        var grid = new UniformGrid4();
        var items = Actions.Items(tab, files);
        foreach (var item in items)
        {
            var b = new Button { Height = 58, Margin = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Stretch, Content = Ui.Column(3, Ui.Glyph(item.Glyph, 16), Center(Ui.Text(item.Title, 10.5, FontWeights.SemiBold))) };
            b.Click += (_, _) => { var f = files; files = []; Hide(); item.Run(f); };
            grid.Children.Add(b);
        }
        body.Content = Ui.Column(8, title, tabs, items.Count == 0 ? Ui.Caption("No options for these files.") : grid);
    }

    static TextBlock Center(TextBlock t) { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; return t; }

    void RenderRecent()
    {
        recent.Children.Clear();
        var entries = History.Entries.Take(5).ToList();
        if (entries.Count == 0) return;
        recent.Children.Add(Ui.Text("Recent", 11, FontWeights.SemiBold, Theme.Subtle));
        foreach (var e in entries)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0), ToolTip = e.Error ?? string.Join(", ", e.Outputs.Select(Path.GetFileName)) };
            var ok = e.Error == null;
            var glyph = Ui.Glyph(ok ? "" : "", 12, ok ? new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43)) : Theme.Accent);
            glyph.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
            if (ok)
            {
                var actions = Ui.Row(0,
                    Ui.IconButton("", () => Explorer.Reveal(e.Outputs), "Show in Explorer"),
                    Ui.IconButton("", () => Undo(e), "Undo — move outputs to the Recycle Bin"));
                DockPanel.SetDock(actions, Dock.Right);
                row.Children.Add(actions);
            }
            row.Children.Add(Ui.Text(e.Title, 12));
            recent.Children.Add(row);
        }
    }

    public static void Undo(HistoryEntry e)
    {
        foreach (var p in e.Outputs)
        {
            try
            {
                if (File.Exists(p)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else if (Directory.Exists(p)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch { }
        }
        History.Remove(e.Id);
    }
}

/// A wrap grid with four equal columns.
sealed class UniformGrid4 : System.Windows.Controls.Primitives.UniformGrid
{
    public UniformGrid4() { Columns = 4; }
}

static class ExplorerSelection
{
    /// Selected items in the front-most File Explorer window.
    public static List<string> Get()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return [];
            dynamic shell = Activator.CreateInstance(type)!;
            var order = ZOrder();
            List<string>? best = null;
            int bestRank = int.MaxValue;
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    IntPtr hwnd = new((long)window.HWND);
                    var rank = order.TryGetValue(hwnd, out var r) ? r : int.MaxValue - 1;
                    var items = new List<string>();
                    foreach (dynamic item in window.Document.SelectedItems()) items.Add((string)item.Path);
                    if (items.Count > 0 && rank < bestRank) { best = items; bestRank = rank; }
                }
                catch { }
            }
            return best ?? [];
        }
        catch { return []; }
    }

    static Dictionary<IntPtr, int> ZOrder()
    {
        var map = new Dictionary<IntPtr, int>();
        int i = 0;
        for (var h = Native.GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && i < 5000; h = Native.GetWindow(h, Native.GW_HWNDNEXT)) map[h] = i++;
        return map;
    }
}
