# CPU / GPU 风扇控制

用于 **神舟 HASEE CNP6S / Windows x64** 的简洁风扇控制工具，使用 C#、WPF 和 .NET 8 开发。

- CPU、GPU 风扇独立调节 **0%–100%**，支持 1% 步进与常用预设。
- 实时显示两路 PWM 占空比、温度和 RPM。
- 单路恢复自动，正常退出时全部恢复自动。
- 驱动或监护进程故障后自动重连。
- 内置温度保护与独立恢复监护进程。

## 下载与运行

从 [Releases](https://github.com/jiwannian/hasee-cnp6s-fan-control/releases/latest) 下载 `CpuFanControl-win-x64.zip`，解压后运行 `CpuFanControl.exe`，允许管理员权限请求。

发布包已包含 .NET 运行时，无需额外安装 .NET。

### 适配范围

当前仅适配 **HASEE CNP6S**，开发验证设备使用 Intel Core i9-14900HX。程序会检查硬件型号，不对其他机型写入。

需要已经安装原厂 CLEVO Control Center 及其驱动接口：

```text
C:\Program Files (x86)\ControlCenter\InsydeDCHU.dll
```

仓库和发布包不包含厂商 DLL、内核驱动或厂商反编译代码。

## 使用

1. 在 CPU 或 GPU 区域拖动滑块，或选择百分比预设。
2. 点击该区域的“应用”。
3. 点击该区域的“恢复自动”可交还该路控制，另一路的手动设置保留。

启动时仅监测，不自动接管风扇；不设置开机自启，也不跨进程保存手动设置。未接管的风扇和第三路保持固件自动控制。

百分比表示 PWM 占空比，不是 RPM 的线性百分比。实际 RPM 会随风扇惯性和固件行为变化。

## 自动重连与温度保护

- 手动控制中的 CPU 或 GPU 达到 **85°C** 时，对应风扇设为 100%，并保持保护锁定；降至 **75°C 或以下** 后才允许主动应用较低设置。
- 驱动读写失败、手动传感器失效或监护进程退出时，先尝试交还全部自动控制，再显示“正在重连”。按 **1、2、4、8、10 秒**间隔重试，之后每 10 秒继续重试。
- 重连时重新加载厂商接口、重建监护进程并读取温度，随后恢复本次运行中最后成功应用的两路设置。保护锁定保留，未应用的滑块值和失败的新指令不会自动执行。
- 重连期间点击“恢复自动”可取消对应待恢复设置。正常关闭、注销和休眠也会清除待恢复设置；唤醒后不重新应用手动值。
- 指令持续 3 秒未被读回时，视为固件或其他软件接管，交还系统控制并取消目标，不反复争夺控制。
- GPU 温度无效时禁用 GPU 手动接管；自动模式下 GPU 暂时无温度不影响 CPU 单独控制。
- 独立监护进程在主程序退出或超过 4 秒没有心跳时尝试恢复自动，驱动未确认时重试。

## 构建与测试

构建需要 **Windows x64、.NET 8 SDK 和 PowerShell**。从仓库根目录运行：

```powershell
./build.ps1
```

程序与使用说明输出到 `artifacts/publish`。指定其他输出目录：

```powershell
./build.ps1 -Output ./artifacts/custom
```

仅运行逻辑测试，不接触硬件：

```powershell
dotnet run --project ./tests/FanControl.Tests --configuration Release
```

74 项测试覆盖双风扇控制、故障恢复、重试间隔和温控。具体边界见 [验证范围](docs/TESTING.md)。GitHub Actions 自动测试和构建 Windows x64 发布包。

## 诊断

在发布目录执行只读诊断：

```powershell
./CpuFanControl.exe --diagnose ./diagnostics.json
```

日志位于 `%LOCALAPPDATA%\CpuFanControl\Logs`。诊断和日志默认只保存在本机。

## 项目结构

- `src/FanControl.Core`：双风扇控制状态、协议和重连逻辑。
- `src/CpuFanControl`：WPF 界面、厂商接口及监护进程。
- `tests/FanControl.Tests`：无第三方测试框架依赖的逻辑测试。

## 接口依据

本项目为独立 C# 实现。通过本机 CLEVO Control Center 接口声明与遥测布局确认调用方式，并与公开的 [TUXEDO Control Center 接口](https://github.com/tuxedocomputers/tuxedo-control-center/blob/095ecef9aa4d029b8cff4bc7d3b44454a6d50e47/src/native-lib/tuxedo_io_lib/tuxedo_io_api.hh) 交叉核对协议。

- ACPI `0x68` 打包三路 PWM，占用一个命令同时写入。
- `0x69` 自动位掩码：`0x0e` 仅 CPU 手动；`0x0d` 仅 GPU 手动；`0x0c` 两路手动；`0x0f` 全部自动。
- 第三路始终自动。`0` 不是恢复自动指令。

## 许可证

[MIT](LICENSE)。
