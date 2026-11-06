using System;
using System.IO;
using System.Linq;
using System.Xml;

namespace CarbonControl
{
    public static class StartupPolicyTests
    {
        private static int count;
        private static void Assert(bool condition, string label)
        { count++; if (!condition) throw new Exception("Startup policy: " + label); }
        private static void Reject<TException>(Action action, string label) where TException : Exception
        {
            bool rejected = false;
            try { action(); } catch (TException) { rejected = true; }
            Assert(rejected, label);
        }
        private static bool Same(CurvePoint[] a, CurvePoint[] b)
        {
            return a.Length == b.Length && a.Select((p, i) => p.Temperature == b[i].Temperature && p.Level == b[i].Level).All(v => v);
        }
        private static CurvePoint[] NinePoints()
        {
            return new[] { new CurvePoint(30, 0), new CurvePoint(38, 0), new CurvePoint(44, 1),
                new CurvePoint(50, 2), new CurvePoint(58, 3), new CurvePoint(65, 4),
                new CurvePoint(72, 5), new CurvePoint(79, 6), new CurvePoint(85, 7) };
        }
        private static Snapshot Sample(double? temperature, DateTime timestamp)
        { return new Snapshot { CpuTemperature = temperature, Timestamp = timestamp }; }

        public static int Run(string directory)
        {
            count = 0;
            Directory.CreateDirectory(directory);
            string prefix = Path.Combine(Path.GetFullPath(directory), "startup-test-" + Guid.NewGuid().ToString("N"));
            string file = prefix + ".xml", legacy = prefix + "-legacy.xml";
            var settings = new Settings();
            Assert(settings.StartupPolicy == StartupFanPolicy.Balanced, "new settings default to balanced");
            Assert(Settings.Read(prefix + "-missing.xml").StartupPolicy == StartupFanPolicy.Balanced, "missing configuration uses balanced");
            foreach (StartupFanPolicy selection in Enum.GetValues(typeof(StartupFanPolicy)))
            {
                settings.StartupPolicy = selection; settings.Save(file);
                Assert(Settings.Read(file).StartupPolicy == selection, "choice persists: " + selection);
            }
            settings.StartupPolicy = (StartupFanPolicy)999; settings.Save(file);
            Assert(settings.StartupPolicy == StartupFanPolicy.Balanced && Settings.Read(file).StartupPolicy == StartupFanPolicy.Balanced,
                "invalid in-memory enum normalized before saving");
            settings.StartupPolicy = (StartupFanPolicy)(-1);
            CurvePoint[] resolved;
            Assert(StartupPolicyResolver.Resolve(settings, out resolved) == FanMode.Balanced && settings.StartupPolicy == (StartupFanPolicy)(-1),
                "resolver fallback does not rewrite caller settings");

            settings = new Settings { Theme = "Dark", ManualLevel = 6, Curve = NinePoints(), StartupPolicy = StartupFanPolicy.Slot3 };
            settings.Save(legacy);
            var document = new XmlDocument(); document.Load(legacy);
            foreach (string name in new[] { "StartupPolicy", "CurveSlots", "SelectedCurveSlot" })
            {
                XmlNode node = document.DocumentElement.SelectSingleNode(name);
                if (node != null) document.DocumentElement.RemoveChild(node);
            }
            document.Save(legacy);
            var migrated = Settings.Read(legacy);
            Assert(migrated.StartupPolicy == StartupFanPolicy.Balanced, "old configuration gets default startup strategy");
            Assert(migrated.Theme == "Dark" && migrated.ManualLevel == 6 && Same(migrated.Curve, NinePoints()), "migration keeps existing preferences and active custom curve");
            Assert(migrated.CurveSlots[0].HasCurve && Same(migrated.CurveSlots[0].Curve, NinePoints()), "startup migration keeps legacy curve-slot migration");

            settings = new Settings { Curve = NinePoints() };
            var presets = new[] { StartupFanPolicy.Bios, StartupFanPolicy.Quiet, StartupFanPolicy.Balanced, StartupFanPolicy.Cool };
            var modes = new[] { FanMode.Bios, FanMode.Quiet, FanMode.Balanced, FanMode.Cool };
            for (int i = 0; i < presets.Length; i++)
            {
                settings.StartupPolicy = presets[i];
                Assert(StartupPolicyResolver.Resolve(settings, out resolved) == modes[i], "preset mode: " + presets[i]);
                Assert(Same(resolved, FanPolicy.Preset(modes[i]).ToArray()), "preset curve: " + presets[i]);
                FanPolicy.Validate(resolved);
            }
            settings.StartupPolicy = StartupFanPolicy.Custom;
            var originalCurve = settings.Curve;
            Assert(StartupPolicyResolver.Resolve(settings, out resolved) == FanMode.Custom && Same(resolved, settings.Curve), "current custom curve resolved");
            Assert(!object.ReferenceEquals(resolved, settings.Curve) && !object.ReferenceEquals(resolved[0], settings.Curve[0]), "current custom curve is deep copied");
            resolved[0].Temperature = 25;
            Assert(settings.Curve[0].Temperature == 30, "editing resolved custom curve leaves original intact");
            settings.SaveCurveSlot(0, "日常", NinePoints());
            settings.SaveCurveSlot(1, "强冷", FanPolicy.Preset(FanMode.Cool));
            settings.SaveCurveSlot(2, "安静", FanPolicy.Preset(FanMode.Quiet));
            var slots = settings.CurveSlots; var firstSlot = slots[0]; var firstSlotCurve = slots[0].Curve;
            int selectedSlot = settings.SelectedCurveSlot;
            for (int i = 0; i < 3; i++)
            {
                settings.StartupPolicy = (StartupFanPolicy)((int)StartupFanPolicy.Slot1 + i);
                Assert(StartupPolicyResolver.Resolve(settings, out resolved) == FanMode.Custom && Same(resolved, slots[i].Curve), "stored curve resolves: " + i);
                Assert(!object.ReferenceEquals(resolved, slots[i].Curve) && !object.ReferenceEquals(resolved[0], slots[i].Curve[0]), "slot deep copy: " + i);
            }
            resolved[0].Temperature = 25;
            Assert(settings.CurveSlots[2].Curve[0].Temperature == 35, "resolved slot does not modify persisted curve");
            Assert(object.ReferenceEquals(settings.Curve, originalCurve) && object.ReferenceEquals(settings.CurveSlots, slots) &&
                object.ReferenceEquals(settings.CurveSlots[0], firstSlot) && object.ReferenceEquals(settings.CurveSlots[0].Curve, firstSlotCurve) && settings.SelectedCurveSlot == selectedSlot,
                "resolution leaves active curve, slot identities, and selection unchanged");
            settings = new Settings { StartupPolicy = StartupFanPolicy.Slot2 };
            Reject<InvalidOperationException>(() => StartupPolicyResolver.Resolve(settings, out resolved), "empty startup slot rejected");
            settings.CurveSlots = null;
            Reject<InvalidOperationException>(() => StartupPolicyResolver.Resolve(settings, out resolved), "missing startup slots rejected");
            Reject<ArgumentNullException>(() => StartupPolicyResolver.Resolve(null, out resolved), "null settings rejected");

            DateTime now = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
            var sample = Sample(60, now); var gate = new StartupPolicyGate();
            Assert(gate.Pending, "startup gate initially pending");
            Assert(!gate.TryTake(false, true, sample, now) && gate.Pending, "wait for fan connection");
            Assert(!gate.TryTake(true, false, sample, now) && gate.Pending, "wait for fan read-back confirmation");
            Assert(!gate.TryTake(true, true, null, now) && gate.Pending, "wait for initial sensor sample");
            foreach (double? invalid in new double?[] { null, double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 126 })
                Assert(!gate.TryTake(true, true, Sample(invalid, now), now) && gate.Pending, "invalid temperature rejected: " + invalid);
            Assert(!gate.TryTake(true, true, Sample(60, now.AddSeconds(-5.1)), now) && gate.Pending, "stale temperature waits");
            Assert(!gate.TryTake(true, true, Sample(60, now.AddSeconds(1.1)), now) && gate.Pending, "future temperature waits");
            Assert(gate.TryTake(true, true, sample, now) && !gate.Pending, "connection plus current temperature releases startup exactly once");
            Assert(!gate.TryTake(true, true, sample, now.AddSeconds(1)), "startup is not applied every refresh");
            gate = new StartupPolicyGate(); gate.Cancel();
            Assert(!gate.Pending && !gate.TryTake(true, true, sample, now), "manual policy selection cancels pending startup");
            gate.Cancel(); Assert(!gate.Pending, "cancel is idempotent");
            foreach (double valid in new[] { 0.0, 125.0 })
            { gate = new StartupPolicyGate(); Assert(gate.TryTake(true, true, Sample(valid, now), now), "valid temperature boundary: " + valid); }
            gate = new StartupPolicyGate(); Assert(gate.TryTake(true, true, Sample(60, now.AddSeconds(-5)), now), "five-second freshness boundary accepted");
            gate = new StartupPolicyGate(); Assert(gate.TryTake(true, true, Sample(60, now.AddSeconds(1)), now), "one-second clock-skew boundary accepted");

            File.Delete(file); File.Delete(legacy);
            return count;
        }
    }
}
