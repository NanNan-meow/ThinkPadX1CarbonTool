using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace CarbonControl
{
    public static class FanPollingTests
    {
        private static int count;
        private static void Assert(bool value, string label) { count++; if (!value) throw new Exception("Fan polling: " + label); }
        private sealed class FakeFan : IFanDevice, IFanTelemetryDevice
        {
            public int Level = 128, TelemetryCalls, ReadLevelCalls, RpmCalls;
            public int FailTelemetry, FailWrite, FailRestore;
            public readonly List<int> Writes = new List<int>();
            public void SetLevel(int level)
            {
                Writes.Add(level);
                if (level == 128 && FailRestore-- > 0) throw new TimeoutException("restore busy");
                if (level != 128 && FailWrite-- > 0) throw new TimeoutException("write busy");
                Level = level;
            }
            public int ReadLevel() { ReadLevelCalls++; return Level; }
            public int? ReadRpm() { RpmCalls++; return 3000; }
            public FanTelemetry ReadTelemetry()
            {
                TelemetryCalls++;
                if (FailTelemetry-- > 0) throw new TimeoutException("telemetry busy");
                return new FanTelemetry { Level = Level, Rpm = 3000 };
            }
            public void Dispose() { }
        }
        private sealed class FakePort : IEcPort
        {
            public readonly Dictionary<int, byte> Registers = new Dictionary<int, byte> { { 0x2F, 128 }, { 0x84, 0xB8 }, { 0x85, 0x0B } };
            public bool ExposeObf = true, IgnoreWrites, Blocked;
            public int Commands, ModeWrites, Reads, DataReads;
            public int FailStatusReads;
            private int phase, address;
            private bool response;
            public byte Read(int port)
            {
                Reads++;
                if (port == 0x66)
                {
                    if (FailStatusReads-- > 0) throw new TimeoutException("injected status timeout");
                    return Blocked ? (byte)2 : response && ExposeObf ? (byte)1 : (byte)0;
                }
                DataReads++; response = false; return Registers[address];
            }
            public void Write(int port, byte value)
            {
                if (port == 0x66) { Commands++; phase = value == 0x80 ? 1 : 2; return; }
                if (phase == 1) { address = value; response = true; phase = 0; return; }
                if (phase == 2) { address = value; phase = 3; return; }
                if (phase == 3) { if (!IgnoreWrites) Registers[address] = value; ModeWrites++; phase = 0; }
            }
            public void Dispose() { }
        }
        private static void Tick(FanController control, int seconds, int level, DateTime start)
        {
            DateTime now = start.AddSeconds(seconds);
            control.Tick(FanMode.Manual, level, FanPolicy.Preset(FanMode.Balanced), new Snapshot { CpuTemperature = 60, Timestamp = now }, now);
        }
        public static int Run()
        {
            count = 0; DateTime start = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
            var fan = new FakeFan(); var c = new FanController(fan);
            for (int i = 0; i < 15; i++) Tick(c, i, 4, start);
            Assert(fan.Writes.Count == 1, "unchanged command does not rewrite each second");
            Assert(fan.TelemetryCalls == 3, "mode/RPM polled once per five seconds");
            Assert(fan.ReadLevelCalls == 0 && fan.RpmCalls == 0, "combined telemetry avoids separate mutex cycles");
            Assert(c.ActualLevel == 4 && c.Rpm == 3000 && c.Healthy, "cached acknowledged state stays available between polls");
            Tick(c, 15, 4, start); Assert(fan.Writes.Count == 2, "unchanged mode gets fifteen-second refresh");
            Tick(c, 16, 6, start); Assert(fan.Writes.Last() == 6 && c.ActualLevel == 6, "changed command dispatched before next telemetry");
            fan.Level = 128; Tick(c, 20, 6, start);
            Assert(fan.Level == 6 && fan.Writes.Last() == 6, "external mode change detected and reasserted");

            fan = new FakeFan(); c = new FanController(fan);
            var hotCurve = new[] { new CurvePoint(35, 0), new CurvePoint(55, 5), new CurvePoint(85, 7) };
            var revisedCurve = new[] { new CurvePoint(35, 0), new CurvePoint(55, 3), new CurvePoint(85, 7) };
            c.Tick(FanMode.Custom, 0, hotCurve, new Snapshot { CpuTemperature = 60, Timestamp = start }, start);
            c.ConfigurationChanged();
            c.Tick(FanMode.Custom, 0, revisedCurve, new Snapshot { CpuTemperature = 60, Timestamp = start }, start);
            Assert(fan.Level == 3, "explicitly applying a revised custom curve does not inherit old cooling dwell");

            fan = new FakeFan(); c = new FanController(fan); Tick(c, 0, 5, start); fan.FailTelemetry = 1;
            Tick(c, 5, 5, start);
            Assert(fan.Level == 5 && c.OwnsFan && !c.Healthy, "one read timeout retains confirmed setting without watchdog renewal");
            Tick(c, 6, 5, start);
            Assert(c.Healthy && fan.Level == 5 && fan.Writes.Count == 1, "transient telemetry error recovers without fan mode churn");
            fan.FailTelemetry = 3; Tick(c, 11, 5, start); Tick(c, 12, 5, start); Tick(c, 14, 5, start);
            Assert(fan.Level == 128 && !c.OwnsFan && c.Rpm == null, "three consecutive read failures restore auto and clear RPM");
            int writes = fan.Writes.Count; Tick(c, 15, 5, start);
            Assert(fan.Writes.Count == writes, "recovery backoff does not hammer EC");
            Tick(c, 16, 5, start); Assert(fan.Level == 5 && c.OwnsFan, "requested setting resumes after recovery delay");

            fan = new FakeFan { FailWrite = 1 }; c = new FanController(fan); Tick(c, 0, 4, start);
            Assert(fan.Writes.SequenceEqual(new[] { 4, 128 }) && !c.OwnsFan, "unconfirmed write immediately restores auto");
            Tick(c, 0, 4, start); Assert(fan.Writes.Count == 2, "same-time retry is deferred");
            Tick(c, 0, 7, start); Assert(fan.Level == 7, "changed user command bypasses old-command backoff");
            var stale = new Snapshot { CpuTemperature = 60, Timestamp = start };
            c.Tick(FanMode.Manual, 7, FanPolicy.Preset(FanMode.Balanced), stale, start.AddSeconds(6));
            Assert(fan.Level == 128 && !c.OwnsFan, "stale CPU data restores immediately despite slow telemetry");

            fan = new FakeFan(); c = new FanController(fan); Tick(c, 0, 4, start); fan.FailRestore = 2;
            c.Tick(FanMode.Bios, 0, FanPolicy.Preset(FanMode.Balanced), stale, start);
            Assert(c.OwnsFan && !c.Healthy, "failed automatic-mode write retains ownership and withholds lease");
            c.Tick(FanMode.Bios, 0, FanPolicy.Preset(FanMode.Balanced), stale, start.AddSeconds(1));
            Assert(c.OwnsFan && !c.Healthy, "failed automatic-mode retry is still unconfirmed");
            c.Tick(FanMode.Bios, 0, FanPolicy.Preset(FanMode.Balanced), stale, start.AddSeconds(3));
            Assert(!c.OwnsFan && fan.Level == 128 && c.Healthy, "automatic-mode restoration eventually confirms");

            var port = new FakePort();
            using (var device = new ThinkPadFan(port))
            {
                device.SetLevel(5);
                Assert(port.Registers[0x2F] == 5 && port.ModeWrites == 1 && port.DataReads == 1, "normal port command is read-back verified once");
                var data = device.ReadTelemetry();
                Assert(data.Level == 5 && data.Rpm == 3000, "tachometer low/high pair and mode decoded");
            }
            port = new FakePort { ExposeObf = false };
            using (var device = new ThinkPadFan(port))
            {
                device.SetLevel(6); Assert(port.Registers[0x2F] == 6, "missing OBF transition supports IBF-cleared compatibility read");
                var data = device.ReadTelemetry(); Assert(data.Level == 6 && data.Rpm == 3000, "compatibility telemetry remains actual register data");
            }
            port = new FakePort { ExposeObf = false, IgnoreWrites = true };
            using (var device = new ThinkPadFan(port))
            {
                bool rejected = false; try { device.SetLevel(5); } catch (IOException) { rejected = true; }
                Assert(rejected && port.Registers[0x2F] == 128, "compatibility fallback does not claim success on mismatched readback");
            }
            port = new FakePort { FailStatusReads = 1 };
            using (var device = new ThinkPadFan(port))
            { device.SetLevel(4); Assert(port.ModeWrites == 1 && port.Registers[0x2F] == 4, "bounded whole-transaction retry recovers transient status timeout"); }
            port = new FakePort { Blocked = true }; var timer = Stopwatch.StartNew();
            using (var device = new ThinkPadFan(port))
            {
                bool failed = false; try { device.SetLevel(4); } catch (TimeoutException) { failed = true; }
                Assert(failed && port.Commands == 0, "busy input buffer aborts without issuing command");
                Assert(timer.ElapsedMilliseconds < 2500, "busy retries have bounded elapsed time");
            }
            return count;
        }
    }
}
