using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace CarbonControl
{
    public sealed partial class MainUi
    {
        private void TestModeInteractionUi(Grid root, Action<string> press)
        {
            press("NavMonitor"); root.UpdateLayout();
            if (FindVisual<Button>(C<StackPanel>("MonitorPage")) != null) throw new Exception("Monitor contains adjustment buttons");
            press("NavPerformance");
            if (activePage != "Performance" || C<StackPanel>("ModeDetailsRows").Children.Count != 4) throw new Exception("Performance page and profile definitions");
            C<Expander>("ModeDetailsExpander").IsExpanded = true;
            press("PerfLow");
            if (!C<TextBlock>("ModeDetailsCurrent").Text.Contains("0/0") || !C<TextBlock>("ModeDetailsCurrent").Text.Contains("沿用硬件"))
                throw new Exception("Mode detail targets do not match backend");
            DateTime now = DateTime.UtcNow;
            var sample = new Snapshot { CpuPolicy = new CpuPolicyReading { BoostMode = 2, MaximumFrequencyMhz = 0, OnAcPower = true, Timestamp = now.AddSeconds(-2) },
                CpuLimits = new CpuLimitReading { LongPowerW = 15, LongEnabled = true } };
            cpuPolicyRefreshRequested = now; PresentCpuLimits(sample);
            if (C<TextBlock>("CpuLimitBoost").Text != "读取中") throw new Exception("Old CPU policy shown after mode change");
            sample.CpuPolicy.Timestamp = now.AddMilliseconds(1); sample.CpuPolicy.BoostMode = 0; PresentCpuLimits(sample);
            if (C<TextBlock>("CpuLimitBoost").Text != "禁用" || C<TextBlock>("CpuLimitPl1").Text != "15 W" || !C<TextBlock>("ModeDetailsPolicyReadback").Text.Contains("读回已确认"))
                throw new Exception("New mode readback and retained package limit");
            press("PerfLow"); TryApplyPerformanceFan(new Snapshot { CpuTemperature = 60, Timestamp = DateTime.UtcNow }, true, true, DateTime.UtcNow);
            performanceUiMode = PerformanceMode.System;
            UpdateFanAction(0, FanMode.Balanced, 2, true, "已应用", DateTime.UtcNow);
            if (actionFeedback.Kind != ActionFeedbackKind.Error) throw new Exception("Old mode acknowledged after external mode change");
            NotifyFeedback(false, "模式应用失败"); performanceFeedbackVersion = actionFeedback.Version;
            TrackFanAction(FanMode.Balanced, true); UpdateFanAction(0, FanMode.Balanced, 2, true, "已应用", DateTime.UtcNow);
            if (actionFeedback.Kind != ActionFeedbackKind.Error) throw new Exception("Failed mode token reused by old fan linkage");
            press("NavFan"); C<Slider>("ManualSlider").Value = 4; press("ApplyManual");
            if (actionFeedback.Kind != ActionFeedbackKind.Pending) throw new Exception("Fan command reports success before readback");
            UpdateFanAction(-1, FanMode.Manual, 3, true, "旧档位", DateTime.UtcNow);
            if (actionFeedback.Kind != ActionFeedbackKind.Pending) throw new Exception("Old fan revision accepted as new command");
            UpdateFanAction(0, FanMode.Manual, 4, true, "已应用", DateTime.UtcNow);
            if (actionFeedback.Kind != ActionFeedbackKind.Success || !actionFeedback.Text.Contains("4 档")) throw new Exception("Verified fan action feedback");
            press("SaveCurve"); UpdateFanAction(0, FanMode.Custom, 2, true, "已应用", DateTime.UtcNow);
            if (!C<TextBlock>("CurveMessage").Text.Contains("曲线已应用")) throw new Exception("Persistent curve readback feedback");
            press("SaveCurve"); curveDraft.Select(2); curveDraft.MoveSelected(60, 3);
            UpdateFanAction(0, FanMode.Custom, 2, true, "已应用", DateTime.UtcNow);
            if (!C<TextBlock>("CurveMessage").Text.Contains("曲线已修改")) throw new Exception("Old curve readback overwrote a newer edit");
            press("NavSettings"); press("ThemeLight");
            if (actionFeedback.Kind != ActionFeedbackKind.Success || !actionFeedback.Text.Contains("浅色")) throw new Exception("Theme setting feedback");
            var caption = C<Border>("AppTitleBar");
            if (((SolidColorBrush)caption.Background).Color != ((SolidColorBrush)Brush("Bg")).Color) throw new Exception("Light title bar does not use application palette");
            var chrome = WindowChrome.GetWindowChrome(window);
            if (chrome == null || chrome.CaptionHeight != 32 || chrome.UseAeroCaptionButtons) throw new Exception("Custom title bar and resize setup");
            if (C<Button>("WindowMinimize") == null || C<Button>("WindowMaximize") == null || C<Button>("WindowClose") == null)
                throw new Exception("Window control buttons missing");
            foreach (string page in new[] { "Monitor", "Performance", "Fan", "Settings" })
            { Navigate(page); root.Measure(new Size(960, 700)); root.Arrange(new Rect(0, 0, 960, 700)); root.UpdateLayout(); }
            press("NavSettings");
        }
    }
}
