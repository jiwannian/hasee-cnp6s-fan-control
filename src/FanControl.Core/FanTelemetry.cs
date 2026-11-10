namespace FanControl.Core;

public enum FanChannel { Cpu, Gpu }

public sealed record FanTelemetry(int CpuPwm, int CpuTemperature, int CpuRpm,
    int GpuPwm, int GpuTemperature, int GpuRpm, int ThirdFanPwm)
{
    public int CpuPercent => FanProtocol.ToPercent(CpuPwm);
    public int GpuPercent => FanProtocol.ToPercent(GpuPwm);

    public int Pwm(FanChannel channel) => channel switch
    {
        FanChannel.Cpu => CpuPwm,
        FanChannel.Gpu => GpuPwm,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    public int Temperature(FanChannel channel) => channel switch
    {
        FanChannel.Cpu => CpuTemperature,
        FanChannel.Gpu => GpuTemperature,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    public static bool ValidTemperature(int value) => value is >= 5 and <= 120;

    public static FanTelemetry Decode(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 41)
            throw new InvalidDataException("厂商遥测数据不完整。");
        if (!ValidTemperature(buffer[18]))
            throw new InvalidDataException("CPU 温度数据无效。");
        return new(buffer[16], buffer[18], FanProtocol.ToRpm((buffer[2] << 8) | buffer[3]),
            buffer[19], buffer[21], FanProtocol.ToRpm((buffer[4] << 8) | buffer[5]), buffer[22]);
    }
}

public static class FanProtocol
{
    public const int ReadTelemetry = 0x0c;
    public const int SetPwm = 0x68;
    public const int SetAutomatic = 0x69;
    public const int AllFansAutomaticMask = 0x0f;

    public static int AutomaticMask(bool cpuManual, bool gpuManual) =>
        AllFansAutomaticMask & ~(cpuManual ? 1 : 0) & ~(gpuManual ? 2 : 0);

    public static int ToPwm(int percent)
    {
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        return (int)Math.Round(percent * 255.0 / 100, MidpointRounding.AwayFromZero);
    }

    public static int ToPercent(int pwm)
    {
        if (pwm is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(pwm));
        return (int)Math.Round(pwm * 100.0 / 255, MidpointRounding.AwayFromZero);
    }

    // Identical tachometer conversion to the installed CLEVO Control Center.
    public static int ToRpm(int tachometer) => tachometer is 0 or 65535 ? 0
        : (int)Math.Round(120.0 / (0.00005565217391304348 * tachometer));

    public static uint PackPwm(int? cpuPwm, int? gpuPwm, FanTelemetry current)
    {
        if (cpuPwm is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(cpuPwm));
        if (gpuPwm is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(gpuPwm));
        return (uint)((cpuPwm ?? current.CpuPwm) | (gpuPwm ?? current.GpuPwm) << 8 | current.ThirdFanPwm << 16);
    }
}
