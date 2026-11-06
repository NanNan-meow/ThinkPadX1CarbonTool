using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CarbonControl
{
    public static class Tests
    {
        private static int count;
        private static void Assert(bool condition, string label) { count++; if (!condition) throw new Exception("FAILED: " + label); }
        private static void Reject(Action action, string label) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Assert(rejected, label); }
        private sealed class FakeFan : IFanDevice
        {
            public int Level = 128; public bool FailRead, FailWrite, FailRestore; public readonly List<int> Writes = new List<int>();
            public void SetLevel(int level) { Writes.Add(level); if ((level == 128 && FailRestore) || (level != 128 && FailWrite)) throw new IOException("injected failure"); Level = level; }
            public int ReadLevel() { if (FailRead) throw new IOException("injected failure"); return Level; }
            public int? ReadRpm() { return 3000; }
            public void Dispose() { }
        }
        public static int Run(string output)
        {
            // Tests never open hardware, write startup tasks or change the real fan.
            Log.DirectoryPath = Path.GetDirectoryName(Path.GetFullPath(output));
            try
            {
                DateTime now = DateTime.UtcNow;
                var policy = new FanPolicy(); var curve = FanPolicy.Preset(FanMode.Balanced);
                Assert(policy.Decide(FanMode.Bios, 7, curve, 70, now, now) == 128, "BIOS ownership");
                Assert(policy.Decide(FanMode.Manual, 4, curve, null, now, now) == 128, "missing temperature");
                Assert(policy.Decide(FanMode.Manual, 4, curve, 60, now.AddSeconds(-6), now) == 128, "stale sample");
                Assert(policy.Decide(FanMode.Manual, 4, curve, double.NaN, now, now) == 128, "NaN temperature");
                Assert(policy.Decide(FanMode.Manual, 4, curve, double.PositiveInfinity, now, now) == 128, "infinite temperature");
                Assert(policy.Decide(FanMode.Manual, 0, curve, 93, now, now) == 7, "hot manual override");
                Assert(policy.Decide(FanMode.Manual, 5, curve, 65, now, now) == 5, "manual level");
                Assert(policy.Decide(FanMode.Balanced, 0, curve, 79, now, now) == 5, "curve heating");
                Assert(policy.Decide(FanMode.Balanced, 0, curve, 77, now, now) == 5, "hysteresis holds");
                Assert(policy.Decide(FanMode.Balanced, 0, curve, 72, now, now) == 5, "cooling dwell begins");
                Assert(policy.Decide(FanMode.Balanced, 0, curve, 72, now.AddSeconds(7), now.AddSeconds(7)) == 5, "cooling dwell holds");
                Assert(policy.Decide(FanMode.Balanced, 0, curve, 72, now.AddSeconds(8), now.AddSeconds(8)) == 3, "cooling dwell releases");
                Assert(policy.Decide(FanMode.Cool, 0, curve, 72, now, now) == 6, "preset change immediate");
                foreach (FanMode m in new[] { FanMode.Quiet, FanMode.Balanced, FanMode.Cool })
                {
                    FanPolicy.Validate(FanPolicy.Preset(m)); policy.Reset(); int last = 0;
                    for (int t = 25; t <= 100; t++) { int level = policy.Decide(m, 0, curve, t, now, now); Assert(level >= last && level <= 7, "monotonic preset " + m + " " + t); last = level; }
                }
                Reject(() => FanPolicy.Validate(new List<CurvePoint>()), "empty curve");
                Reject(() => FanPolicy.Validate(new[] { new CurvePoint(50, 1), new CurvePoint(40, 7) }), "unordered curve");
                Reject(() => FanPolicy.Validate(new[] { new CurvePoint(40, 6), new CurvePoint(60, 4), new CurvePoint(80, 7) }), "descending level");
                Reject(() => FanPolicy.Validate(new[] { new CurvePoint(40, 0), new CurvePoint(94, 7) }), "late max fan");
                var fan = new FakeFan(); var control = new FanController(fan);
                var sample = new Snapshot { CpuTemperature = 60, Timestamp = now };
                control.Tick(FanMode.Bios, 0, curve, sample, now); Assert(fan.Writes.Count == 0, "read-only BIOS startup");
                control.Tick(FanMode.Manual, 5, curve, sample, now); Assert(fan.Level == 5 && control.OwnsFan, "manual applied");
                control.Tick(FanMode.Manual, 5, curve, sample, now.AddSeconds(6)); Assert(fan.Level == 128 && !control.OwnsFan, "stale restores BIOS");
                fan = new FakeFan { FailWrite = true }; control = new FanController(fan);
                control.Tick(FanMode.Manual, 5, curve, sample, now);
                Assert(fan.Writes.Last() == 128 && !control.OwnsFan, "partial write restores BIOS");
                fan = new FakeFan { FailRead = true }; control = new FanController(fan);
                control.Tick(FanMode.Manual, 5, curve, sample, now);
                Assert(fan.Level == 5 && control.OwnsFan && !control.Healthy, "transient read failure preserves confirmed setting without renewing watchdog");
                control.Tick(FanMode.Manual, 5, curve, new Snapshot { CpuTemperature = 60, Timestamp = now.AddSeconds(1) }, now.AddSeconds(1));
                control.Tick(FanMode.Manual, 5, curve, new Snapshot { CpuTemperature = 60, Timestamp = now.AddSeconds(3) }, now.AddSeconds(3));
                Assert(fan.Level == 128 && control.Rpm == null, "sustained read failures restore BIOS and clear RPM");
                fan = new FakeFan(); control = new FanController(fan);
                control.Tick(FanMode.Manual, 5, curve, sample, now); fan.FailRestore = true;
                control.Tick(FanMode.Bios, 0, curve, sample, now); Assert(control.OwnsFan && !control.Healthy, "failed restore retains ownership and withholds watchdog heartbeat");
                fan.FailRestore = false; control.Restore(); Assert(!control.OwnsFan && fan.Level == 128, "restore retry");
                var sensors = new List<SensorReading> {
                    new SensorReading { Hardware="Cpu", Kind="Temperature", Name="CPU Package", Value=58 },
                    new SensorReading { Hardware="Cpu", Kind="Temperature", Name="CPU Core #1 Distance to TjMax", Value=99 },
                    new SensorReading { Hardware="Cpu", Kind="Power", Name="CPU Package", Value=14 },
                    new SensorReading { Hardware="Cpu", Kind="Power", Name="CPU Graphics", Value=2 },
                    new SensorReading { Hardware="Cpu", Kind="Clock", Name="CPU Core #1", Value=2400 },
                    new SensorReading { Hardware="Cpu", Kind="Clock", Name="CPU Core #2", Value=2800 },
                    new SensorReading { Hardware="Cpu", Kind="Clock", Name="Bus Speed", Value=100 },
                    new SensorReading { Hardware="GpuIntel", Kind="Load", Name="D3D 3D", Value=25 },
                    new SensorReading { Hardware="GpuIntel", Kind="Load", Name="D3D Copy", Value=12 },
                    new SensorReading { Hardware="GpuIntel", Kind="Load", Name="GPU Memory", Value=60 },
                    new SensorReading { Hardware="GpuNvidia", Kind="Power", Name="GPU Power", Value=70 }
                };
                var mapped = SensorMap.Map(sensors);
                Assert(mapped.CpuTemperature == 58 && mapped.CpuPower == 14, "CPU package mapping");
                Assert(mapped.CpuClock == 2.6, "exclude bus clock");
                Assert(mapped.GpuLoad == 25 && mapped.GpuPower == 2, "Intel engine maximum and graphics domain power");
                sensors.RemoveAt(0); Assert(SensorMap.Map(sensors).CpuTemperature == null, "exclude distance-to-TjMax");
                Assert(SensorMap.Map(new List<SensorReading>()).GpuPower == null, "unavailable is not zero");
                var settings = new Settings(); string file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), "test-settings.xml");
                settings.Theme = "Dark"; settings.Save(file); settings.Theme = "Light"; settings.Save(file);
                Assert(Settings.Read(file).Theme == "Light", "atomic settings round trip");
                var editable = new CurveDraft(); editable.Add(40, 0); editable.Add(52, 1); editable.Add(73, 4);
                settings.Curve = editable.Points.ToArray(); settings.Save(file);
                var loaded = Settings.Read(file);
                Assert(loaded.Curve.Length == 9 && loaded.Curve[1].Temperature == 40 && loaded.Curve[6].Temperature == 73, "variable-length curve persists all nodes");
                policy.Reset(); Assert(policy.Decide(FanMode.Custom, 0, loaded.Curve, 74, now, now) == 4, "saved drawn curve drives policy");
                File.Delete(file);
                int curveAssertions = CurveEditingTests.Run();
                int slotAssertions = CurveSlotTests.Run(Path.GetDirectoryName(Path.GetFullPath(output)));
                int pollingAssertions = FanPollingTests.Run();
                int loadingAssertions = HardwareLoadingTests.Run(Path.GetDirectoryName(Path.GetFullPath(output)));
                File.WriteAllText(output, "PASS: " + (count + curveAssertions + slotAssertions + pollingAssertions + loadingAssertions) + " assertions. Fan policy, sensor mapping, failure recovery, settings, curve editing (" + curveAssertions + "), curve slots (" + slotAssertions + "), EC polling/protocol (" + pollingAssertions + "), component loading (" + loadingAssertions + ").\r\n"); return 0;
            }
            catch (Exception ex) { File.WriteAllText(output, ex.ToString()); return 1; }
        }
    }
}
