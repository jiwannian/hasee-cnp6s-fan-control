using FanControl.Core;

int passed = 0, failed = 0;
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

Test("PWM endpoints", () => { Equal(0, FanProtocol.ToPwm(0)); Equal(255, FanProtocol.ToPwm(100)); });
Test("PWM rounding", () => { Equal(128, FanProtocol.ToPwm(50)); Equal(64, FanProtocol.ToPwm(25)); });
Test("All 101 slider values round-trip", () =>
{
    for (int i = 0; i <= 100; i++) Equal(i, FanProtocol.ToPercent(FanProtocol.ToPwm(i)));
});
Test("Invalid percentages rejected", () => { Throws<ArgumentOutOfRangeException>(() => FanProtocol.ToPwm(-1)); Throws<ArgumentOutOfRangeException>(() => FanProtocol.ToPwm(101)); });
Test("Invalid PWM rejected", () => { Throws<ArgumentOutOfRangeException>(() => FanProtocol.ToPercent(256)); Throws<ArgumentOutOfRangeException>(() => FanProtocol.ToPercent(-1)); });
Test("RPM matches OEM conversion", () => Equal(3711, FanProtocol.ToRpm(581)));
Test("Stopped tachometers", () => { Equal(0, FanProtocol.ToRpm(0)); Equal(0, FanProtocol.ToRpm(65535)); });
Test("CPU command preserves other fans", () => Equal(0x007B80FFu, FanProtocol.PackPwm(255, null, new(128, 50, 3700, 128, 50, 3700, 123))));
Test("Firmware auto masks", () => { Equal(14, FanProtocol.AutomaticMask(true, false)); Equal(13, FanProtocol.AutomaticMask(false, true)); Equal(12, FanProtocol.AutomaticMask(true, true)); Equal(15, FanProtocol.AutomaticMask(false, false)); });
Test("Telemetry buffer decode", () =>
{
    byte[] b = new byte[256]; b[2] = 2; b[3] = 69; b[16] = 128; b[18] = 49; b[19] = 100; b[21] = 55;
    var t = FanTelemetry.Decode(b); Equal(3711, t.CpuRpm); Equal(50, t.CpuPercent); Equal(49, t.CpuTemperature); Equal(100, t.GpuPwm);
});
Test("Truncated and invalid sensor data rejected", () =>
{
    Throws<InvalidDataException>(() => FanTelemetry.Decode(new byte[20]));
    Throws<InvalidDataException>(() => FanTelemetry.Decode(new byte[256]));
    var b = new byte[256]; b[18] = 255; Throws<InvalidDataException>(() => FanTelemetry.Decode(b));
});
Test("Startup is read-only", () => { var f = new Fixture(); f.Session.Refresh(); Equal(0, f.Hardware.Writes); Equal(0, f.Hardware.Restores); Equal(FanMode.System, f.Session.State.Cpu.Mode); });
Test("Watchdog armed before write", () => { var f = new Fixture(); f.Hardware.BeforeWrite = () => Equal(true, f.Watchdog.Armed); f.Session.Apply(FanChannel.Cpu, 50); Equal(1, f.Hardware.Writes); });
Test("Zero accepted and confirmed", () => { var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 0); f.Session.Refresh(); Equal(0, f.Hardware.Current.CpuPwm); Equal(FanMode.Manual, f.Session.State.Cpu.Mode); });
Test("Full speed accepted and confirmed", () => { var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 100); f.Session.Refresh(); Equal(255, f.Hardware.Current.CpuPwm); Equal(100, f.Session.State.Cpu.AppliedPercent); });
Test("Apply requires fresh read", () => { var f = new Fixture(); f.Session.Refresh(); f.Hardware.ReadFails = true; Throws<InvalidOperationException>(() => f.Session.Apply(FanChannel.Cpu, 0)); Equal(0, f.Hardware.Writes); });
Test("Watchdog failure prevents write", () => { var f = new Fixture(); f.Watchdog.Ready = false; Throws<InvalidOperationException>(() => f.Session.Apply(FanChannel.Cpu, 0)); Equal(0, f.Hardware.Writes); });
Test("Partial write failure restores AUTO", () => { var f = new Fixture(); f.Hardware.WriteFails = true; Throws<InvalidOperationException>(() => f.Session.Apply(FanChannel.Cpu, 0)); Equal(1, f.Hardware.Restores); Equal(false, f.Watchdog.Armed); Equal(false, f.Session.State.OwnsControl); });
Test("High temperature overrides requested zero", () => { var f = new Fixture(); f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 85 }; f.Session.Apply(FanChannel.Cpu, 0); Equal(255, f.Hardware.Current.CpuPwm); Equal(FanMode.ThermalProtection, f.Session.State.Cpu.Mode); });
Test("High temperature while manual forces 100", () => { var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 0); f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 86 }; f.Session.Refresh(); Equal(255, f.Hardware.Current.CpuPwm); Equal(FanMode.ThermalProtection, f.Session.State.Cpu.Mode); });
Test("Protection remains latched after cooling", () => { var f = new Fixture(); f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 90 }; f.Session.Apply(FanChannel.Cpu, 0); f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 60 }; f.Session.Refresh(); Equal(FanMode.ThermalProtection, f.Session.State.Cpu.Mode); Equal(255, f.Hardware.Current.CpuPwm); });
Test("Protection cannot be released above 75 C", () => { var f = new Fixture(); f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 90 }; f.Session.Apply(FanChannel.Cpu, 0); f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 76 }; f.Session.Apply(FanChannel.Cpu, 0); Equal(255, f.Hardware.Current.CpuPwm); });
Test("Explicit apply after cooling releases protection", () => { var f = new Fixture(); f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 90 }; f.Session.Apply(FanChannel.Cpu, 0); f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 75 }; f.Session.Apply(FanChannel.Cpu, 25); f.Session.Refresh(); Equal(64, f.Hardware.Current.CpuPwm); Equal(FanMode.Manual, f.Session.State.Cpu.Mode); });
Test("Read failure restores AUTO and schedules reconnect", () => { var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 0); f.Hardware.ReadFails = true; f.Session.Refresh(); Equal(1, f.Hardware.Restores); Equal(false, f.Session.State.OwnsControl); Equal(FanMode.Reconnecting, f.Session.State.Cpu.Mode); Equal<FanTelemetry?>(null, f.Session.State.Telemetry); });
Test("Initial readback mismatch rolls back", () => { var f = new Fixture(); f.Hardware.AcceptWrites = false; f.Session.Apply(FanChannel.Cpu, 0); f.Now = f.Now.AddSeconds(4); f.Session.Refresh(); Equal(1, f.Hardware.Restores); Equal(FanMode.ExternalOverride, f.Session.State.Cpu.Mode); });
Test("Late external controller override rolls back", () => { var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 0); f.Session.Refresh(); f.Hardware.Current = f.Hardware.Current with { CpuPwm = 128 }; f.Session.Refresh(); f.Now = f.Now.AddSeconds(4); f.Session.Refresh(); Equal(FanMode.ExternalOverride, f.Session.State.Cpu.Mode); Equal(1, f.Hardware.Restores); });
Test("AUTO disarms only after acknowledgement", () => { var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 0); f.Hardware.RestoreFails = true; Throws<IOException>(() => f.Session.RestoreAutomatic()); Equal(true, f.Watchdog.Armed); Equal(true, f.Session.State.OwnsControl); f.Hardware.RestoreFails = false; f.Session.RestoreAutomatic(); Equal(false, f.Watchdog.Armed); });
Test("Failed restoration retries without resuming canceled target", () => { var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 0); f.Hardware.RestoreFails = true; Throws<IOException>(() => f.Session.RestoreAutomatic()); f.Hardware.RestoreFails = false; f.Now = f.Now.AddSeconds(1); f.Session.Refresh(); Equal(false, f.Session.State.OwnsControl); Equal(false, f.Watchdog.Armed); Equal<int?>(null, f.Session.State.Cpu.RequestedPercent); });
Test("Invalid request never arms or writes", () => { var f = new Fixture(); Throws<ArgumentOutOfRangeException>(() => f.Session.Apply(FanChannel.Cpu, 101)); Equal(false, f.Watchdog.Armed); Equal(0, f.Hardware.Writes); });
Test("Automatic clears applied target", () => { var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 50); f.Session.RestoreAutomatic(); Equal<int?>(null, f.Session.State.Cpu.AppliedPercent); Equal(FanMode.System, f.Session.State.Cpu.Mode); });

