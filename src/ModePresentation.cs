using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CarbonControl
{
    public sealed partial class MainUi
    {
        private DateTime? cpuPolicyRefreshRequested;
        private CpuPolicyReading displayedCpuPolicy = new CpuPolicyReading();

        private void InitializeModeDetails()
        {
            var rows = C<StackPanel>("ModeDetailsRows"); rows.Children.Clear();
            foreach (var profile in PerformanceProfiles.All)
            {
                var body = new StackPanel();
                body.Children.Add(DetailText(profile.Name, 15, true));
                body.Children.Add(DetailText(profile.Description, 12, false));
                body.Children.Add(DetailText(profile.RestoreOriginal ? "接电 / 电池：恢复原电源方案" :
                    "接电：睿频 " + ModeBoost(profile.AcBoost) + " · 频率上限 " + ModeFrequency(profile.AcMaxFrequencyMhz), 12, false));
                if (!profile.RestoreOriginal) body.Children.Add(DetailText("电池：睿频 " + ModeBoost(profile.DcBoost) + " · 频率上限 " + ModeFrequency(profile.DcMaxFrequencyMhz), 12, false));
                body.Children.Add(DetailText("PL1 / PL2 / 时间窗口：沿用硬件设置", 12, false));
                body.Children.Add(DetailText("预设风扇：" + FanDetails(profile.Fan), 12, false));
                var border = new Border { Child = body, Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 9), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1) };
                border.SetResourceReference(Border.BackgroundProperty, "Input"); border.SetResourceReference(Border.BorderBrushProperty, "Line");
                rows.Children.Add(border);
            }
            UpdateModeDetails();
        }
        private TextBlock DetailText(string text, double size, bool heading)
        {
            var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, heading ? 7 : 5) };
            block.SetResourceReference(TextBlock.ForegroundProperty, heading ? "Text" : "Muted"); return block;
        }
        private static string ModeBoost(uint? value)
        { return !value.HasValue ? "原方案" : CpuPolicyMonitor.BoostLabel(value) + " (" + value.Value + ")"; }
        private static string ModeFrequency(uint? value)
        { return !value.HasValue ? "原方案" : value == 0 ? "系统调节 (0 MHz)" : CpuPolicyMonitor.FrequencyLabel(value); }
        private string FanDetails(FanMode mode)
        {
            if (mode == FanMode.Bios) return "系统自动";
            if (mode == FanMode.Manual) return "手动 " + appliedManualLevel + " 档";
            var curve = mode == FanMode.Custom ? settings.Curve : FanPolicy.Preset(mode).ToArray();
            return ModeLabels[(int)mode] + " · " + string.Join("  /  ", curve.Select(p => p.Temperature + "°C→" + p.Level + "档"));
        }
        private void RequestCpuPolicyRefresh()
        {
            if (preview) return;
            cpuPolicyRefreshRequested = DateTime.UtcNow;
            if (engine != null) { engine.RefreshCpuLimits(); PresentCpuLimits(engine.Current); }
        }
        private void UpdateModeDetails()
        {
            var profile = PerformanceProfiles.Get(performanceUiMode);
            string current = profile.RestoreOriginal ? "系统 · 当前 Windows 电源方案" : profile.Name + " · 接电 / 电池睿频 " + profile.AcBoost + "/" + profile.DcBoost +
                " · 频率上限 " + ModeFrequency(profile.AcMaxFrequencyMhz) + " / " + ModeFrequency(profile.DcMaxFrequencyMhz);
            T("ModeDetailsCurrent", current + " · PL1 / PL2 / 窗口沿用硬件");
            string readback;
            if (cpuPolicyRefreshRequested.HasValue) readback = "正在读取切换后的策略…";
            else if (!displayedCpuPolicy.BoostMode.HasValue || !displayedCpuPolicy.MaximumFrequencyMhz.HasValue) readback = "等待策略读回";
            else
            {
                string supply = displayedCpuPolicy.OnAcPower == true ? "接电" : "电池";
                bool match = profile.RestoreOriginal || (displayedCpuPolicy.BoostMode == (displayedCpuPolicy.OnAcPower == true ? profile.AcBoost : profile.DcBoost) &&
                    displayedCpuPolicy.MaximumFrequencyMhz == (displayedCpuPolicy.OnAcPower == true ? profile.AcMaxFrequencyMhz : profile.DcMaxFrequencyMhz));
                readback = (match ? "读回已确认" : "当前策略已变更") + " · " + supply + " · 睿频 " + ModeBoost(displayedCpuPolicy.BoostMode) +
                    " · 频率 " + ModeFrequency(displayedCpuPolicy.MaximumFrequencyMhz) + " · " + displayedCpuPolicy.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            }
            T("ModeDetailsPolicyReadback", readback);
            string actualFan = engine != null && engine.FanLevel.HasValue ? " · 实际 " + ((engine.FanLevel.Value & 128) != 0 ? "系统自动" : (engine.FanLevel.Value & 7) + " 档") : "";
            T("ModeDetailsFanDetails", "预设：" + FanDetails(profile.Fan) + "\n当前：" +
                (pendingPerformanceFan.HasValue ? "等待连接后应用" + ModeLabels[(int)pendingPerformanceFan.Value] : FanDetails(selectedMode)) +
                actualFan +
                (selectedMode == FanMode.Bios ? "" : selectedMode == FanMode.Manual ? "\n92°C 使用 7 档" : "\n降温回差 3°C · 降档延迟 8 秒 · 92°C 使用 7 档"));
            T("MonitorModeSummary", (profile.RestoreOriginal ? "系统电源设置" : profile.Name) + " · 睿频 " +
                (cpuPolicyRefreshRequested.HasValue ? "读取中" : CpuPolicyMonitor.BoostLabel(displayedCpuPolicy.BoostMode)) + " · 风扇" + ModeLabels[(int)selectedMode]);
        }
    }
}
