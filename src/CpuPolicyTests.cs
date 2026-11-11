using System;
using System.Collections.Generic;
using System.IO;

namespace CarbonControl
{
    public static class CpuPolicyTests
    {
        private static int count;
        private static void Assert(bool value, string label)
        { count++; if (!value) throw new Exception("CPU policy: " + label); }
        private sealed class FakePolicy : IPowerPlanBackend
        {
            public Guid Active = Guid.NewGuid();
            public PowerPlanValues Values = new PowerPlanValues { AcBoost = 2, DcBoost = 0, AcFrequency = 3200, DcFrequency = 1800 };
            public int ActiveReads, ValueReads, Mutations;
            public bool Fail;
            public Action DuringRead;
            public Guid GetActiveScheme() { ActiveReads++; if (Fail) throw new IOException("read failed"); return Active; }
            public PowerPlanValues Read(Guid scheme)
            {
                ValueReads++; if (Fail) throw new IOException("read failed");
                if (DuringRead != null) DuringRead();
                return Values.Clone();
            }
            public Guid Duplicate(Guid source) { Mutations++; throw new Exception("Unexpected mutation"); }
            public void Write(Guid scheme, PowerPlanValues values) { Mutations++; throw new Exception("Unexpected mutation"); }
            public void Activate(Guid scheme) { Mutations++; throw new Exception("Unexpected mutation"); }
            public void Rename(Guid scheme, string name) { Mutations++; throw new Exception("Unexpected mutation"); }
            public void Delete(Guid scheme) { Mutations++; throw new Exception("Unexpected mutation"); }
        }
        private sealed class FakeFan : IFanDevice
        {
            public int Level = 128;
            public bool FailWrite, FailRead;
            public int ReadLevel() { if (FailRead) throw new IOException("read failed"); return Level; }
            public int? ReadRpm() { return 3000; }
            public void SetLevel(int level) { if (level != 128 && FailWrite) throw new IOException("write failed"); Level = level; }
            public void Dispose() { }
        }
        public static int Run()
        {
            count = 0;
            var fake = new FakePolicy(); DateTime now = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
            byte? source = 1; int sourceReads = 0, clockReads = 0;
            var monitor = new CpuPolicyMonitor(fake, () => { sourceReads++; return source; }, () => { clockReads++; return now; });
            Assert(fake.ActiveReads == 0 && fake.ValueReads == 0 && sourceReads == 0 && clockReads == 0, "constructor performs no OS/provider reads");
            var ac = monitor.Read();
            Assert(ac.OnAcPower == true && ac.BoostMode == 2 && ac.MaximumFrequencyMhz == 3200 && ac.SchemeName == "接电", "AC selects actual AC plan values");
            Assert(ac.Timestamp == now && fake.ValueReads == 1 && sourceReads == 2, "snapshot time and consistent-source validation");
            ac.BoostMode = 99; ac.MaximumFrequencyMhz = 9999;
            now = now.AddMilliseconds(1999); var cached = monitor.Read();
            Assert(cached.BoostMode == 2 && cached.MaximumFrequencyMhz == 3200 && fake.ValueReads == 1 && sourceReads == 2, "two-second cache returns a copy");
            source = 0; now = now.AddMilliseconds(1); var dc = monitor.Read();
            Assert(dc.OnAcPower == false && dc.BoostMode == 0 && dc.MaximumFrequencyMhz == 1800 && dc.SchemeName == "电池", "DC source refresh selects DC values at cache expiry");
            fake.Values.DcBoost = 4; fake.Values.DcFrequency = 2200; now = now.AddSeconds(2);
            dc = monitor.Read();
            Assert(dc.BoostMode == 4 && dc.MaximumFrequencyMhz == 2200, "external policy changes are read instead of app mode defaults");

            int previousReads = fake.ValueReads;
            foreach (byte? unknown in new byte?[] { 255, 2, null })
            {
                source = unknown; now = now.AddSeconds(2); var unavailable = monitor.Read();
                Assert(!unavailable.OnAcPower.HasValue && !unavailable.BoostMode.HasValue && !unavailable.MaximumFrequencyMhz.HasValue, "unknown source never guesses AC or DC");
            }
            Assert(fake.ValueReads == previousReads, "unknown source does not read an unused policy branch");
            source = 1; fake.Fail = true; now = now.AddSeconds(2); var failed = monitor.Read();
            Assert(failed.OnAcPower == true && !failed.BoostMode.HasValue && !failed.MaximumFrequencyMhz.HasValue, "policy API failure clears old limits");
            fake.Fail = false; now = now.AddSeconds(2); var recovered = monitor.Read();
            Assert(recovered.BoostMode == 2 && recovered.MaximumFrequencyMhz == 3200, "policy reads recover after failure");
            now = now.AddMinutes(-1); int readsBeforeRollback = fake.ValueReads;
            monitor.Read(); Assert(fake.ValueReads == readsBeforeRollback + 1, "clock rollback cannot keep a future cache entry");

            fake = new FakePolicy(); source = 1; bool switchedSource = false;
            monitor = new CpuPolicyMonitor(fake, () => source, () => now);
            fake.DuringRead = () => { if (!switchedSource) { source = 0; switchedSource = true; } };
            var transition = monitor.Read();
            Assert(transition.OnAcPower == false && transition.BoostMode == 0 && transition.MaximumFrequencyMhz == 1800 && fake.ValueReads == 2, "power-source transition is retried without mixing branches");
            fake = new FakePolicy(); bool switchedPlan = false;
            monitor = new CpuPolicyMonitor(fake, () => (byte?)1, () => now);
            fake.DuringRead = () => { if (!switchedPlan) { fake.Active = Guid.NewGuid(); fake.Values.AcBoost = 1; switchedPlan = true; } };
            var planTransition = monitor.Read();
            Assert(planTransition.BoostMode == 1 && fake.ValueReads == 2, "active plan transition is retried");
            fake = new FakePolicy(); fake.DuringRead = () => fake.Active = Guid.NewGuid();
            monitor = new CpuPolicyMonitor(fake, () => (byte?)1, () => now);
            var unstable = monitor.Read();
            Assert(!unstable.BoostMode.HasValue && !unstable.MaximumFrequencyMhz.HasValue && fake.ValueReads == 2, "continuously changing plan yields unknown values");
            var noSource = new CpuPolicyMonitor(fake, () => { throw new IOException("power source unavailable"); }, () => now).Read();
            Assert(!noSource.OnAcPower.HasValue && !noSource.BoostMode.HasValue, "power-source API failure remains unknown");
            Assert(fake.Mutations == 0, "monitor uses no power-plan mutation API");

            Assert(CpuPolicyMonitor.BoostLabel(0) == "禁用" && CpuPolicyMonitor.BoostLabel(1) == "启用" && CpuPolicyMonitor.BoostLabel(2) == "积极", "basic boost labels");
            Assert(CpuPolicyMonitor.BoostLabel(3) == "高效启用" && CpuPolicyMonitor.BoostLabel(4) == "高效积极" && CpuPolicyMonitor.BoostLabel(5) == "积极(保障)" && CpuPolicyMonitor.BoostLabel(6) == "高效积极(保障)", "all Windows boost enum labels");
            Assert(CpuPolicyMonitor.BoostLabel(null) == "—" && CpuPolicyMonitor.BoostLabel(99) == "—", "unknown boost enum is not presented as disabled");
            Assert(CpuPolicyMonitor.FrequencyLabel(0) == "系统调节", "zero maximum frequency is system-controlled");
            Assert(CpuPolicyMonitor.FrequencyLabel(3200) == "3.20 GHz" && CpuPolicyMonitor.FrequencyLabel(null) == "—", "MHz converted to GHz with unknown value preserved");
            fake = new FakePolicy(); monitor = new CpuPolicyMonitor(fake, () => (byte?)1, () => now);
            monitor.Read(); fake.Values.AcBoost = 0; fake.Values.AcFrequency = 0; monitor.Invalidate();
            Assert(fake.ValueReads == 1, "invalidating only clears the cache without native reads");
            var immediate = monitor.Read();
            Assert(immediate.BoostMode == 0 && immediate.MaximumFrequencyMhz == 0 && fake.ValueReads == 2, "mode change can reread policy without waiting two seconds");
            fake.Fail = true; monitor.Invalidate(); immediate = monitor.Read();
            Assert(!immediate.BoostMode.HasValue && !immediate.MaximumFrequencyMhz.HasValue, "failed immediate refresh never shows the previous mode settings");
            VerifyConfigurationFeedback();
            return count;
        }
        private static void VerifyConfigurationFeedback()
        {
            var engine = new Engine(); var fan = new FakeFan(); var control = new FanController(fan);
            var sample = new Snapshot { CpuTemperature = 60, Timestamp = DateTime.UtcNow };
            var curve = FanPolicy.Preset(FanMode.Balanced);
            Assert(engine.ConfigurationRevision == 0 && engine.ConfirmedConfigurationRevision == -1, "new engine has no confirmed fan configuration");
            engine.Configure(FanMode.Balanced, 0, curve); int first = engine.ConfigurationRevision;
            Assert(first == 1 && engine.ConfirmedConfigurationRevision == -1, "queued configuration is not reported as applied");
            control.Tick(FanMode.Balanced, 0, curve, sample, sample.Timestamp);
            engine.UpdateConfigurationConfirmation(FanMode.Balanced, first, control);
            Assert(engine.ConfirmedConfigurationRevision == first, "verified manual register acknowledges the requested fan strategy");
            var confirmation = engine.ReadFanConfirmation();
            Assert(confirmation.Revision == first && confirmation.Mode == FanMode.Balanced && confirmation.Level == control.ActualLevel &&
                confirmation.Ready && confirmation.Confirmed && confirmation.Status == control.Status, "fan acknowledgement and readback form one coherent snapshot");
            confirmation.Level = 7;
            Assert(engine.ReadFanConfirmation().Level == control.ActualLevel, "editing a returned confirmation cannot change engine state");
            engine.Configure(FanMode.Cool, 0, curve); int next = engine.ConfigurationRevision;
            confirmation = engine.ReadFanConfirmation();
            Assert(confirmation.Mode == FanMode.Cool && confirmation.Revision == -1 && !confirmation.Confirmed, "new mode immediately clears the old acknowledgement in its snapshot");
            engine.UpdateConfigurationConfirmation(FanMode.Balanced, first, control);
            Assert(engine.ConfirmedConfigurationRevision == -1, "an old fan iteration cannot acknowledge a newer request");
            control.ConfigurationChanged(); control.Tick(FanMode.Cool, 0, curve, sample, sample.Timestamp);
            engine.UpdateConfigurationConfirmation(FanMode.Cool, next, control);
            Assert(engine.ConfirmedConfigurationRevision == next, "latest configured strategy is confirmed independently");
            fan.FailRead = true; sample.Timestamp = sample.Timestamp.AddSeconds(5);
            control.Tick(FanMode.Cool, 0, curve, sample, sample.Timestamp);
            engine.UpdateConfigurationConfirmation(FanMode.Cool, next, control);
            Assert(engine.ConfirmedConfigurationRevision == -1, "a failed telemetry iteration clears an earlier success for the same revision");
            confirmation = engine.ReadFanConfirmation();
            Assert(confirmation.Revision == -1 && !confirmation.Confirmed && !confirmation.Level.HasValue && !engine.FanRpm.HasValue,
                "failed read withdraws acknowledgement and telemetry together");
            fan.FailRead = false; sample.Timestamp = sample.Timestamp.AddSeconds(1);
            control.Tick(FanMode.Cool, 0, curve, sample, sample.Timestamp);
            engine.UpdateConfigurationConfirmation(FanMode.Cool, next, control);
            Assert(engine.ConfirmedConfigurationRevision == next, "recovered telemetry can confirm the current revision again");
            engine.Configure(FanMode.Manual, 2, curve); fan.FailWrite = true;
            control.ConfigurationChanged(); control.Tick(FanMode.Manual, 2, curve, sample, sample.Timestamp);
            engine.UpdateConfigurationConfirmation(FanMode.Manual, engine.ConfigurationRevision, control);
            Assert(engine.ConfirmedConfigurationRevision == -1, "failed write followed by healthy BIOS recovery is not mode success");
            fan.FailWrite = false; engine.Configure(FanMode.Custom, 0, curve);
            control.ConfigurationChanged(); control.Tick(FanMode.Custom, 0, curve, new Snapshot(), sample.Timestamp);
            engine.UpdateConfigurationConfirmation(FanMode.Custom, engine.ConfigurationRevision, control);
            Assert(engine.ConfirmedConfigurationRevision == -1, "waiting for temperature cannot acknowledge a custom strategy");
            engine.Configure(FanMode.Bios, 0, curve);
            control.ConfigurationChanged(); control.Tick(FanMode.Bios, 0, curve, sample, sample.Timestamp);
            engine.UpdateConfigurationConfirmation(FanMode.Bios, engine.ConfigurationRevision, control);
            Assert(engine.ConfirmedConfigurationRevision == engine.ConfigurationRevision, "BIOS mode requires automatic register readback");
            fan.Level = 4; control = new FanController(fan); control.Tick(FanMode.Bios, 0, curve, sample, sample.Timestamp);
            engine.UpdateConfigurationConfirmation(FanMode.Bios, engine.ConfigurationRevision, control);
            Assert(engine.ConfirmedConfigurationRevision == -1, "an external manual mode is not confirmed as BIOS automatic");
            engine.Dispose(); engine.RefreshCpuLimits(); engine.Dispose();
            Assert(engine.ConfirmedConfigurationRevision == -1, "disposed engine ignores refresh requests and clears acknowledgements");
            confirmation = engine.ReadFanConfirmation();
            Assert(!confirmation.Ready && !confirmation.Confirmed && confirmation.Revision == -1 && !confirmation.Level.HasValue,
                "disposed engine exposes one consistently withdrawn confirmation");
        }
    }
}
