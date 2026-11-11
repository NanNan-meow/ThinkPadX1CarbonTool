using System;

namespace CarbonControl
{
    public static class PerformanceProfilesTests
    {
        private static int count;
        private static void Assert(bool value, string label)
        { count++; if (!value) throw new Exception("Performance profiles: " + label); }
        private static void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, label);
        }
        public static int Run()
        {
            count = 0;
            var profiles = PerformanceProfiles.All;
            Assert(profiles.Length == 4 && profiles[0].Mode == PerformanceMode.System && profiles[1].Mode == PerformanceMode.LowTemperature &&
                profiles[2].Mode == PerformanceMode.Balanced && profiles[3].Mode == PerformanceMode.Performance, "all displayed profiles are complete and ordered");
            var system = PerformanceProfiles.Get(PerformanceMode.System);
            Assert(system.RestoreOriginal && system.Fan == FanMode.Bios, "system restores the original power scheme and BIOS fan ownership");
            Assert(!system.AcBoost.HasValue && !system.DcBoost.HasValue && !system.AcMaxFrequencyMhz.HasValue && !system.DcMaxFrequencyMhz.HasValue,
                "restoration never invents original boost or frequency settings");
            foreach (PerformanceProfile profile in profiles)
                Assert(!profile.ChangesPackagePowerLimits, "package PL1 PL2 and Tau remain hardware settings for " + profile.Mode);
            var modes = new[] { PerformanceMode.LowTemperature, PerformanceMode.Balanced, PerformanceMode.Performance };
            var boosts = new uint[] { 0, 1, 2 };
            var fans = new[] { FanMode.Balanced, FanMode.Balanced, FanMode.Cool };
            for (int i = 0; i < modes.Length; i++)
            {
                var profile = PerformanceProfiles.Get(modes[i]);
                Assert(!profile.RestoreOriginal && profile.AcBoost == boosts[i] && profile.DcBoost == boosts[i], "AC and DC boost match the mode " + modes[i]);
                Assert(profile.AcMaxFrequencyMhz == 0 && profile.DcMaxFrequencyMhz == 0, "presets use system frequency management " + modes[i]);
                Assert(profile.Fan == fans[i], "preset cooling linkage " + modes[i]);
                PowerPlanValues backend = PerformanceController.ValuesFor(modes[i]);
                Assert(backend.AcBoost == profile.AcBoost && backend.DcBoost == profile.DcBoost &&
                    backend.AcFrequency == profile.AcMaxFrequencyMhz && backend.DcFrequency == profile.DcMaxFrequencyMhz,
                    "controller writes exactly the settings exposed to the UI " + modes[i]);
            }
            var changed = PerformanceProfiles.ValuesFor(PerformanceMode.LowTemperature);
            changed.AcBoost = 6; changed.DcFrequency = 1234;
            var fresh = PerformanceProfiles.ValuesFor(PerformanceMode.LowTemperature);
            Assert(fresh.AcBoost == 0 && fresh.DcFrequency == 0, "caller mutations cannot change a later preset application");
            profiles[0] = profiles[3];
            Assert(PerformanceProfiles.All[0].RestoreOriginal, "changing a UI list cannot replace a preset definition");
            Reject(() => PerformanceProfiles.ValuesFor(PerformanceMode.System), "system values require restoration rather than a fixed profile");
            Reject(() => PerformanceController.ValuesFor(PerformanceMode.System), "controller preserves system rejection semantics");
            Reject(() => PerformanceProfiles.Get((PerformanceMode)99), "undefined mode has no display definition");
            Reject(() => PerformanceController.ValuesFor((PerformanceMode)99), "undefined mode cannot produce power writes");
            return count;
        }
    }
}
