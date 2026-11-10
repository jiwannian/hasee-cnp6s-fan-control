using System.IO;
using System.Text.Json;
using System.Windows;

namespace CpuFanControl;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--watchdog")
        {
            Shutdown(WatchdogLease.Run(e.Args));
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--diagnose")
        {
            Diagnose(e.Args.Length > 1 ? e.Args[1] : Path.Combine(AppLog.DirectoryPath, "diagnostics.json"));
            return;
        }
        instance = new Mutex(true, @"Local\CpuFanControl_HASEE_CNP6S", out bool created);
        if (!created)
        {
            MessageBox.Show("CPU / GPU 风扇控制已在运行。", "CPU / GPU 风扇控制", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        SessionEnding += (_, _) => window.RestoreForSystemEvent();
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write("ui-error", args.Exception.ToString());
            window.RestoreForSystemEvent();
            args.Handled = true;
            MessageBox.Show(args.Exception.Message, "CPU / GPU 风扇控制", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        window.Show();
    }

    private void Diagnose(string path)
    {
        try
        {
            using var hardware = new NativeFanHardware();
            var data = new { time = DateTimeOffset.Now, hardware.Model, hardware.DllPath,
                readOnly = true, telemetry = hardware.Read() };
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception ex) { AppLog.Write("diagnose-error", ex.ToString()); Shutdown(1); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
