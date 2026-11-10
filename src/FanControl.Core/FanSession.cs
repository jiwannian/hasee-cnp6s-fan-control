namespace FanControl.Core;

public interface IFanHardware
{
    FanTelemetry Read();
    void Reconnect();
    void SetPwm(int? cpuPwm, int? gpuPwm, FanTelemetry current);
    void RestoreAutomatic();
}

public interface IWatchdogLease
{
    void EnsureReady();
    void Restart();
    void Arm();
    void Disarm();
    void Beat();
}

public enum FanMode { System, Applying, Manual, ThermalProtection, Fault, ExternalOverride, Reconnecting }

public sealed record FanChannelState(FanMode Mode, int? AppliedPercent, int? RequestedPercent = null);
public sealed record FanState(FanTelemetry? Telemetry, FanChannelState Cpu, FanChannelState Gpu,
    bool OwnsControl, string? Error, bool Reconnecting = false, int RetryCount = 0);

/// <summary>Serializes both fans because the firmware writes their PWM bytes in one command.</summary>
public sealed class FanSession(IFanHardware hardware, IWatchdogLease watchdog, Func<DateTimeOffset>? clock = null)
{
    public const int ThermalLimit = 85;
    public const int ThermalRelease = 75;
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ChannelControl[] controls = [new(FanChannel.Cpu), new(FanChannel.Gpu)];
    private FanTelemetry? telemetry;
    private bool owned;
    private string? error;
    private bool reconnecting;
    private int reconnectFailures;
    private DateTimeOffset reconnectAt;

    public FanState State { get { lock (gate) return Snapshot(); } }

    public FanState Refresh()
    {
        lock (gate)
        {
            try
            {
                if (reconnecting)
                {
                    if (now() >= reconnectAt) ReconnectCore();
                    return Snapshot();
                }
                var current = hardware.Read();
                telemetry = current;
                if (owned)
                {
                    bool protect = ProtectTemperatures(current);
                    watchdog.EnsureReady();
                    if (protect) WriteTargets(current);
                    else
                    {
                        foreach (var control in controls.Where(c => c.Percent.HasValue))
                        {
                            if (Math.Abs(current.Pwm(control.Channel) - FanProtocol.ToPwm(control.Percent!.Value)) <= 1)
                            {
                                control.Verifying = false;
                                if (control.Mode == FanMode.Applying) control.Mode = FanMode.Manual;
                            }
                            else if (!control.Verifying)
                            {
                                control.Verifying = true;
                                control.VerifyBy = now().AddSeconds(3);
                            }
                            else if (now() >= control.VerifyBy)
                            {
                                RestoreCore();
                                foreach (var c in controls) c.Mode = FanMode.ExternalOverride;
                                error = "固件或其他软件覆盖了调速指令，已恢复系统控制。";
                                break;
                            }
                        }
                    }
                }
                watchdog.Beat();
            }
            catch (Exception ex)
            {
                telemetry = null;
                Recover(ex);
            }
            return Snapshot();
        }
    }

    public FanState Apply(FanChannel channel, int percent)
    {
        _ = FanProtocol.ToPwm(percent);
        var control = GetControl(channel);
        lock (gate)
        {
            if (reconnecting) throw new InvalidOperationException("正在自动重连，请稍候。");
            bool previousThermalLatch = control.ThermalLatched;
            try
            {
                // Use fresh telemetry and retain the other fan's desired target,
                // even while its PWM readback is still catching up.
                var current = hardware.Read();
                telemetry = current;
                ValidateTemperature(current, channel);
                watchdog.EnsureReady();
                bool protect = current.Temperature(channel) >= ThermalLimit ||
                    (control.ThermalLatched && current.Temperature(channel) > ThermalRelease);
                control.ThermalLatched = protect;
                control.Percent = protect ? 100 : percent;
                control.Mode = protect ? FanMode.ThermalProtection : FanMode.Applying;
                ProtectTemperatures(current);
                WriteTargets(current);
                // Only a successfully acknowledged apply becomes eligible for replay.
                // Moving a slider or a failed new request never changes this target.
                control.RequestedPercent = percent;
                watchdog.Beat();
            }
            catch (Exception ex)
            {
                // A rejected apply must not release an already latched protection.
                control.ThermalLatched |= previousThermalLatch;
                Recover(ex);
                throw new InvalidOperationException(error, ex);
            }
            return Snapshot();
        }
    }

    public FanState RestoreAutomatic(FanChannel? channel = null)
    {
        if (channel.HasValue) _ = GetControl(channel.Value);
        lock (gate)
        {
            // Cancel replay BEFORE touching the driver, even when AUTO itself fails.
            foreach (var control in controls.Where(c => !channel.HasValue || c.Channel == channel.Value))
            {
                control.RequestedPercent = null;
                control.ThermalLatched = false;
            }
            try
            {
                if (reconnecting)
                {
                    ReleaseHardware();
                    foreach (var control in controls) control.ResetActive();
                    return Snapshot();
                }
                // Restoring the last manual fan needs no working temperature sensor.
                if (!channel.HasValue || !controls.Any(c => c.Channel != channel.Value && c.Percent.HasValue))
                    RestoreCore();
                else
                {
                    var current = hardware.Read();
                    telemetry = current;
                    watchdog.EnsureReady();
                    GetControl(channel.Value).ResetActive();
                    ProtectTemperatures(current);
                    WriteTargets(current);
                }
            }
            catch (Exception ex)
            {
                Recover(ex);
                throw;
            }
            return Snapshot();
        }
    }

