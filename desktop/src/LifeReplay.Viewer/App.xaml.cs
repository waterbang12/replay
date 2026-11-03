using System.Windows;
using LifeReplay.Core;

namespace LifeReplay.Viewer;

public partial class App : Application
{
    // One viewer per data folder, like a normal desktop app: opening it again brings the open window to the front.
    private Mutex? _single;
    private EventWaitHandle? _activate, _showCoach;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? dataDir = null;
        string? date = null;
        string? time = null;
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--data-dir") dataDir = e.Args[i + 1];
            if (e.Args[i] == "--date") date = e.Args[i + 1];
            if (e.Args[i] == "--time") time = e.Args[i + 1];
        }
        var paths = AppPaths.Resolve(dataDir);
        paths.EnsureCreated();
        Log.Init(paths.LogsDir, "viewer");

        var name = @"Local\LifeReplay.Viewer." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(paths.DataDir.ToLowerInvariant())))[..16];
        _single = new Mutex(true, name, out bool first);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".activate");
        _showCoach = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".coach");
        if (!first)
        {
            // Already open: ask that window to come to the front (and open the coach if asked), then quit.
            (e.Args.Contains("--coach") ? _showCoach : _activate).Set();
            Shutdown(0);
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show(args.Exception.Message, "LifeReplay", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try
        {
            var store = new MetadataStore(paths.DatabasePath);
            store.Migrate();
            DateOnly? day = DateOnly.TryParse(date, out var d) ? d : null;
            var window = new MainWindow(paths, store, day, time);
            MainWindow = window;
            window.Show();
            if (e.Args.Contains("--coach")) window.ShowCoach();
            ListenForSecondLaunch(window);
        }
        catch (Exception ex)
        {
            Log.Error("Viewer failed to start", ex);
            MessageBox.Show("LifeReplay could not open its database:\n" + ex.Message, "LifeReplay", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ListenForSecondLaunch(MainWindow window)
    {
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _activate!, _showCoach! };
            while (true)
            {
                int which = WaitHandle.WaitAny(handles);
                Dispatcher.BeginInvoke(() =>
                {
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Show();
                    window.Activate();
                    // Windows only lets the foreground app take focus; a brief Topmost brings it up reliably.
                    window.Topmost = true;
                    window.Topmost = false;
                    window.Focus();
                    if (which == 1) window.ShowCoach();
                });
            }
        }) { IsBackground = true, Name = "single-instance" };
        thread.Start();
    }
}