Test("GPU command preserves CPU and third fan", () => Equal(0x007BFF80u,
    FanProtocol.PackPwm(null, 255, new(128, 50, 3700, 64, 50, 3700, 123))));
Test("Dual command packs distinct targets", () => Equal(0x007BFF00u,
    FanProtocol.PackPwm(0, 255, new(128, 50, 3700, 64, 50, 3700, 123))));
Test("Invalid GPU PWM rejected", () => Throws<ArgumentOutOfRangeException>(() =>
    FanProtocol.PackPwm(null, 256, new(128, 50, 3700, 128, 50, 3700, 0))));
Test("GPU telemetry decode", () =>
{
    byte[] b = new byte[256]; b[4] = 2; b[5] = 69; b[18] = 49; b[19] = 255; b[21] = 55;
    var t = FanTelemetry.Decode(b); Equal(3711, t.GpuRpm); Equal(100, t.GpuPercent); Equal(55, t.GpuTemperature);
});
Test("GPU startup stays automatic", () =>
{
    var f = new Fixture(); f.Session.Refresh(); Equal(FanMode.System, f.Session.State.Gpu.Mode);
    Equal(15, f.Hardware.AutomaticMask); Equal(0, f.Hardware.Writes);
});
Test("GPU zero accepted and confirmed", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Gpu, 0); f.Session.Refresh();
    Equal(0, f.Hardware.Current.GpuPwm); Equal(128, f.Hardware.Current.CpuPwm);
    Equal(13, f.Hardware.AutomaticMask); Equal(FanMode.Manual, f.Session.State.Gpu.Mode);
    Equal(FanMode.System, f.Session.State.Cpu.Mode);
});
Test("GPU full speed accepted", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Gpu, 100); f.Session.Refresh();
    Equal(255, f.Hardware.Current.GpuPwm); Equal(100, f.Session.State.Gpu.AppliedPercent);
});
Test("Independent CPU and GPU targets survive consecutive applies", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 33); f.Session.Apply(FanChannel.Gpu, 75); f.Session.Refresh();
    Equal(84, f.Hardware.Current.CpuPwm); Equal(191, f.Hardware.Current.GpuPwm);
    Equal(12, f.Hardware.AutomaticMask); Equal(FanMode.Manual, f.Session.State.Cpu.Mode);
    Equal(FanMode.Manual, f.Session.State.Gpu.Mode);
});
Test("Other requested target wins over lagging readback", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 33);
    f.Hardware.Current = f.Hardware.Current with { CpuPwm = 128 };
    f.Session.Apply(FanChannel.Gpu, 75); Equal(84, f.Hardware.LastCpuPwm);
});
Test("CPU auto leaves GPU manual and watchdog armed", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 33); f.Session.Apply(FanChannel.Gpu, 75);
    f.Session.RestoreAutomatic(FanChannel.Cpu); f.Session.Refresh();
    Equal(13, f.Hardware.AutomaticMask); Equal<int?>(null, f.Session.State.Cpu.AppliedPercent);
    Equal(191, f.Hardware.Current.GpuPwm); Equal(FanMode.Manual, f.Session.State.Gpu.Mode);
    Equal(true, f.Session.State.OwnsControl); Equal(true, f.Watchdog.Armed);
});
Test("GPU auto leaves CPU manual and watchdog armed", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Gpu, 75); f.Session.Apply(FanChannel.Cpu, 33);
    f.Session.RestoreAutomatic(FanChannel.Gpu); f.Session.Refresh();
    Equal(14, f.Hardware.AutomaticMask); Equal<int?>(null, f.Session.State.Gpu.AppliedPercent);
    Equal(84, f.Hardware.Current.CpuPwm); Equal(FanMode.Manual, f.Session.State.Cpu.Mode);
    Equal(true, f.Watchdog.Armed);
});
Test("Last GPU auto disarms watchdog", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Gpu, 75); f.Session.RestoreAutomatic(FanChannel.Gpu);
    Equal(15, f.Hardware.AutomaticMask); Equal(false, f.Session.State.OwnsControl); Equal(false, f.Watchdog.Armed);
});
Test("All auto clears both targets", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 25); f.Session.Apply(FanChannel.Gpu, 75);
    f.Session.RestoreAutomatic(); Equal<int?>(null, f.Session.State.Cpu.AppliedPercent);
    Equal<int?>(null, f.Session.State.Gpu.AppliedPercent); Equal(15, f.Hardware.AutomaticMask);
});
Test("GPU heat overrides zero and preserves CPU", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 33);
    f.Hardware.Current = f.Hardware.Current with { GpuTemperature = 85 };
    f.Session.Apply(FanChannel.Gpu, 0); Equal(255, f.Hardware.Current.GpuPwm);
    Equal(84, f.Hardware.Current.CpuPwm); Equal(FanMode.ThermalProtection, f.Session.State.Gpu.Mode);
});
Test("GPU heating during manual forces full speed", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Gpu, 0);
    f.Hardware.Current = f.Hardware.Current with { GpuTemperature = 86 }; f.Session.Refresh();
    Equal(255, f.Hardware.Current.GpuPwm); Equal(FanMode.ThermalProtection, f.Session.State.Gpu.Mode);
});
Test("GPU protection latches until explicit cooled apply", () =>
{
    var f = new Fixture(); f.Hardware.Current = f.Hardware.Current with { GpuTemperature = 85 };
    f.Session.Apply(FanChannel.Gpu, 0); f.Hardware.Current = f.Hardware.Current with { GpuTemperature = 76 };
    f.Session.Apply(FanChannel.Gpu, 25); Equal(255, f.Hardware.Current.GpuPwm);
    f.Hardware.Current = f.Hardware.Current with { GpuTemperature = 60 }; f.Session.Refresh();
    Equal(255, f.Hardware.Current.GpuPwm); f.Session.Apply(FanChannel.Gpu, 25); f.Session.Refresh();
    Equal(64, f.Hardware.Current.GpuPwm); Equal(FanMode.Manual, f.Session.State.Gpu.Mode);
});
Test("Both hot fans protect in one write", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 25); f.Session.Apply(FanChannel.Gpu, 25);
    int before = f.Hardware.Writes;
    f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 85, GpuTemperature = 85 };
    f.Session.Refresh(); Equal(before + 1, f.Hardware.Writes);
    Equal(255, f.Hardware.Current.CpuPwm); Equal(255, f.Hardware.Current.GpuPwm);
});
Test("Applying GPU also protects already manual hot CPU", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 0);
    f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 85 };
    f.Session.Apply(FanChannel.Gpu, 25); Equal(255, f.Hardware.Current.CpuPwm);
    Equal(64, f.Hardware.Current.GpuPwm);
});
Test("Unavailable automatic GPU does not block CPU", () =>
{
    var f = new Fixture(); f.Hardware.Current = f.Hardware.Current with { GpuTemperature = 0 };
    f.Session.Apply(FanChannel.Cpu, 50); f.Session.Refresh(); Equal(FanMode.Manual, f.Session.State.Cpu.Mode);
    Equal(FanMode.System, f.Session.State.Gpu.Mode);
});
Test("Unavailable GPU rejects apply without write", () =>
{
    var f = new Fixture(); f.Hardware.Current = f.Hardware.Current with { GpuTemperature = 0 };
    Throws<InvalidOperationException>(() => f.Session.Apply(FanChannel.Gpu, 0));
    Equal(0, f.Hardware.Writes); Equal(false, f.Watchdog.Armed);
});
Test("GPU sensor loss restores both manual fans", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 25); f.Session.Apply(FanChannel.Gpu, 0);
    f.Hardware.Current = f.Hardware.Current with { GpuTemperature = 255 }; f.Session.Refresh();
    Equal(1, f.Hardware.Restores); Equal(15, f.Hardware.AutomaticMask); Equal(false, f.Watchdog.Armed);
});
Test("GPU write failure restores existing CPU control", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 25); f.Hardware.WriteFails = true;
    Throws<InvalidOperationException>(() => f.Session.Apply(FanChannel.Gpu, 0));
    Equal(1, f.Hardware.Restores); Equal<int?>(null, f.Session.State.Cpu.AppliedPercent);
    Equal<int?>(null, f.Session.State.Gpu.AppliedPercent);
});
Test("GPU external override restores all automatic", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 25); f.Session.Apply(FanChannel.Gpu, 0); f.Session.Refresh();
    f.Hardware.Current = f.Hardware.Current with { GpuPwm = 128 }; f.Session.Refresh();
    f.Now = f.Now.AddSeconds(4); f.Session.Refresh();
    Equal(15, f.Hardware.AutomaticMask); Equal(FanMode.ExternalOverride, f.Session.State.Gpu.Mode);
});
Test("Restoring GPU with dead sensor still works", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Gpu, 0); f.Hardware.ReadFails = true;
    f.Session.RestoreAutomatic(FanChannel.Gpu); Equal(15, f.Hardware.AutomaticMask); Equal(false, f.Watchdog.Armed);
});
Test("Failed partial auto falls back to all auto", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 25); f.Session.Apply(FanChannel.Gpu, 75);
    f.Hardware.WriteFails = true; Throws<IOException>(() => f.Session.RestoreAutomatic(FanChannel.Gpu));
    Equal(15, f.Hardware.AutomaticMask); Equal(false, f.Watchdog.Armed);
});
Test("Invalid fan channel never writes", () =>
{
    var f = new Fixture(); Throws<ArgumentOutOfRangeException>(() => f.Session.Apply((FanChannel)7, 50));
    Throws<ArgumentOutOfRangeException>(() => f.Session.RestoreAutomatic((FanChannel)7)); Equal(0, f.Hardware.Writes);
});

