using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Tangelo.Core;

namespace Tangelo.App;

/// Runs operations in the background, shows progress cards bottom-right, logs history.
static class Jobs
{
    static HudWindow? hud;
    static int running;
    public static bool Busy => running > 0;

    public static void Run(string title, IReadOnlyList<string> inputs, Func<IProgress<double>, Task<IEnumerable<string>>> work, bool reveal = true)
    {
        hud ??= new HudWindow();
        var card = hud.Add(title);
        Interlocked.Increment(ref running);
        var progress = new Progress<double>(p => card.SetProgress(p));
        _ = Task.Run(async () =>
        {
            try
            {
                var outputs = (await work(progress)).ToList();
                History.Add(new HistoryEntry { Title = title, Inputs = inputs.ToList(), Outputs = outputs });
                await App.Ui(() =>
                {
                    card.Done(outputs);
                    Feedback.Success();
                    if (reveal && outputs.Count > 0 && Settings.Current.RevealOutputs) Explorer.Reveal(outputs);
                });
            }
            catch (Exception e)
            {
                var message = e is TangeloException ? e.Message : $"{e.GetType().Name}: {e.Message}";
                History.Add(new HistoryEntry { Title = title, Inputs = inputs.ToList(), Error = message });
                await App.Ui(() => { card.Failed(message); Feedback.Failure(); });
            }
            finally { Interlocked.Decrement(ref running); }
        });
    }

    /// Runs `op` for each input and aggregates outputs, reporting per-file progress.
    public static void Batch(FileTool tool, IReadOnlyList<string> inputs, Func<string, Task<IEnumerable<string>>> op)
    {
        var title = $"{tool.Title()}: " + (inputs.Count == 1 ? Path.GetFileName(inputs[0]) : $"{inputs.Count} files");
        Run(title, inputs, async progress =>
        {
            var outs = new List<string>();
            for (int i = 0; i < inputs.Count; i++)
            {
                outs.AddRange(await op(inputs[i]));
                progress.Report((i + 1.0) / inputs.Count);
            }
            return outs;
        });
    }

    public static void Batch(FileTool tool, IReadOnlyList<string> inputs, Func<string, string> op) =>
        Batch(tool, inputs, p => Task.FromResult<IEnumerable<string>>([op(p)]));
}

static class Feedback
{
    public static void Success() { if (Settings.Current.SoundFeedback) SystemSounds.Asterisk.Play(); }
    public static void Failure() { if (Settings.Current.SoundFeedback) SystemSounds.Hand.Play(); }
}

static class Explorer
{
    /// Selects files in Explorer (one window per folder).
    public static void Reveal(IEnumerable<string> paths)
    {
        foreach (var group in paths.GroupBy(p => Path.GetDirectoryName(p)).Take(3))
        {
            var first = group.First();
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"") { UseShellExecute = true });
        }
    }

    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}

sealed class HudWindow : Window
{
    readonly StackPanel stack = new();

    public HudWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 340;
        Content = stack;
        SourceInitialized += (_, _) => Native.MakeNonActivating(this);
    }

    void Place()
    {
        var area = SystemParameters.WorkArea;
        UpdateLayout();
        Left = area.Right - Width - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    public JobCard Add(string title)
    {
        var card = new JobCard(title, this);
        stack.Children.Insert(0, card);
        while (stack.Children.Count > 4) stack.Children.RemoveAt(stack.Children.Count - 1);
        if (!IsVisible) Show();
        Dispatcher.BeginInvoke(Place, DispatcherPriority.Loaded);
        return card;
    }

    public void Remove(JobCard card)
    {
        stack.Children.Remove(card);
        if (stack.Children.Count == 0) Hide(); else Dispatcher.BeginInvoke(Place, DispatcherPriority.Loaded);
    }
}

sealed class JobCard : Border
{
    readonly HudWindow owner;
    readonly ProgressBar bar = new() { Height = 4, Maximum = 1, Foreground = Theme.Accent, Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock detail = Ui.Caption("", wrap: true);
    readonly ContentControl icon = new() { Width = 26 };
    List<string> outputs = [];

    public JobCard(string title, HudWindow owner)
    {
        this.owner = owner;
        CornerRadius = new CornerRadius(10);
        Background = Theme.SurfaceTranslucent;
        BorderBrush = Theme.Border;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(12);
        Margin = new Thickness(0, 8, 0, 0);
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.25 };
        icon.Content = new ProgressBar { IsIndeterminate = true, Width = 18, Height = 4, Foreground = Theme.Accent };
        var text = Ui.Column(2, Ui.Text(title, 13, FontWeights.SemiBold), bar, detail);
        var dock = new DockPanel();
        var close = Ui.IconButton("", () => owner.Remove(this), "Dismiss");
        close.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        dock.Children.Add(icon);
        dock.Children.Add(close);
        dock.Children.Add(text);
        Child = dock;
        Cursor = System.Windows.Input.Cursors.Hand;
        MouseLeftButtonUp += (_, _) => { if (outputs.Count > 0) Explorer.Reveal(outputs); };
        detail.Visibility = Visibility.Collapsed;
        this.close = close;
    }

    readonly Button close;

    public void SetProgress(double p) => bar.Value = p;

    public void Done(List<string> outs)
    {
        outputs = outs;
        bar.Visibility = Visibility.Collapsed;
        detail.Visibility = Visibility.Visible;
        detail.Text = outs.Count == 1 ? Path.GetFileName(outs[0]) : $"{outs.Count} files created";
        icon.Content = Ui.Glyph("", 18, new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43)));
        close.Visibility = Visibility.Visible;
        AutoDismiss(4);
    }

    public void Failed(string message)
    {
        bar.Visibility = Visibility.Collapsed;
        detail.Visibility = Visibility.Visible;
        detail.Text = message;
        detail.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x4F, 0x3A));
        icon.Content = Ui.Glyph("", 18, Theme.Accent);
        close.Visibility = Visibility.Visible;
        AutoDismiss(10);
    }

    void AutoDismiss(int seconds)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); if (!IsMouseOver) owner.Remove(this); else AutoDismiss(3); };
        t.Start();
    }
}
