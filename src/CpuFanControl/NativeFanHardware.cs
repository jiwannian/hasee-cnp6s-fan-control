using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using FanControl.Core;
using Microsoft.Win32;

namespace CpuFanControl;

internal sealed class NativeFanHardware : IFanHardware, IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ReadBuffer(int command, [Out] byte[] buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WriteBuffer(int command, [In] byte[] buffer, int length);

    private nint library;
    private ReadBuffer? read;
    private WriteBuffer? write;
    private bool disposed;
    public string DllPath { get; }
    public string Model { get; }

    public NativeFanHardware()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("需要 64 位 Windows。");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("请以管理员身份运行。");
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        Model = Convert.ToString(bios?.GetValue("SystemProductName")) ?? "";
        string maker = Convert.ToString(bios?.GetValue("SystemManufacturer")) ?? "";
        if (!Model.Equals("CNP6S", StringComparison.OrdinalIgnoreCase) ||
            !maker.Contains("HASEE", StringComparison.OrdinalIgnoreCase))
            throw new PlatformNotSupportedException("此版本仅适配神舟 HASEE CNP6S。");
        DllPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "ControlCenter", "InsydeDCHU.dll");
        // Loading is lazy so a temporary driver/DLL startup failure is retried by the session.
    }

    public void Reconnect()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Unload();
        EnsureConnected();
        AppLog.Write("hardware-reconnected");
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (library != 0) return;
        if (!File.Exists(DllPath))
            throw new FileNotFoundException("未找到本机 CLEVO Control Center 驱动接口。", DllPath);
        library = NativeLibrary.Load(DllPath);
        try
        {
            read = Marshal.GetDelegateForFunctionPointer<ReadBuffer>(
                NativeLibrary.GetExport(library, "GetDCHU_Data_Buffer"));
            write = Marshal.GetDelegateForFunctionPointer<WriteBuffer>(
                NativeLibrary.GetExport(library, "SetDCHU_Data"));
        }
        catch { Unload(); throw; }
    }

    public FanTelemetry Read()
    {
        EnsureConnected();
        var data = new byte[256];
        int result = read!(FanProtocol.ReadTelemetry, data);
        if (result != FanProtocol.ReadTelemetry)
            throw new IOException($"读取厂商接口失败（返回 {result}）。");
        return FanTelemetry.Decode(data);
    }

    public void SetPwm(int? cpuPwm, int? gpuPwm, FanTelemetry current)
    {
        // Preserve automatic channels while packing both manual targets, then release
        // the automatic channels again, including the unused third fan.
        Write(FanProtocol.SetPwm, FanProtocol.PackPwm(cpuPwm, gpuPwm, current));
        Write(FanProtocol.SetAutomatic, (uint)FanProtocol.AutomaticMask(cpuPwm.HasValue, gpuPwm.HasValue));
    }

    public void RestoreAutomatic() => Write(FanProtocol.SetAutomatic, FanProtocol.AllFansAutomaticMask);

    private void Write(int command, uint value)
    {
        EnsureConnected();
        var data = BitConverter.GetBytes(value);
        int result = write!(command, data, data.Length);
        if (result != command) throw new IOException($"调速指令 0x{command:X2} 失败（返回 {result}）。");
    }

    private void Unload()
    {
        if (library != 0) NativeLibrary.Free(library);
        library = 0;
        read = null;
        write = null;
    }

    public void Dispose() { if (disposed) return; disposed = true; Unload(); }
}
