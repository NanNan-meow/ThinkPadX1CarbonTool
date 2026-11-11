using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CarbonControl
{
    public sealed partial class MainUi
    {
        private PerformanceController performanceController;
        private PerformanceMode performanceUiMode = PerformanceMode.System;
        private FanMode? pendingPerformanceFan;
        private string storageSignature;
        private string performanceError;
        private readonly List<double?> batteryPowerHistory = new List<double?>();

        private void InitializeDeviceUi()
        {
            if (!preview)
            {
                try { performanceController = new PerformanceController(); performanceController.RecoverPreviousSession(); }
                catch (Exception ex) { Log.Write(ex); performanceError = ex.Message; }
            }
            Click("PerfSystem", () => ApplyPerformance(PerformanceMode.System));
            Click("PerfLow", () => ApplyPerformance(PerformanceMode.LowTemperature));
            Click("PerfBalanced", () => ApplyPerformance(PerformanceMode.Balanced));
            Click("PerfHigh", () => ApplyPerformance(PerformanceMode.Performance));
            C<Canvas>("BatteryPowerChart").SizeChanged += (s, e) => DrawBatteryPower();
            UpdatePerformanceStatus(); PresentDevices(new Snapshot());
        }
        private void ApplyPerformance(PerformanceMode mode)
        {
            long feedback = BeginFeedback("正在应用" + PerformanceProfiles.Get(mode).Name + "模式，等待设置与风扇读回…");
            performanceFeedbackVersion = feedback;
            pendingFanAction = null;
            try
            {
                if (!preview)
                {
                    if (performanceController == null) performanceController = new PerformanceController();
                    performanceController.Apply(mode);
                }
                performanceError = null; performanceUiMode = mode;
                pendingPerformanceFan = PerformanceProfiles.Get(mode).Fan;
                feedback = BeginFeedback(PerformanceProfiles.Get(mode).Name + "：电源设置已确认 · 等待风扇与温度读回"); performanceFeedbackVersion = feedback;
                startupPolicyGate.Cancel();
                RequestCpuPolicyRefresh();
                var sample = engine == null ? new Snapshot() : engine.Current;
                TryApplyPerformanceFan(sample, engine != null && engine.FanReady, engine != null && engine.FanConfirmed, DateTime.UtcNow);
                UpdatePerformanceStatus();
            }
            catch (Exception ex) { Log.Write(ex); performanceError = PerformanceProfiles.Get(mode).Name + "：" + ex.Message; pendingFanAction = null; performanceFeedbackVersion = 0; FinishFeedback(feedback, false, "模式未应用 · " + performanceError); UpdatePerformanceStatus(); }
        }
        private void TryApplyPerformanceFan(Snapshot sample, bool ready, bool confirmed, DateTime now)
        {
            if (!pendingPerformanceFan.HasValue) return;
            FanMode mode = pendingPerformanceFan.Value;
            if (mode != FanMode.Bios && (!ready || !confirmed || !SensorMap.Valid(sample.CpuTemperature) ||
                sample.CpuTemperature > 125 || sample.Timestamp > now || (now - sample.Timestamp).TotalSeconds > 5)) return;
            if (SelectMode(mode, false, false, false, true)) pendingPerformanceFan = null;
        }
        private void RefreshPerformanceMode()
        {
            if (performanceController == null || tickCount % 3 != 0) return;
            try
            {
                performanceController.RefreshActiveMode();
                if (performanceUiMode != performanceController.Mode)
                {
                    long previous = performanceFeedbackVersion;
                    performanceUiMode = performanceController.Mode; pendingPerformanceFan = null; performanceFeedbackVersion = 0;
                    if (pendingFanAction != null && pendingFanAction.Token == previous) pendingFanAction = null;
                    FinishFeedback(previous, false, "Windows 电源方案已改变 · 之前的模式操作已取消");
                }
            }
            catch (Exception ex) { Log.Write(ex); }
        }
        private void UpdatePerformanceStatus()
        {
            var status = C<TextBlock>("PerformanceStatus"); if (status == null) return;
            foreach (var pair in new[] { new { Name = "PerfSystem", Mode = PerformanceMode.System }, new { Name = "PerfLow", Mode = PerformanceMode.LowTemperature },
                new { Name = "PerfBalanced", Mode = PerformanceMode.Balanced }, new { Name = "PerfHigh", Mode = PerformanceMode.Performance } })
                Highlight(pair.Name, pair.Mode == performanceUiMode);
            string label = performanceUiMode == PerformanceMode.System ? "系统电源设置" : performanceUiMode == PerformanceMode.LowTemperature ? "低温办公 · 关闭睿频" :
                performanceUiMode == PerformanceMode.Balanced ? "均衡 · 允许睿频" : "性能 · 积极睿频";
            status.Text = performanceError != null ? "未应用：" + performanceError : label +
                (pendingPerformanceFan.HasValue ? " · 等待风扇连接" : " · 风扇" + ModeLabels[(int)selectedMode]);
            UpdateModeDetails();
        }
        private void PresentDevices(Snapshot sample)
        {
            var battery = sample.Batteries.FirstOrDefault();
            T("BatteryName", battery == null ? "正在读取电池" : battery.Name);
            T("BatteryState", battery == null ? "等待数据" : battery.State);
            T("BatteryCharge", Format(battery == null ? null : battery.ChargePercent, "0", "%"));
            T("BatteryHealth", Format(battery == null ? null : battery.HealthPercent, "0.0", "%"));
            double? signedPower = null;
            string power = "— W";
            if (battery != null)
            {
                if (SensorMap.Valid(battery.ChargePowerW) && battery.ChargePowerW > 0)
                { signedPower = battery.ChargePowerW; power = "充电 " + Format(battery.ChargePowerW, "0.0", "W"); }
                else if (SensorMap.Valid(battery.DischargePowerW) && battery.DischargePowerW > 0)
                { signedPower = -battery.DischargePowerW; power = "耗电 " + Format(battery.DischargePowerW, "0.0", "W"); }
                else if (SensorMap.Valid(battery.ChargePowerW) || SensorMap.Valid(battery.DischargePowerW)) { signedPower = 0; power = "0.0 W"; }
            }
            T("BatteryPower", power);
            T("BatteryCycles", battery == null || !battery.CycleCount.HasValue ? "— 次" : battery.CycleCount.Value.ToString("N0") + " 次");
            T("BatteryDesign", Format(battery == null ? null : battery.DesignCapacityWh, "0.00", "Wh"));
            T("BatteryFull", Format(battery == null ? null : battery.FullChargeCapacityWh, "0.00", "Wh"));
            T("BatteryRemaining", Format(battery == null ? null : battery.RemainingCapacityWh, "0.00", "Wh"));
            T("BatteryVoltage", Format(battery == null ? null : battery.Voltage, "0.00", "V"));
            T("BatteryTemperature", Format(battery == null ? null : battery.Temperature, "0.0", "°C"));
            T("BatteryRuntime", battery == null || !SensorMap.Valid(battery.RemainingMinutes) ? "—" :
                ((int)battery.RemainingMinutes.Value / 60) + " 小时 " + ((int)battery.RemainingMinutes.Value % 60) + " 分钟");
            T("BatteryStatus", battery == null ? "等待电池数据" : "每 2 秒刷新 · 电池端功率" );
            batteryPowerHistory.Add(signedPower); if (batteryPowerHistory.Count > 60) batteryPowerHistory.RemoveAt(0); DrawBatteryPower();
            PresentStorage(sample.Drives);
        }
        private void PresentStorage(IList<StorageReading> drives)
        {
            string signature = string.Join(";", drives.Select(d => string.Join("|", new object[] { d.Id, d.Name, d.Temperature, d.HealthPercent, d.UsedLifePercent, d.TotalWrittenGB, d.TotalReadGB, d.UsedSpacePercent, d.HasWarning, d.Status })));
            T("StorageStatus", drives.Count == 0 ? "等待 SSD 数据" : drives.Count + " 个存储设备 · 每 10 秒刷新");
            if (storageSignature == signature) return;
            storageSignature = signature; var cards = C<StackPanel>("StorageCards"); cards.Children.Clear();
            foreach (var drive in drives)
            {
                var panel = new StackPanel(); var header = new DockPanel();
                var name = new TextBlock { Text = drive.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                name.SetResourceReference(TextBlock.ForegroundProperty, "Text"); header.Children.Add(name); panel.Children.Add(header);
                var state = new TextBlock { Text = drive.Status ?? "", FontSize = 12, Margin = new Thickness(0, 8, 0, 18) };
                state.SetResourceReference(TextBlock.ForegroundProperty, drive.HasWarning == true ? "Accent" : "Muted"); panel.Children.Add(state);
                var metrics = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
                metrics.Children.Add(StorageMetric("温度", Format(drive.Temperature, "0", "°C")));
                metrics.Children.Add(StorageMetric("寿命剩余估计", Format(drive.HealthPercent, "0", "%")));
                metrics.Children.Add(StorageMetric("空间使用率", Format(drive.UsedSpacePercent, "0", "%")));
                metrics.Children.Add(StorageMetric("累计写入", StorageCapacity(drive.TotalWrittenGB)));
                metrics.Children.Add(StorageMetric("累计读取", StorageCapacity(drive.TotalReadGB)));
                metrics.Children.Add(StorageMetric("寿命已用估计", Format(drive.UsedLifePercent, "0", "%")));
                panel.Children.Add(metrics);
                var card = new Border { Style = (Style)window.Resources["CardStyle"], Child = panel, Margin = new Thickness(0, 0, 0, 14) };
                cards.Children.Add(card);
            }
        }
        private FrameworkElement StorageMetric(string title, string value)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 14, 18) };
            panel.Children.Add(new TextBlock { Text = title, Style = (Style)window.Resources["Label"] });
            var label = new TextBlock { Text = value, FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 7, 0, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text"); panel.Children.Add(label); return panel;
        }
        private static string StorageCapacity(double? gigabytes)
        { return !SensorMap.Valid(gigabytes) ? "—" : gigabytes >= 1000 ? Format(gigabytes / 1000, "0.00", "TB") : Format(gigabytes, "0.0", "GB"); }
        private void DrawBatteryPower()
        {
            var c = C<Canvas>("BatteryPowerChart"); if (c == null || c.ActualWidth < 1) return;
            c.Children.Clear(); double w = c.ActualWidth - 32, h = c.ActualHeight - 16;
            double maximum = Math.Max(10, batteryPowerHistory.Where(x => x.HasValue).Select(x => Math.Abs(x.Value)).DefaultIfEmpty(0).Max());
            foreach (int sign in new[] { -1, 0, 1 })
            {
                double y = 8 + h / 2 - sign * h / 2;
                Line(c, 0, y, w, y, "Line", 1);
                var label = new TextBlock { Text = (sign * maximum).ToString("0"), FontSize = 9, Foreground = Brush("Muted") };
                Canvas.SetLeft(label, w + 5); Canvas.SetTop(label, y - 6); c.Children.Add(label);
            }
            for (int i = 1; i < batteryPowerHistory.Count; i++) if (batteryPowerHistory[i - 1].HasValue && batteryPowerHistory[i].HasValue)
                Line(c, (60 - batteryPowerHistory.Count + i - 1) * w / 59, 8 + h / 2 - batteryPowerHistory[i - 1].Value / maximum * h / 2,
                    (60 - batteryPowerHistory.Count + i) * w / 59, 8 + h / 2 - batteryPowerHistory[i].Value / maximum * h / 2, "Green", 2);
        }
        private static void AppendDeviceReport(StringBuilder text, Snapshot sample)
        {
            foreach (var b in sample.Batteries) text.AppendLine("Battery: " + b.Name + " | " + b.State + " | Health=" + b.HealthPercent + "% | Cycles=" + b.CycleCount + " | Charge=" + b.ChargePowerW + "W | Discharge=" + b.DischargePowerW + "W");
            foreach (var d in sample.Drives) text.AppendLine("Storage: " + d.Name + " | Temperature=" + d.Temperature + "C | Life remaining=" + d.HealthPercent + "% | Written=" + d.TotalWrittenGB + "GB");
        }
        private static Snapshot DevicePreview()
        {
            var s = new Snapshot();
            s.Batteries.Add(new BatteryReading { Name = "ThinkPad 内置电池", State = "正在使用电池", ChargePercent = 68, HealthPercent = 74.1, DesignCapacityWh = 51.01,
                FullChargeCapacityWh = 37.8, RemainingCapacityWh = 25.7, CycleCount = 1184, DischargePowerW = 7.4, Voltage = 15.3, Temperature = 32, RemainingMinutes = 208 });
            s.Drives.Add(new StorageReading { Name = "NVMe SSD · 界面预览", Id = "preview", Temperature = 42, HealthPercent = 95, UsedLifePercent = 5,
                TotalWrittenGB = 18400, TotalReadGB = 32600, UsedSpacePercent = 62, HasWarning = false, Status = "健康信息正常 · 示例数据" });
            return s;
        }
        private void TestDeviceUi(Grid root, Action<string> press)
        {
            PresentDevices(DevicePreview()); press("NavBattery");
            if (activePage != "Battery" || C<TextBlock>("BatteryHealth").Text != "74.1 %" || !C<TextBlock>("BatteryPower").Text.Contains("7.4")) throw new Exception("Battery telemetry presentation");
            press("NavStorage"); if (activePage != "Storage" || C<StackPanel>("StorageCards").Children.Count != 1) throw new Exception("SSD telemetry cards");
            press("PerfLow"); if (performanceUiMode != PerformanceMode.LowTemperature || pendingPerformanceFan != FanMode.Balanced) throw new Exception("Low temperature profile and fan linkage");
            var s = new Snapshot { CpuTemperature = 60, Timestamp = DateTime.UtcNow };
            TryApplyPerformanceFan(s, false, false, s.Timestamp); if (!pendingPerformanceFan.HasValue) throw new Exception("Performance waits for fan");
            TryApplyPerformanceFan(s, true, true, s.Timestamp); if (selectedMode != FanMode.Balanced || pendingPerformanceFan.HasValue) throw new Exception("Performance applies linked fan on connection");
            press("PerfHigh"); TryApplyPerformanceFan(s, true, true, s.Timestamp);
            if (selectedMode != FanMode.Cool || performanceUiMode != PerformanceMode.Performance) throw new Exception("Performance cooling linkage");
            press("ModeQuiet"); if (pendingPerformanceFan.HasValue || selectedMode != FanMode.Quiet) throw new Exception("Manual fan override");
            press("PerfSystem"); if (performanceUiMode != PerformanceMode.System || selectedMode != FanMode.Bios) throw new Exception("Restore system profile");
            PresentDevices(new Snapshot()); if (C<TextBlock>("BatteryHealth").Text != "— %" || C<StackPanel>("StorageCards").Children.Count != 0) throw new Exception("Missing device readings");
            foreach (string page in new[] { "Monitor", "Battery", "Storage" })
            { Navigate(page); root.Measure(new Size(960, 700)); root.Arrange(new Rect(0, 0, 960, 700)); root.UpdateLayout(); }
            press("NavSettings");
        }
    }
}
