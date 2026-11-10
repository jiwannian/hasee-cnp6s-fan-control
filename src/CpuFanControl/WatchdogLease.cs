using System.Diagnostics;
using System.Globalization;
using System.IO;
using FanControl.Core;

namespace CpuFanControl;

internal sealed class WatchdogLease : IWatchdogLease, IDisposable
{
    private string token = "";
    private EventWaitHandle heartbeat = null!;
    private EventWaitHandle armed = null!;
    private EventWaitHandle ready = null!;
    private EventWaitHandle stop = null!;
    private Process? process;
    private bool disposed;

    public WatchdogLease() => CreateEvents();

    private void CreateEvents()
    {
        token = "Local\\CpuFanControl_" + Guid.NewGuid().ToString("N");
        heartbeat = new(false, EventResetMode.AutoReset, token + "_beat");
        armed = new(false, EventResetMode.ManualReset, token + "_armed");
        ready = new(false, EventResetMode.ManualReset, token + "_ready");
        stop = new(false, EventResetMode.ManualReset, token + "_stop");
    }

    public void EnsureReady()
    {
        if (disposed) throw new ObjectDisposedException(nameof(WatchdogLease));
        if (process != null)
        {
            if (process.HasExited || !ready.WaitOne(0))
                throw new InvalidOperationException("风扇恢复监护进程已停止，将自动重新连接。");
            return;
        }
        using var parent = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--watchdog");
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(token);
        process = Process.Start(start) ?? throw new InvalidOperationException("无法启动风扇监护进程。");
        Beat();
        if (!ready.WaitOne(TimeSpan.FromSeconds(5)) || process.HasExited)
            throw new InvalidOperationException("风扇监护进程未就绪，未接管风扇。");
    }

    public void Restart()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (process != null)
        {
            stop.Set();
            if (!process.HasExited && !process.WaitForExit(4000))
                throw new IOException("正在等待旧监护进程退出，将稍后重试。");
            process.Dispose();
            process = null;
        }
        // Never reuse armed/ready events shared with an older process generation.
        heartbeat.Dispose(); armed.Dispose(); ready.Dispose(); stop.Dispose();
        CreateEvents();
        EnsureReady();
        AppLog.Write("watchdog-restarted", new { pid = process!.Id });
    }

    public void Arm() => armed.Set();
    public void Disarm() => armed.Reset();
    public void Beat() => heartbeat.Set();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Set(); // The child restores AUTO itself if the lease is still armed.
        process?.Dispose();
        heartbeat.Dispose(); armed.Dispose(); ready.Dispose(); stop.Dispose();
    }

    public static int Run(string[] arguments)
    {
        try
        {
            if (arguments.Length != 4 || !int.TryParse(arguments[1], out int pid) ||
                !long.TryParse(arguments[2], out long ticks) ||
                !arguments[3].StartsWith("Local\\CpuFanControl_", StringComparison.Ordinal)) return 2;
            using var parent = Process.GetProcessById(pid);
            if (parent.StartTime.ToUniversalTime().Ticks != ticks) return 2;
            string token = arguments[3];
            using var beat = EventWaitHandle.OpenExisting(token + "_beat");
            using var armed = EventWaitHandle.OpenExisting(token + "_armed");
            using var ready = EventWaitHandle.OpenExisting(token + "_ready");
            using var stop = EventWaitHandle.OpenExisting(token + "_stop");
            using var hardware = new NativeFanHardware();
            ready.Set();
            var lastBeat = Stopwatch.StartNew();
            string reason;
            while (true)
            {
                if (beat.WaitOne(250)) lastBeat.Restart();
                if (parent.HasExited) { reason = "parent-exited"; break; }
                if (stop.WaitOne(0)) { reason = "shutdown"; break; }
                if (armed.WaitOne(0) && lastBeat.Elapsed > TimeSpan.FromSeconds(4))
                { reason = "heartbeat-timeout"; break; }
            }
            ready.Reset();
            if (armed.WaitOne(0))
            {
                // A failed driver acknowledgement is retried by this independent process.
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        hardware.RestoreAutomatic();
                        AppLog.Write("watchdog-restored-auto", new { reason, attempt });
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("watchdog-restore-error", new { reason, attempt, ex.Message });
                        if (attempt == 4) return 3;
                        Thread.Sleep(500);
                    }
                }
            }
            return 0;
        }
        catch (Exception ex) { AppLog.Write("watchdog-error", ex.Message); return 2; }
    }
}
