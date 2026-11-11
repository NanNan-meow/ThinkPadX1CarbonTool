using System;
using System.Globalization;
using System.Text;
using System.Windows.Controls;

namespace CarbonControl
{
    public sealed partial class MainUi
    {
        private void PresentCpuLimits(Snapshot sample)
        {
            var limits = sample.CpuLimits ?? new CpuLimitReading();
            var policy = sample.CpuPolicy ?? new CpuPolicyReading();
            T("CpuLimitPl1", LimitPower(limits.LongPowerW, limits.LongEnabled));
            T("CpuLimitPl2", LimitPower(limits.ShortPowerW, limits.ShortEnabled));
            T("CpuLimitTau", Format(limits.TimeWindowSeconds, "0.##", "s"));
            T("CpuLimitBoost", CpuPolicyMonitor.BoostLabel(policy.BoostMode));
            T("CpuLimitFrequency", !policy.MaximumFrequencyMhz.HasValue ? "—" : policy.MaximumFrequencyMhz == 0 ? "系统调节" :
                (policy.MaximumFrequencyMhz.Value / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " GHz");
            string activity = limits.ThermalThrottling == true && limits.PowerThrottling == true ? "温度 / 功耗限制" :
                limits.ThermalThrottling == true ? "温度限制" : limits.PowerThrottling == true ? "功耗限制" :
                limits.ThermalThrottling == false && limits.PowerThrottling == false ? "未触发" : "—";
            T("CpuLimitActivity", activity);
            C<TextBlock>("CpuLimitActivity").SetResourceReference(TextBlock.ForegroundProperty, activity == "未触发" ? "Green" : activity == "—" ? "Text" : "Accent");
            string supply = policy.OnAcPower == true ? "接电" : policy.OnAcPower == false ? "电池" : "";
            string source = string.IsNullOrEmpty(limits.Source) ? supply : limits.Source + (supply.Length == 0 ? "" : " · " + supply);
            if (limits.Locked == true) source += " · 已锁定";
            T("CpuLimitSource", source);
        }
        private static string LimitPower(double? watts, bool? enabled)
        { return enabled == false ? "未启用" : Format(watts, "0.##", "W"); }
        private static void AppendCpuLimits(StringBuilder text, Snapshot sample)
        {
            var l = sample.CpuLimits ?? new CpuLimitReading(); var p = sample.CpuPolicy ?? new CpuPolicyReading();
            text.AppendLine("CPU limits: PL1=" + l.LongPowerW + "W (enabled=" + l.LongEnabled + ") | PL2=" + l.ShortPowerW +
                "W (enabled=" + l.ShortEnabled + ") | Tau=" + l.TimeWindowSeconds + "s | Locked=" + l.Locked + " | Source=" + l.Source);
            text.AppendLine("CPU policy: AC=" + p.OnAcPower + " | Boost=" + p.BoostMode + " | Max MHz=" + p.MaximumFrequencyMhz);
            text.AppendLine("CPU limiting: Thermal=" + l.ThermalThrottling + " | Power=" + l.PowerThrottling);
        }
        private void TestCpuLimitUi()
        {
            var sample = new Snapshot { CpuLimits = new CpuLimitReading { LongPowerW = 15, ShortPowerW = 44, TimeWindowSeconds = 28,
                LongEnabled = true, ShortEnabled = true, ThermalThrottling = false, PowerThrottling = true, Source = "封装 MSR" },
                CpuPolicy = new CpuPolicyReading { BoostMode = 0, MaximumFrequencyMhz = 2200, OnAcPower = false } };
            PresentCpuLimits(sample);
            if (C<TextBlock>("CpuLimitPl1").Text != "15 W" || C<TextBlock>("CpuLimitPl2").Text != "44 W" ||
                C<TextBlock>("CpuLimitTau").Text != "28 s" || C<TextBlock>("CpuLimitFrequency").Text != "2.2 GHz" ||
                C<TextBlock>("CpuLimitActivity").Text != "功耗限制" || !C<TextBlock>("CpuLimitSource").Text.Contains("电池"))
                throw new Exception("Live CPU limit presentation");
            sample.CpuPolicy.MaximumFrequencyMhz = 0; sample.CpuLimits.LongEnabled = false; PresentCpuLimits(sample);
            if (C<TextBlock>("CpuLimitFrequency").Text != "系统调节" || C<TextBlock>("CpuLimitPl1").Text != "未启用") throw new Exception("Disabled and system CPU limits");
            PresentCpuLimits(new Snapshot());
            if (C<TextBlock>("CpuLimitPl1").Text != "— W" || C<TextBlock>("CpuLimitBoost").Text != "—" || C<TextBlock>("CpuLimitActivity").Text != "—")
                throw new Exception("Missing CPU limits");
        }
    }
}
