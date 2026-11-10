using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FanControl.Core;

namespace CpuFanControl;

public partial class FanPanel : UserControl
{
    private FanChannel channel;
    private bool initializedTarget;
    private bool hasTemperature;

    public FanPanel() { InitializeComponent(); Channel = FanChannel.Cpu; }

    public FanChannel Channel
    {
        get => channel;
        set
        {
            channel = value;
            string prefix = value == FanChannel.Cpu ? "CPU" : "GPU";
            FanTitle.Text = prefix + " 风扇";
            AutomationProperties.SetName(this, prefix + " 风扇");
            AutomationProperties.SetName(SpeedSlider, prefix + " 目标百分比");
            AutomationProperties.SetAutomationId(SpeedSlider, prefix + "SpeedSlider");
            AutomationProperties.SetName(ApplyButton, prefix + " 应用");
            AutomationProperties.SetAutomationId(ApplyButton, prefix + "Apply");
            AutomationProperties.SetName(AutoButton, prefix + " 恢复自动");
            AutomationProperties.SetAutomationId(AutoButton, prefix + "Auto");
            foreach (Button button in Presets.Children)
                AutomationProperties.SetName(button, prefix + " " + button.Content);
        }
    }

    public int TargetPercent => (int)Math.Round(SpeedSlider.Value);
    public event EventHandler? ApplyRequested;
    public event EventHandler? AutoRequested;

    public void Render(FanTelemetry? data, FanChannelState state, string? error)
    {
        hasTemperature = data != null && FanTelemetry.ValidTemperature(data.Temperature(Channel));
        if (data != null)
        {
            int percent = Channel == FanChannel.Cpu ? data.CpuPercent : data.GpuPercent;
            int rpm = Channel == FanChannel.Cpu ? data.CpuRpm : data.GpuRpm;
            ActualPercentText.Text = percent + "%";
            DutyDial.Percent = percent;
            TemperatureText.Text = hasTemperature ? data.Temperature(Channel) + " °C" : "— °C";
            TemperatureText.Foreground = (Brush)FindResource(
                data.Temperature(Channel) >= FanSession.ThermalLimit ? "Warm" : "Ink");
            RpmText.Text = rpm.ToString("N0");
            if (!initializedTarget)
            {
                initializedTarget = true;
                SpeedSlider.Value = percent;
            }
        }
        else
        {
            ActualPercentText.Text = "—";
            DutyDial.Percent = 0;
            TemperatureText.Text = "— °C";
            RpmText.Text = "—";
        }
        string label = state.Mode switch
        {
            FanMode.System => hasTemperature ? "系统控制" : "数据不可用",
            FanMode.Applying => "正在应用",
            FanMode.Manual => "手动控制",
            FanMode.ThermalProtection => "高温保护",
            FanMode.ExternalOverride => "控制已交还",
            FanMode.Reconnecting => "正在重连",
            _ => "控制失败"
        };
        SetStatus(label, state.Mode is FanMode.Fault or FanMode.ThermalProtection or FanMode.ExternalOverride or FanMode.Reconnecting, error);
    }

    public void SetStatus(string label, bool warm, string? detail)
    {
        ModeText.Text = label;
        ModeText.Foreground = (Brush)FindResource(warm ? "Warm" : "Accent");
        ModeBadge.Background = (Brush)FindResource(warm ? "WarmSoft" : "AccentSoft");
        ModeBadge.ToolTip = detail;
    }

    public void UpdateButtons(bool connected, bool blocked)
    {
        ApplyButton.IsEnabled = connected && hasTemperature && !blocked;
        AutoButton.IsEnabled = connected && !blocked;
        SpeedSlider.IsEnabled = connected && hasTemperature && !blocked;
        Presets.IsEnabled = SpeedSlider.IsEnabled;
    }

    private void SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TargetPercentText != null) TargetPercentText.Text = ((int)Math.Round(e.NewValue)) + "%";
    }

    private void PresetClick(object sender, RoutedEventArgs e) => SpeedSlider.Value = int.Parse((string)((Button)sender).Tag);
    private void ApplyClick(object sender, RoutedEventArgs e) => ApplyRequested?.Invoke(this, EventArgs.Empty);
    private void AutoClick(object sender, RoutedEventArgs e) => AutoRequested?.Invoke(this, EventArgs.Empty);
}
