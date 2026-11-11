using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CarbonControl
{
    public static class PerformanceControlTests
    {
        private static int count;
        private static void Assert(bool value, string label)
        { count++; if (!value) throw new Exception("Performance control: " + label); }
        private static void Reject(Action action, string label)
        { bool failed = false; try { action(); } catch { failed = true; } Assert(failed, label); }
        private sealed class FakePower : IPowerPlanBackend
        {
            public readonly Dictionary<Guid, PowerPlanValues> Plans = new Dictionary<Guid, PowerPlanValues>();
            public readonly List<Guid> Deleted = new List<Guid>();
            public readonly Guid Original = Guid.NewGuid();
            public Guid Active;
            public int Duplicates, Writes, Activations;
            public bool Unsupported, FailWrite, IgnoreWrite, FailActivate, IgnoreActivate, FailRestore, FailDelete, FailGetActive;
            public Guid? SwitchExternallyOnWrite;
            public Action<Guid> OnActivate, OnRead;
            public FakePower()
            {
                Active = Original;
                Plans.Add(Original, new PowerPlanValues { AcBoost = 4, DcBoost = 3, AcFrequency = 3300, DcFrequency = 2000 });
            }
            public Guid GetActiveScheme() { if (FailGetActive) throw new IOException("active plan unavailable"); return Active; }
            public PowerPlanValues Read(Guid scheme)
            {
                if (Unsupported) throw new IOException("unsupported setting");
                var values = Plans[scheme].Clone();
                if (OnRead != null) OnRead(scheme);
                return values;
            }
            public Guid Duplicate(Guid source)
            { Duplicates++; var id = Guid.NewGuid(); Plans.Add(id, Plans[source].Clone()); return id; }
            public void Write(Guid scheme, PowerPlanValues values)
            {
                Writes++;
                if (FailWrite) { Plans[scheme].AcBoost = values.AcBoost; throw new IOException("partial write failed"); }
                if (!IgnoreWrite) Plans[scheme] = values.Clone();
                if (SwitchExternallyOnWrite.HasValue) Active = SwitchExternallyOnWrite.Value;
            }
            public void Activate(Guid scheme)
            {
                Activations++;
                if (FailActivate || (FailRestore && scheme == Original)) throw new IOException("activation failed");
                if (!IgnoreActivate) Active = scheme;
                if (OnActivate != null) OnActivate(scheme);
            }
            public void Rename(Guid scheme, string name) { if (!Plans.ContainsKey(scheme)) throw new IOException("missing scheme"); }
            public void Delete(Guid scheme)
            {
                if (scheme == Active) throw new IOException("cannot remove active plan");
                if (FailDelete) throw new IOException("cleanup failed");
                Deleted.Add(scheme); Plans.Remove(scheme);
            }
            public Guid External()
            { var id = Guid.NewGuid(); Plans[id] = new PowerPlanValues { AcBoost = 1, DcBoost = 0 }; return id; }
        }
        private static string PathFor(string root, string name) { return Path.Combine(root, name + ".xml"); }
        private static string ReadOnlyState(FakePower fake, string path)
        {
            string plans = string.Join(";", fake.Plans.OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value.AcBoost + "," + x.Value.DcBoost + "," + x.Value.AcFrequency + "," + x.Value.DcFrequency));
            return fake.Active + "|" + plans + "|" + fake.Writes + "," + fake.Activations + "," + fake.Duplicates + "|" +
                string.Join(",", fake.Deleted) + "|" + (File.Exists(path) ? File.ReadAllText(path) : "no journal");
        }
        public static int Run(string directory)
        {
            count = 0;
            string folder = Path.Combine(Path.GetFullPath(directory), "release", "performance-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var fake = new FakePower(); string path = PathFor(folder, "normal");
            var original = fake.Plans[fake.Original].Clone();
            var c = new PerformanceController(fake, path);
            Assert(c.Mode == PerformanceMode.System && fake.Writes == 0 && fake.Activations == 0 && fake.Duplicates == 0 && !File.Exists(path), "constructing does not change the machine");
            c.Apply(PerformanceMode.LowTemperature); Guid low = fake.Active;
            Assert(low != fake.Original && fake.Plans[low].AcBoost == 0 && fake.Plans[low].DcBoost == 0, "low temperature disables boost on both power sources");
            Assert(fake.Plans[low].AcFrequency == 0 && fake.Plans[low].DcFrequency == 0, "no unrelated fixed frequency cap");
            Assert(fake.Plans[fake.Original].Same(original), "original plan values remain intact");
            Assert(c.Mode == PerformanceMode.LowTemperature && File.Exists(path), "confirmed state and recovery journal recorded");
            c.Apply(PerformanceMode.LowTemperature);
            Assert(fake.Duplicates == 1 && fake.Writes == 1, "reapplying confirmed profile is a no-op");
            c.Apply(PerformanceMode.Balanced); Guid balanced = fake.Active;
            Assert(balanced != low && fake.Plans[balanced].AcBoost == 1 && fake.Plans[balanced].DcBoost == 1, "balanced profile enables boost");
            Assert(!fake.Plans.ContainsKey(low) && fake.Deleted.Contains(low), "old owned plan cleaned after successful switch");
            c.Apply(PerformanceMode.Performance);
            Assert(fake.Plans[fake.Active].AcBoost == 2 && fake.Plans[fake.Active].DcBoost == 2 && c.Mode == PerformanceMode.Performance, "performance requests aggressive boost");
            c.Apply(PerformanceMode.System);
            Assert(fake.Active == fake.Original && fake.Plans.Count == 1 && !File.Exists(path) && c.Mode == PerformanceMode.System, "system restores exact original plan and removes journal");
            c.Dispose(); c.Dispose(); Assert(fake.Plans[fake.Original].Same(original), "dispose is idempotent");
            Reject(() => c.Apply(PerformanceMode.Balanced), "disposed controller rejects changes");

            fake = new FakePower { Unsupported = true }; path = PathFor(folder, "unsupported"); c = new PerformanceController(fake, path);
            Reject(() => c.Apply(PerformanceMode.LowTemperature), "unsupported read aborts profile");
            Assert(fake.Duplicates == 0 && fake.Active == fake.Original && !File.Exists(path), "unsupported interface does not create or activate a plan");
            c.Dispose();

            fake = new FakePower { FailWrite = true }; path = PathFor(folder, "write-failure"); c = new PerformanceController(fake, path);
            Reject(() => c.Apply(PerformanceMode.Performance), "partial write failure is surfaced");
            Assert(fake.Active == fake.Original && fake.Plans.Count == 1 && c.Mode == PerformanceMode.System && !File.Exists(path), "partial writes affect inactive clone only and are cleaned");
            c.Dispose();

            fake = new FakePower { IgnoreWrite = true }; path = PathFor(folder, "readback"); c = new PerformanceController(fake, path);
            Reject(() => c.Apply(PerformanceMode.LowTemperature), "readback mismatch rejected");
            Assert(fake.Activations == 0 && fake.Plans.Count == 1 && !File.Exists(path), "unconfirmed settings are never activated");
            c.Dispose();

            fake = new FakePower { FailActivate = true }; path = PathFor(folder, "activate-failure"); c = new PerformanceController(fake, path);
            Reject(() => c.Apply(PerformanceMode.Balanced), "activation failure surfaced");
            Assert(fake.Active == fake.Original && fake.Plans.Count == 1 && !File.Exists(path), "failed activation leaves original active"); c.Dispose();

            fake = new FakePower { IgnoreActivate = true }; path = PathFor(folder, "activate-mismatch"); c = new PerformanceController(fake, path);
            Reject(() => c.Apply(PerformanceMode.Performance), "active plan is verified after activation");
            Assert(fake.Active == fake.Original && c.Mode == PerformanceMode.System, "ignored activation does not report performance applied"); c.Dispose();

            fake = new FakePower(); path = PathFor(folder, "activate-readback"); c = new PerformanceController(fake, path);
            fake.OnActivate = id => { if (id != fake.Original) fake.Plans[id].DcBoost = 4; };
            Reject(() => c.Apply(PerformanceMode.LowTemperature), "settings changed during activation are rejected");
            Assert(fake.Active == fake.Original && c.Mode == PerformanceMode.System && fake.Plans.Count == 1 && !File.Exists(path), "post-activation mismatch restores original plan"); c.Dispose();

            fake = new FakePower(); path = PathFor(folder, "committed-journal-refresh"); c = new PerformanceController(fake, path);
            FileStream journalLock = null;
            fake.OnActivate = id => { if (id != fake.Original) journalLock = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); };
            try
            {
                c.Apply(PerformanceMode.LowTemperature);
                Assert(c.Mode == PerformanceMode.LowTemperature && fake.Active != fake.Original, "committed mode succeeds if cleanup journal refresh fails");
            }
            finally { if (journalLock != null) journalLock.Dispose(); }
            Assert(File.Exists(path), "pre-activation journal remains available after failed refresh");
            fake.OnActivate = null; c.Dispose();
            Assert(fake.Active == fake.Original && !File.Exists(path), "original journal still restores committed mode");

            fake = new FakePower(); path = PathFor(folder, "restored-journal-cleanup"); c = new PerformanceController(fake, path);
            c.Apply(PerformanceMode.LowTemperature); journalLock = null;
            fake.OnActivate = id => { if (id == fake.Original) journalLock = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); };
            try
            {
                c.Apply(PerformanceMode.System);
                Assert(fake.Active == fake.Original && c.Mode == PerformanceMode.System, "confirmed restoration succeeds if old journal cannot yet be removed");
            }
            finally { if (journalLock != null) journalLock.Dispose(); }
            fake.OnActivate = null; c.Apply(PerformanceMode.System);
            Assert(!File.Exists(path), "restoration journal cleanup can be retried"); c.Dispose();

            fake = new FakePower(); path = PathFor(folder, "switch-failure"); c = new PerformanceController(fake, path);
            c.Apply(PerformanceMode.LowTemperature); Guid previous = fake.Active; fake.FailWrite = true;
            Reject(() => c.Apply(PerformanceMode.Performance), "failed switch surfaced");
            Assert(fake.Active == previous && c.Mode == PerformanceMode.LowTemperature && fake.Plans.Count == 2 && File.Exists(path), "failed switch preserves the already active application mode");
            fake.FailWrite = false; c.Dispose(); Assert(fake.Active == fake.Original && !File.Exists(path), "prior session still restores after failed switch");

            fake = new FakePower(); path = PathFor(folder, "external"); c = new PerformanceController(fake, path);
            c.Apply(PerformanceMode.LowTemperature); Guid external = fake.External(); fake.Active = external; c.Apply(PerformanceMode.System);
            Assert(fake.Active == external && fake.Plans.ContainsKey(fake.Original) && !File.Exists(path), "restoration respects an externally selected power plan");
            c.Dispose();

            fake = new FakePower(); path = PathFor(folder, "refresh-external"); c = new PerformanceController(fake, path);
            c.Apply(PerformanceMode.LowTemperature); external = fake.External(); fake.Active = external;
            int writesBeforeRefresh = fake.Writes, activationsBeforeRefresh = fake.Activations, duplicatesBeforeRefresh = fake.Duplicates;
            Guid[] plansBeforeRefresh = fake.Plans.Keys.OrderBy(x => x).ToArray();
            c.RefreshActiveMode();
            Assert(c.Mode == PerformanceMode.System && c.Status == "使用系统电源设置", "refresh detects external power plan selection");
            Assert(fake.Active == external && fake.Writes == writesBeforeRefresh && fake.Activations == activationsBeforeRefresh &&
                fake.Duplicates == duplicatesBeforeRefresh && fake.Plans.Keys.OrderBy(x => x).SequenceEqual(plansBeforeRefresh) && File.Exists(path),
                "refresh does not activate, modify or clean any power plan");
            c.Dispose();

            fake = new FakePower(); path = PathFor(folder, "mode-active-confirmation"); c = new PerformanceController(fake, path);
            string beforeConfirmation = ReadOnlyState(fake, path);
            Assert(c.IsModeActive(PerformanceMode.System) && !c.IsModeActive(PerformanceMode.Performance), "system means Windows management rather than a fixed profile");
            Assert(ReadOnlyState(fake, path) == beforeConfirmation, "checking an idle controller never creates or modifies a power plan");
            c.Apply(PerformanceMode.LowTemperature); Guid confirmedPlan = fake.Active;
            beforeConfirmation = ReadOnlyState(fake, path);
            Assert(c.IsModeActive(PerformanceMode.LowTemperature), "owned active scheme and matching values confirm the expected mode");
            Assert(!c.IsModeActive(PerformanceMode.Performance) && !c.IsModeActive(PerformanceMode.System), "a different expected mode is not reported as active");
            Assert(ReadOnlyState(fake, path) == beforeConfirmation, "successful validation does not write activate duplicate delete or alter the journal");
            fake.Plans[confirmedPlan].DcBoost = 2;
            beforeConfirmation = ReadOnlyState(fake, path);
            Assert(!c.IsModeActive(PerformanceMode.LowTemperature) && c.Mode == PerformanceMode.LowTemperature, "external value edits inside an owned plan fail confirmation without changing the selected mode");
            Assert(ReadOnlyState(fake, path) == beforeConfirmation, "mismatch validation leaves external edits and recovery information intact");
            fake.Plans[confirmedPlan].DcBoost = 0; fake.Unsupported = true;
            beforeConfirmation = ReadOnlyState(fake, path);
            Assert(!c.IsModeActive(PerformanceMode.LowTemperature), "unreadable values cannot confirm a mode");
            Assert(ReadOnlyState(fake, path) == beforeConfirmation, "failed value reads do not modify power state");
            fake.Unsupported = false; fake.FailGetActive = true;
            beforeConfirmation = ReadOnlyState(fake, path);
            Assert(!c.IsModeActive(PerformanceMode.LowTemperature), "unreadable active scheme cannot confirm a mode");
            Assert(ReadOnlyState(fake, path) == beforeConfirmation, "failed active-scheme reads do not modify power state");
            fake.FailGetActive = false;
            external = fake.External(); fake.Active = external;
            beforeConfirmation = ReadOnlyState(fake, path);
            Assert(!c.IsModeActive(PerformanceMode.LowTemperature) && c.Mode == PerformanceMode.System && c.IsModeActive(PerformanceMode.System), "external scheme selection invalidates application mode and returns Windows management");
            Assert(ReadOnlyState(fake, path) == beforeConfirmation, "external-selection validation does not clean up or replace any plan");
            c.Dispose();

            fake = new FakePower(); path = PathFor(folder, "mode-active-race"); c = new PerformanceController(fake, path);
            c.Apply(PerformanceMode.Performance); confirmedPlan = fake.Active; external = fake.External();
            int writesBeforeConfirmation = fake.Writes, activationsBeforeConfirmation = fake.Activations, duplicatesBeforeConfirmation = fake.Duplicates;
            string journalBeforeConfirmation = File.ReadAllText(path);
            fake.OnRead = id => { if (id == confirmedPlan) fake.Active = external; };
            Assert(!c.IsModeActive(PerformanceMode.Performance), "scheme change during readback cannot acknowledge the previous profile");
            Assert(fake.Active == external && fake.Writes == writesBeforeConfirmation && fake.Activations == activationsBeforeConfirmation &&
                fake.Duplicates == duplicatesBeforeConfirmation && File.ReadAllText(path) == journalBeforeConfirmation, "race validation preserves the external selection without writes or journal changes");
            fake.OnRead = null; c.Dispose();

            fake = new FakePower(); path = PathFor(folder, "external-new-profile"); c = new PerformanceController(fake, path);
            c.Apply(PerformanceMode.Balanced); external = fake.External(); fake.Active = external;
            c.Apply(PerformanceMode.Performance); c.Apply(PerformanceMode.System);
            Assert(fake.Active == external && !File.Exists(path), "subsequent profile uses latest external plan as its restore target"); c.Dispose();

            fake = new FakePower(); path = PathFor(folder, "race"); external = fake.External(); fake.SwitchExternallyOnWrite = external; c = new PerformanceController(fake, path);
            Reject(() => c.Apply(PerformanceMode.LowTemperature), "external selection during transaction is detected");
            Assert(fake.Active == external && fake.Plans.Count == 2 && !File.Exists(path), "transaction does not override external selection"); c.Dispose();

            fake = new FakePower(); path = PathFor(folder, "crash"); var crashed = new PerformanceController(fake, path); crashed.Apply(PerformanceMode.Performance);
            var recovered = new PerformanceController(fake, path);
            Assert(fake.Active != fake.Original && File.Exists(path), "constructing recovery controller is read-only");
            recovered.RecoverPreviousSession();
            Assert(fake.Active == fake.Original && fake.Plans.Count == 1 && !File.Exists(path), "explicit recovery restores previous process plan"); recovered.Dispose();

            fake = new FakePower(); path = PathFor(folder, "crash-external"); crashed = new PerformanceController(fake, path); crashed.Apply(PerformanceMode.Balanced);
            external = fake.External(); fake.Active = external; recovered = new PerformanceController(fake, path); recovered.RecoverPreviousSession();
            Assert(fake.Active == external && fake.Plans.Count == 2 && !File.Exists(path), "crash recovery respects external selection"); recovered.Dispose();

            fake = new FakePower(); path = PathFor(folder, "recovery-retry"); c = new PerformanceController(fake, path); c.Apply(PerformanceMode.Balanced); fake.FailRestore = true;
            Reject(() => c.Apply(PerformanceMode.System), "restore failure surfaced");
            Assert(File.Exists(path) && fake.Active != fake.Original, "unconfirmed restore retains recovery journal");
            fake.FailRestore = false; c.Apply(PerformanceMode.System);
            Assert(fake.Active == fake.Original && !File.Exists(path), "restore retry succeeds"); c.Dispose();

            fake = new FakePower(); path = PathFor(folder, "cleanup-retry"); c = new PerformanceController(fake, path); c.Apply(PerformanceMode.LowTemperature); fake.FailDelete = true;
            c.Apply(PerformanceMode.System);
            Assert(fake.Active == fake.Original && c.Mode == PerformanceMode.System && File.Exists(path), "cleanup failure preserves original active plan and recovery record");
            fake.FailDelete = false; c.Apply(PerformanceMode.System); Assert(!File.Exists(path) && fake.Plans.Count == 1, "cleanup can be retried"); c.Dispose();

            Reject(() => PerformanceController.ValuesFor(PerformanceMode.System), "system profile is restoration rather than hardcoded values");
            fake = new FakePower(); c = new PerformanceController(fake, PathFor(folder, "invalid"));
            Reject(() => c.Apply((PerformanceMode)99), "undefined modes rejected"); Assert(fake.Duplicates == 0, "invalid modes never touch power settings"); c.Dispose();
            return count;
        }
    }
}
