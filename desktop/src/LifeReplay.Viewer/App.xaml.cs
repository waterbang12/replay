using System.Windows;
using LifeReplay.Core;

namespace LifeReplay.Viewer;

public partial class App : Application
{
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
        }
        catch (Exception ex)
        {
            Log.Error("Viewer failed to start", ex);
            MessageBox.Show("LifeReplay could not open its database:\n" + ex.Message, "LifeReplay", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
