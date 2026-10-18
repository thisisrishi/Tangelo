using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using Tangelo.Core;

namespace Tangelo.App;

sealed class App : Application
{
    const string PipeName = "Tangelo.Instance.v1";
    static Mutex? mutex;
    bool demoMode;

    /// Hidden owner window used for share sheets and dialogs.
    public static Window Host { get; private set; } = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
        {
            Platform.Windows.WindowsServices.Register();
            var dir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "tangelo-selftest");
            return SelfTest.Run(dir, Console.Out).GetAwaiter().GetResult() == 0 ? 0 : 1;
        }

        var demo = args.Length > 0 && args[0] == "--demo";
        var files = demo ? [] : args.Where(a => File.Exists(a) || Directory.Exists(a)).Select(Path.GetFullPath).ToArray();
        mutex = new Mutex(true, "Tangelo.SingleInstance", out var first);
        if (!first && !demo)
        {
            // Forward files (e.g. from the Explorer menu) to the running instance.
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(1500);
                using var w = new StreamWriter(client);
                w.Write(string.Join("\n", files));
            }
            catch { }
            return 0;
        }

        var app = new App { demoMode = demo };
        app.Startup += (_, _) => app.Start(files, demo ? args[1..] : null);
        return app.Run();
    }

    App()
    {
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, e) =>
        {
            if (demoMode)
            {
                // Never block unattended runs (CI screenshots) on a dialog.
                Console.Error.WriteLine(e.Exception);
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "tangelo-demo-errors.log"), e.Exception + Environment.NewLine);
                Environment.Exit(3);
            }
            Alert("Something went wrong", e.Exception.Message);
            e.Handled = true;
        };
    }

    void Start(string[] files, string[]? demo)
    {
        Platform.Windows.WindowsServices.Register();
        Host = new Window { Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
        Host.Show();
        Host.Hide();

        if (demo != null) { Demo.Run(demo); return; }

        Tray.Install();
        DragMonitor.Start(Dispatcher);
        FloatManager.Restore();
        Integration.Sync();
        ListenForInstances();
        if (!Settings.Current.DidOnboard) WelcomeWindow.ShowOnce();
        if (files.Length > 0) TrayPopup.ShowFor(files);
    }

    void ListenForInstances()
    {
        _ = Task.Run(async () =>
        {
            var pending = new List<string>();
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var r = new StreamReader(server);
                    var text = await r.ReadToEndAsync();
                    var paths = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
                    lock (pending) pending.AddRange(paths);
                    // Explorer launches one process per selected file; gather them briefly.
                    await Task.Delay(350);
                    List<string> batch;
                    lock (pending) { batch = pending.Distinct().ToList(); pending.Clear(); }
                    await Ui(() => { if (batch.Count > 0) TrayPopup.ShowFor(batch); else TrayPopup.Toggle(); });
                }
                catch { await Task.Delay(500); }
            }
        });
    }

    public static Task Ui(Action a) => Current.Dispatcher.InvokeAsync(a, DispatcherPriority.Normal).Task;

    public static void Alert(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Quit()
    {
        FloatManager.Save();
        DragMonitor.Stop();
        Tray.Remove();
        Current.Shutdown();
    }
}