    private ChannelControl GetControl(FanChannel channel) => channel switch
    {
        FanChannel.Cpu => controls[0],
        FanChannel.Gpu => controls[1],
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    private static void ValidateTemperature(FanTelemetry current, FanChannel channel)
    {
        if (!FanTelemetry.ValidTemperature(current.Temperature(channel)))
            throw new InvalidDataException($"{channel.ToString().ToUpperInvariant()} 温度数据无效，无法接管风扇。");
    }

    private bool ProtectTemperatures(FanTelemetry current)
    {
        bool changed = false;
        foreach (var control in controls.Where(c => c.Percent.HasValue))
        {
            ValidateTemperature(current, control.Channel);
            if (current.Temperature(control.Channel) >= ThermalLimit && control.Mode != FanMode.ThermalProtection)
            {
                control.ThermalLatched = true;
                control.Percent = 100;
                control.Mode = FanMode.ThermalProtection;
                changed = true;
            }
        }
        return changed;
    }

    private void WriteTargets(FanTelemetry current)
    {
        watchdog.Beat();
        watchdog.Arm(); // Arm BEFORE writing either fan or changing the automatic mask.
        owned = true;
        hardware.SetPwm(controls[0].Percent is int cpu ? FanProtocol.ToPwm(cpu) : null,
            controls[1].Percent is int gpu ? FanProtocol.ToPwm(gpu) : null, current);
        foreach (var control in controls.Where(c => c.Percent.HasValue))
        {
            control.Verifying = true;
            control.VerifyBy = now().AddSeconds(3);
        }
        error = null;
    }

    private void ReleaseHardware()
    {
        hardware.RestoreAutomatic(); // Keep the lease armed if the driver rejects restoration.
        owned = false;
        watchdog.Disarm();
    }

    private void RestoreCore()
    {
        ReleaseHardware();
        foreach (var control in controls) control.Reset();
        reconnecting = false;
        reconnectFailures = 0;
        error = null;
    }

    private void ReconnectCore()
    {
        hardware.Reconnect();
        // Restart waits for the previous child to finish restoring AUTO before a new
        // generation is armed, so an old watchdog cannot overwrite resumed settings.
        watchdog.Restart();
        if (owned) ReleaseHardware();
        var current = hardware.Read();
        telemetry = current;
        foreach (var control in controls)
        {
            control.ResetActive();
            if (control.RequestedPercent is not int requested) continue;
            ValidateTemperature(current, control.Channel);
            control.ThermalLatched |= current.Temperature(control.Channel) >= ThermalLimit;
            control.Percent = control.ThermalLatched ? 100 : requested;
            control.Mode = control.ThermalLatched ? FanMode.ThermalProtection : FanMode.Applying;
        }
        if (controls.Any(c => c.Percent.HasValue))
        {
            watchdog.EnsureReady();
            WriteTargets(current);
        }
        reconnecting = false;
        reconnectFailures = 0;
        error = null;
        watchdog.Beat();
    }

    private void Recover(Exception cause)
    {
        var message = cause.Message;
        if (owned)
        {
            try { ReleaseHardware(); }
            catch (Exception restore) { message += "；恢复自动失败：" + restore.Message; }
        }
        foreach (var control in controls)
        {
            control.ResetActive();
            if (!control.RequestedPercent.HasValue) control.ThermalLatched = false;
        }
        telemetry = null;
        reconnecting = true;
        int delay = Math.Min(10, 1 << Math.Min(reconnectFailures, 4));
        reconnectAt = now().AddSeconds(delay);
        reconnectFailures = Math.Min(reconnectFailures + 1, 5);
        error = message;
    }

    private FanState Snapshot() => new(telemetry, controls[0].Snapshot(reconnecting), controls[1].Snapshot(reconnecting),
        owned, error, reconnecting, reconnectFailures);

    private sealed class ChannelControl(FanChannel channel)
    {
        public FanChannel Channel { get; } = channel;
        public FanMode Mode;
        public int? Percent;
        public int? RequestedPercent;
        public bool ThermalLatched;
        public bool Verifying;
        public DateTimeOffset VerifyBy;
        public FanChannelState Snapshot(bool reconnecting) => new(reconnecting ? FanMode.Reconnecting : Mode, Percent, RequestedPercent);
        public void ResetActive() { Mode = FanMode.System; Percent = null; Verifying = false; }
        public void Reset() { ResetActive(); RequestedPercent = null; ThermalLatched = false; }
    }
}
