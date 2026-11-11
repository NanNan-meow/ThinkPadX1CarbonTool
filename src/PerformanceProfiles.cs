using System;

namespace CarbonControl
{
    public sealed class PerformanceProfile
    {
        public PerformanceMode Mode { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public uint? AcBoost { get; private set; }
        public uint? DcBoost { get; private set; }
        public uint? AcMaxFrequencyMhz { get; private set; }
        public uint? DcMaxFrequencyMhz { get; private set; }
        public FanMode Fan { get; private set; }
        public bool RestoreOriginal { get; private set; }
        public bool ChangesPackagePowerLimits { get { return false; } }

        internal PerformanceProfile(PerformanceMode mode, string name, string description, uint? boost, FanMode fan, bool restoreOriginal)
        {
            Mode = mode; Name = name; Description = description;
            AcBoost = DcBoost = boost;
            AcMaxFrequencyMhz = DcMaxFrequencyMhz = restoreOriginal ? (uint?)null : 0u;
            Fan = fan; RestoreOriginal = restoreOriginal;
        }
    }

    // The control backend and mode-details UI use the same definitions.
    // All presets retain the processor's package PL1/PL2/Tau settings.
    public static class PerformanceProfiles
    {
        public static PerformanceProfile[] All
        {
            get { return new[] { Get(PerformanceMode.System), Get(PerformanceMode.LowTemperature), Get(PerformanceMode.Balanced), Get(PerformanceMode.Performance) }; }
        }
        public static PerformanceProfile Get(PerformanceMode mode)
        {
            switch (mode)
            {
                case PerformanceMode.System:
                    return new PerformanceProfile(mode, "系统", "恢复原电源方案，风扇由系统调速", null, FanMode.Bios, true);
                case PerformanceMode.LowTemperature:
                    return new PerformanceProfile(mode, "低温办公", "关闭睿频，使用均衡风扇曲线", 0u, FanMode.Balanced, false);
                case PerformanceMode.Balanced:
                    return new PerformanceProfile(mode, "均衡", "允许睿频，使用均衡风扇曲线", 1u, FanMode.Balanced, false);
                case PerformanceMode.Performance:
                    return new PerformanceProfile(mode, "性能", "积极睿频，使用强冷风扇曲线", 2u, FanMode.Cool, false);
                default:
                    throw new ArgumentOutOfRangeException("mode");
            }
        }
        public static PowerPlanValues ValuesFor(PerformanceMode mode)
        {
            PerformanceProfile profile = Get(mode);
            if (profile.RestoreOriginal) throw new ArgumentOutOfRangeException("mode");
            return new PowerPlanValues { AcBoost = profile.AcBoost.Value, DcBoost = profile.DcBoost.Value,
                AcFrequency = profile.AcMaxFrequencyMhz.Value, DcFrequency = profile.DcMaxFrequencyMhz.Value };
        }
    }
}