Test("Startup telemetry failure reconnects to system without writing PWM", () =>
{
    var f = new Fixture(); f.Hardware.ReadFails = true; f.Session.Refresh();
    Equal(true, f.Session.State.Reconnecting);
    f.Hardware.ReadFails = false; f.Now = f.Now.AddSeconds(1); f.Session.Refresh();
    Equal(FanMode.System, f.Session.State.Cpu.Mode); Equal(false, f.Session.State.Reconnecting);
    Equal<string?>(null, f.Session.State.Error); Equal(0, f.Hardware.Writes); Equal(0, f.Hardware.Restores);
});
Test("Reconnect respects increasing retry intervals capped at ten seconds", () =>
{
    var f = new Fixture(); f.Hardware.ReadFails = true; f.Session.Refresh();
    foreach (int delay in new[] { 1, 2, 4, 8, 10, 10 })
    {
        int before = f.Hardware.Reconnects;
        f.Now = f.Now.AddSeconds(delay - 0.1); f.Session.Refresh(); Equal(before, f.Hardware.Reconnects);
        f.Now = f.Now.AddSeconds(0.1); f.Session.Refresh(); Equal(before + 1, f.Hardware.Reconnects);
    }
    Equal(0, f.Hardware.Writes);
});
Test("Recovered sensors automatically resume independent CPU and GPU targets", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 0); f.Session.Apply(FanChannel.Gpu, 100);
    f.Hardware.ReadFails = true; f.Session.Refresh(); Equal(15, f.Hardware.AutomaticMask);
    f.Hardware.ReadFails = false; f.Now = f.Now.AddSeconds(1); f.Session.Refresh(); f.Session.Refresh();
    Equal(0, f.Hardware.Current.CpuPwm); Equal(255, f.Hardware.Current.GpuPwm);
    Equal(FanMode.Manual, f.Session.State.Cpu.Mode); Equal(FanMode.Manual, f.Session.State.Gpu.Mode);
    Equal(12, f.Hardware.AutomaticMask); Equal(1, f.Watchdog.Restarts); Equal(true, f.Watchdog.Armed);
});
Test("Dead watchdog is replaced before resuming manual control", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 33); f.Watchdog.Ready = false;
    f.Session.Refresh(); Equal(15, f.Hardware.AutomaticMask);
    f.Hardware.BeforeWrite = () => { Equal(1, f.Watchdog.Restarts); Equal(true, f.Watchdog.Ready); Equal(true, f.Watchdog.Armed); };
    f.Now = f.Now.AddSeconds(1); f.Session.Refresh(); f.Session.Refresh();
    Equal(84, f.Hardware.Current.CpuPwm); Equal(FanMode.Manual, f.Session.State.Cpu.Mode);
});
Test("Restart failure backs off and eventually recovers", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Gpu, 75); f.Watchdog.Ready = false; f.Session.Refresh();
    f.Watchdog.RestartFails = true; f.Now = f.Now.AddSeconds(1); f.Session.Refresh();
    Equal(true, f.Session.State.Reconnecting); Equal(15, f.Hardware.AutomaticMask);
    f.Watchdog.RestartFails = false; f.Now = f.Now.AddSeconds(2); f.Session.Refresh(); f.Session.Refresh();
    Equal(191, f.Hardware.Current.GpuPwm); Equal(FanMode.Manual, f.Session.State.Gpu.Mode);
});
Test("Driver reconnect failure preserves successful target until available", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 25); f.Hardware.ReadFails = true; f.Session.Refresh();
    f.Hardware.ReadFails = false; f.Hardware.ReconnectFails = true; f.Now = f.Now.AddSeconds(1); f.Session.Refresh();
    Equal(true, f.Session.State.Reconnecting); Equal(1, f.Hardware.Writes);
    f.Hardware.ReconnectFails = false; f.Now = f.Now.AddSeconds(2); f.Session.Refresh();
    Equal(64, f.Hardware.Current.CpuPwm); Equal(false, f.Session.State.Reconnecting);
});
Test("Canceling CPU while disconnected resumes only GPU", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 0); f.Session.Apply(FanChannel.Gpu, 75);
    f.Hardware.ReadFails = true; f.Session.Refresh(); f.Session.RestoreAutomatic(FanChannel.Cpu);
    f.Hardware.ReadFails = false; f.Now = f.Now.AddSeconds(1); f.Session.Refresh(); f.Session.Refresh();
    Equal(13, f.Hardware.AutomaticMask); Equal(FanMode.System, f.Session.State.Cpu.Mode);
    Equal(191, f.Hardware.Current.GpuPwm); Equal<int?>(null, f.Session.State.Cpu.RequestedPercent);
});
Test("All AUTO during disconnect cancels every pending manual target", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 0); f.Session.Apply(FanChannel.Gpu, 100);
    f.Hardware.ReadFails = true; f.Session.Refresh(); f.Session.RestoreAutomatic();
    f.Hardware.ReadFails = false; f.Now = f.Now.AddSeconds(1); f.Session.Refresh();
    Equal(2, f.Hardware.Writes); Equal(false, f.Session.State.OwnsControl);
    Equal(FanMode.System, f.Session.State.Cpu.Mode); Equal(FanMode.System, f.Session.State.Gpu.Mode);
});
Test("Failed AUTO still cancels pending replay", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 0); f.Hardware.ReadFails = true; f.Session.Refresh();
    f.Hardware.RestoreFails = true; Throws<IOException>(() => f.Session.RestoreAutomatic());
    f.Hardware.RestoreFails = false; f.Hardware.ReadFails = false; f.Now = f.Now.AddSeconds(2); f.Session.Refresh();
    Equal(1, f.Hardware.Writes); Equal(FanMode.System, f.Session.State.Cpu.Mode);
});
Test("Close or suspend restoration does not revive manual target on next refresh", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 0); f.Hardware.ReadFails = true; f.Session.Refresh();
    f.Session.RestoreAutomatic(); f.Hardware.ReadFails = false; f.Now = f.Now.AddMinutes(1);
    f.Session.Refresh(); f.Session.Refresh(); Equal(1, f.Hardware.Writes); Equal(false, f.Watchdog.Armed);
});
Test("Hot CPU on reconnection resumes at full speed", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 0); f.Hardware.ReadFails = true; f.Session.Refresh();
    f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 85 }; f.Hardware.ReadFails = false;
    f.Now = f.Now.AddSeconds(1); f.Session.Refresh(); Equal(255, f.Hardware.Current.CpuPwm);
    Equal(FanMode.ThermalProtection, f.Session.State.Cpu.Mode);
});
Test("Thermal latch survives disconnect and cooling", () =>
{
    var f = new Fixture(); f.Hardware.Current = f.Hardware.Current with { GpuTemperature = 90 };
    f.Session.Apply(FanChannel.Gpu, 0); f.Hardware.ReadFails = true; f.Session.Refresh();
    f.Hardware.Current = f.Hardware.Current with { GpuTemperature = 60 }; f.Hardware.ReadFails = false;
    f.Now = f.Now.AddSeconds(1); f.Session.Refresh(); Equal(255, f.Hardware.Current.GpuPwm);
    Equal(FanMode.ThermalProtection, f.Session.State.Gpu.Mode);
    f.Session.Apply(FanChannel.Gpu, 25); Equal(64, f.Hardware.Current.GpuPwm);
});
Test("Invalid GPU temperature prevents resuming either manual target", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 25); f.Session.Apply(FanChannel.Gpu, 0);
    f.Hardware.Current = f.Hardware.Current with { GpuTemperature = 0 }; f.Session.Refresh();
    f.Now = f.Now.AddSeconds(1); f.Session.Refresh(); Equal(2, f.Hardware.Writes);
    Equal(true, f.Session.State.Reconnecting); Equal(15, f.Hardware.AutomaticMask);
    f.Hardware.Current = f.Hardware.Current with { GpuTemperature = 55 };
    f.Now = f.Now.AddSeconds(2); f.Session.Refresh(); Equal(0, f.Hardware.Current.GpuPwm);
});
Test("Failed first apply never becomes an automatic replay target", () =>
{
    var f = new Fixture(); f.Hardware.WriteFails = true;
    Throws<InvalidOperationException>(() => f.Session.Apply(FanChannel.Cpu, 0));
    f.Hardware.WriteFails = false; f.Now = f.Now.AddSeconds(1); f.Session.Refresh();
    Equal(1, f.Hardware.Writes); Equal(FanMode.System, f.Session.State.Cpu.Mode);
});
Test("Failed new apply resumes the previous successful target", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 50); f.Hardware.WriteFails = true;
    Throws<InvalidOperationException>(() => f.Session.Apply(FanChannel.Cpu, 0));
    f.Hardware.WriteFails = false; f.Now = f.Now.AddSeconds(1); f.Session.Refresh();
    Equal(128, f.Hardware.Current.CpuPwm); Equal(50, f.Session.State.Cpu.RequestedPercent);
});
Test("Heat observed with dead watchdog remains latched after reconnect", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Cpu, 0); f.Watchdog.Ready = false;
    f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 85 }; f.Session.Refresh();
    f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 60 };
    f.Now = f.Now.AddSeconds(1); f.Session.Refresh(); Equal(255, f.Hardware.Current.CpuPwm);
    Equal(FanMode.ThermalProtection, f.Session.State.Cpu.Mode);
});
Test("Failed cooled apply does not clear thermal latch on replay", () =>
{
    var f = new Fixture(); f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 85 };
    f.Session.Apply(FanChannel.Cpu, 0); f.Hardware.Current = f.Hardware.Current with { CpuTemperature = 60 };
    f.Hardware.WriteFails = true; Throws<InvalidOperationException>(() => f.Session.Apply(FanChannel.Cpu, 25));
    f.Hardware.WriteFails = false; f.Now = f.Now.AddSeconds(1); f.Session.Refresh();
    Equal(255, f.Hardware.Current.CpuPwm); Equal(FanMode.ThermalProtection, f.Session.State.Cpu.Mode);
});
Test("Firmware override is released without fighting it through reconnection", () =>
{
    var f = new Fixture(); f.Session.Apply(FanChannel.Gpu, 0); f.Session.Refresh();
    f.Hardware.Current = f.Hardware.Current with { GpuPwm = 128 }; f.Session.Refresh();
    f.Now = f.Now.AddSeconds(4); f.Session.Refresh(); f.Now = f.Now.AddMinutes(1); f.Session.Refresh();
    Equal(FanMode.ExternalOverride, f.Session.State.Gpu.Mode); Equal(0, f.Hardware.Reconnects);
    Equal(1, f.Hardware.Writes); Equal<int?>(null, f.Session.State.Gpu.RequestedPercent);
});

