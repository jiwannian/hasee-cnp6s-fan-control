using System.ComponentModel;
using System.Windows;
using FanControl.Core;
using Microsoft.Win32;

namespace CpuFanControl;

public partial class MainWindow : Window
{
    private NativeFanHardware? hardware;
    private WatchdogLease? watchdog;
    private FanSession? session;
    private readonly CancellationTokenSource cancellation = new();
    private Task? polling;
    private bool busy;
    private bool allowClose;
    private bool closing;
    private (FanMode Cpu, FanMode Gpu, int RetryCount)? lastLoggedMode;
    private bool initializationComplete;
    private bool closeRequested;

    public MainWindow() => InitializeComponent();

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            hardware = await Task.Run(() => new NativeFanHardware());
            watchdog = new WatchdogLease();
            session = new FanSession(hardware, watchdog);
            SystemEvents.PowerModeChanged += PowerModeChanged;
            polling = PollAsync(cancellation.Token);
            AppLog.Write("started", new { hardware.Model, hardware.DllPath });
        }
        catch (Exception ex)
        {
            CpuPanel.SetStatus("连接失败", true, ex.Message);
            GpuPanel.SetStatus("连接失败", true, ex.Message);
            AppLog.Write("startup-error", ex.ToString());
            MessageBox.Show(this, ex.Message, "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            initializationComplete = true;
            if (closeRequested) Close();
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var state = await Task.Run(() => session!.Refresh(), token);
                if (!token.IsCancellationRequested) Render(state);
                await Task.Delay(500, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("polling-error", ex.ToString()); RestoreForSystemEvent(); }
    }

    private void Render(FanState state)
    {
        CpuPanel.Render(state.Telemetry, state.Cpu, state.Error);
        GpuPanel.Render(state.Telemetry, state.Gpu, state.Error);
        UpdatedText.Text = state.Telemetry != null ? DateTime.Now.ToString("HH:mm:ss") : "数据不可用";
        UpdateButtons();
        var modes = (state.Cpu.Mode, state.Gpu.Mode, state.RetryCount);
        if (lastLoggedMode != modes)
        {
            AppLog.Write("state", state);
            lastLoggedMode = modes;
        }
    }

    private void UpdateButtons()
    {
        CpuPanel.UpdateButtons(session != null, busy || closing);
        GpuPanel.UpdateButtons(session != null, busy || closing);
    }

    private async void ApplyRequested(object? sender, EventArgs e)
    {
        var panel = (FanPanel)sender!;
        int percent = panel.TargetPercent;
        await Perform(() => session!.Apply(panel.Channel, percent), "apply", panel.Channel, percent);
    }

    private async void AutoRequested(object? sender, EventArgs e)
    {
        var channel = ((FanPanel)sender!).Channel;
        await Perform(() => session!.RestoreAutomatic(channel), "restore-auto", channel, null);
    }

    private async Task Perform(Func<FanState> action, string logAction, FanChannel channel, int? percent)
    {
        if (busy || session == null || closing) return;
        busy = true;
        UpdateButtons();
        try
        {
            var state = await Task.Run(action);
            Render(state);
            AppLog.Write(logAction, new { channel = channel.ToString(), percent, state });
        }
        catch (Exception ex)
        {
            Render(session.State);
            AppLog.Write(logAction + "-error", ex.ToString());
            if (!session.State.Reconnecting)
                MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            busy = false;
            UpdateButtons();
            if (closeRequested) Close();
        }
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Suspend) return;
        RestoreForSystemEvent();
    }

    public void RestoreForSystemEvent()
    {
        try
        {
            if (session != null && (session.State.OwnsControl || session.State.Reconnecting))
            {
                session.RestoreAutomatic();
                AppLog.Write("system-event-restored-auto");
            }
        }
        catch (Exception ex) { AppLog.Write("system-event-restore-error", ex.ToString()); }
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        if (busy || !initializationComplete) { closeRequested = true; return; }
        closing = true;
        UpdateButtons();
        try
        {
            if (session != null && (session.State.OwnsControl || session.State.Reconnecting))
                await Task.Run(() => session.RestoreAutomatic());
            cancellation.Cancel();
            if (polling != null) await polling;
            SystemEvents.PowerModeChanged -= PowerModeChanged;
            watchdog?.Dispose();
            hardware?.Dispose();
            cancellation.Dispose();
            AppLog.Write("closed");
            allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            closing = false;
            UpdateButtons();
            AppLog.Write("close-restore-error", ex.ToString());
            MessageBox.Show(this, ex.Message, "恢复自动控制失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
