using System.Windows;
using Shitie.Core;

namespace Shitie;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTests.Run(args);
        var testIndex = Array.IndexOf(args, "--test-profile");
        var testProfile = testIndex >= 0 && testIndex + 1 < args.Length;
        var root = testProfile ? Path.GetFullPath(args[testIndex + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shitie");
        Directory.CreateDirectory(root);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..20];
        using var mutex = new Mutex(true, @"Local\Shitie.Mutex." + identity, out var created);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Shitie.Show." + identity);
        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Shitie.Quit." + identity);
        if (!created) { if (args.Contains("--quit")) quitEvent.Set(); else showEvent.Set(); return 0; }
        if (args.Contains("--quit")) return 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Themes.xaml", UriKind.Relative) });
        AppController? controller = null;
        using var shutdown = new ManualResetEvent(false);
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log(root, e.Exception);
            MessageBox.Show("拾贴遇到了一个问题，详细信息已记录到本地日志。\n" + e.Exception.Message, "拾贴", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
        app.Startup += async (_, _) =>
        {
            try
            {
                controller = new(root, testProfile);
                app.MainWindow = controller.Window;
                if (!args.Contains("--background")) controller.Show(false);
                _ = Task.Run(() =>
                {
                    while (true)
                    {
                        var signal = WaitHandle.WaitAny([showEvent, quitEvent, shutdown]);
                        if (signal == 2) return;
                        if (signal == 1) { app.Dispatcher.BeginInvoke(() => controller.Exit()); return; }
                        app.Dispatcher.BeginInvoke(() => controller.Show(false));
                    }
                });
                await controller.InitializeAsync();
            }
            catch (Exception ex)
            {
                Log(root, ex);
                MessageBox.Show("拾贴未能启动：\n" + ex.Message, "拾贴", MessageBoxButton.OK, MessageBoxImage.Error);
                app.Shutdown(1);
            }
        };
        app.Exit += (_, _) => { shutdown.Set(); controller?.Dispose(); };
        var result = app.Run();
        if (created) mutex.ReleaseMutex();
        return result;
    }
    private static void Log(string root, Exception exception)
    {
        try
        {
            var path = Path.Combine(root, "error.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}\n{exception}\n\n");
        }
        catch { }
    }
}