Console.WriteLine($"\n{passed} passed; {failed} failed");
Environment.ExitCode = failed == 0 ? 0 : 1;

sealed class Fixture
{
    public FakeHardware Hardware { get; } = new();
    public FakeWatchdog Watchdog { get; } = new();
    public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-09T00:00:00Z");
    public FanSession Session { get; }
    public Fixture() => Session = new(Hardware, Watchdog, () => Now);
}
sealed class FakeHardware : IFanHardware
{
    public FanTelemetry Current = new(128, 49, 3711, 128, 55, 3700, 0);
    public int Writes, Restores, Reconnects;
    public bool ReadFails, WriteFails, RestoreFails, ReconnectFails;
    public bool AcceptWrites = true;
    public int? LastCpuPwm, LastGpuPwm;
    public int AutomaticMask = 15;
    public Action? BeforeWrite;
    public FanTelemetry Read() => ReadFails ? throw new IOException("sensor unavailable") : Current;
    public void Reconnect() { Reconnects++; if (ReconnectFails) throw new IOException("driver unavailable"); }
    public void SetPwm(int? cpuPwm, int? gpuPwm, FanTelemetry current)
    {
        BeforeWrite?.Invoke(); Writes++;
        if (WriteFails) throw new IOException("write failed");
        LastCpuPwm = cpuPwm; LastGpuPwm = gpuPwm;
        if (AcceptWrites) Current = current with { CpuPwm = cpuPwm ?? current.CpuPwm, GpuPwm = gpuPwm ?? current.GpuPwm };
        AutomaticMask = FanProtocol.AutomaticMask(cpuPwm.HasValue, gpuPwm.HasValue);
    }
    public void RestoreAutomatic() { Restores++; if (RestoreFails) throw new IOException("restore failed"); Current = Current with { CpuPwm = 128, GpuPwm = 128 }; AutomaticMask = 15; }
}
sealed class FakeWatchdog : IWatchdogLease
{
    public bool Ready = true, Armed;
    public bool RestartFails;
    public int Restarts;
    public void EnsureReady() { if (!Ready) throw new IOException("watchdog unavailable"); }
    public void Restart() { Restarts++; if (RestartFails) throw new IOException("watchdog restart failed"); Ready = true; Armed = false; }
    public void Arm() => Armed = true;
    public void Disarm() => Armed = false;
    public void Beat() { }
}
