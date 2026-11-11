using System;
using System.Collections.Generic;

namespace CarbonControl
{
    public static class CpuLimitsTests
    {
        private static int assertions;
        private static void Assert(bool value, string label)
        { assertions++; if (!value) throw new Exception("CPU limit reader: " + label); }
        private static bool Near(double? value, double expected)
        { return value.HasValue && Math.Abs(value.Value - expected) < 1e-9; }
        private sealed class FakeBackend : ICpuLimitBackend
        {
            public readonly Dictionary<uint, ulong> Values = new Dictionary<uint, ulong>();
            public uint? ThermalFeatures { get; set; }
            public int Reads, Disposals;
            public bool ThrowReads;
            public bool TryRead(uint index, out ulong value)
            { Reads++; if (ThrowReads) throw new InvalidOperationException(); return Values.TryGetValue(index, out value); }
            public void Dispose() { Disposals++; }
        }
        private sealed class FakePawnIo
        {
            public bool IsLoaded { get; set; }
            public int Result, Calls;
            public uint ReturnSize = 1;
            public long Value;
            public int ExecuteHr(string name, long[] input, uint inSize, long[] output, uint outSize, out uint returned)
            {
                Calls++;
                if (name != "ioctl_read_msr" || inSize != 1 || outSize != 1 || input.Length != 1)
                    throw new Exception("Unexpected native endpoint");
                output[0] = Value; returned = ReturnSize; return Result;
            }
        }
        private sealed class FakeIntelMsr
        {
            private readonly FakePawnIo _pawnIO;
            public int Closes, MisleadingReads;
            public FakeIntelMsr(FakePawnIo pawn) { _pawnIO = pawn; }
            public bool ReadMsr(uint index, out ulong value) { MisleadingReads++; value = 0; return true; }
            public void Close() { Closes++; _pawnIO.IsLoaded = false; }
        }
        public static int Run()
        {
            assertions = 0;
            ulong units = 3UL | (10UL << 16); // 1/8 W and 1/1024 s.
            ulong limits = 120UL | (1UL << 15) | (110UL << 17) | (240UL << 32) | (1UL << 47) | (1UL << 63);
            var reading = CpuLimitMonitor.Decode(units, limits, 0, 0x50);
            Assert(Near(reading.LongPowerW, 15), "PL1 uses power-unit exponent");
            Assert(Near(reading.ShortPowerW, 30), "PL2 comes from high dword");
            Assert(Near(reading.TimeWindowSeconds, 28), "Tau decodes Y and fractional Z");
            Assert(reading.LongEnabled == true && reading.ShortEnabled == true && reading.Locked == true, "enable and lock bits decoded");
            Assert(reading.ThermalThrottling == false && reading.PowerThrottling == false, "successful supported clear status means not active");
            Assert(reading.Source == "封装 MSR", "source is package MSR rather than effective platform limit");
            reading = CpuLimitMonitor.Decode(units, limits & ~(1UL << 15) & ~(1UL << 47), (1UL << 0) | (1UL << 10), 0x50);
            Assert(reading.LongEnabled == false && reading.ShortEnabled == false && reading.Locked == true, "disabled limits retain programmed values");
            Assert(Near(reading.LongPowerW, 15) && Near(reading.ShortPowerW, 30), "enable bits never enter watts");
            Assert(reading.ThermalThrottling == true && reading.PowerThrottling == true, "active thermal and package power status decoded");
            reading = CpuLimitMonitor.Decode(units, limits & ~(1UL << 63) | (1UL << 31), (1UL << 1) | (1UL << 11), 0x50);
            Assert(reading.Locked == false, "bit 31 is not package lock");
            Assert(reading.ThermalThrottling == false && reading.PowerThrottling == false, "historical sticky logs never become active status");
            reading = CpuLimitMonitor.Decode(units, limits, 0x401, 0x40);
            Assert(reading.ThermalThrottling == true && !reading.PowerThrottling.HasValue, "unsupported power notification remains unknown");
            reading = CpuLimitMonitor.Decode(units, limits, 0x401, 0x10);
            Assert(!reading.ThermalThrottling.HasValue && !reading.PowerThrottling.HasValue, "package thermal feature required");
            reading = CpuLimitMonitor.Decode(units, limits, 0x401, null);
            Assert(!reading.ThermalThrottling.HasValue && !reading.PowerThrottling.HasValue, "missing CPUID capabilities remain unknown");
            reading = CpuLimitMonitor.Decode(null, limits, null, 0x50);
            Assert(!reading.LongPowerW.HasValue && !reading.ShortPowerW.HasValue && !reading.TimeWindowSeconds.HasValue, "missing units never produce invented watts or Tau");
            Assert(reading.LongEnabled == true && reading.Locked == true, "control bits independent of unit read");
            reading = CpuLimitMonitor.Decode(units, null, 1, 0x50);
            Assert(!reading.LongEnabled.HasValue && !reading.ShortPowerW.HasValue && reading.ThermalThrottling == true, "partial register failure preserves available status");
            reading = CpuLimitMonitor.Decode(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 0x50);
            Assert(!reading.LongPowerW.HasValue && !reading.LongEnabled.HasValue && !reading.Locked.HasValue && !reading.ThermalThrottling.HasValue, "all-ones responses rejected");
            reading = CpuLimitMonitor.Decode(0, 0, 0, 0x50);
            Assert(Near(reading.LongPowerW, 0) && reading.LongEnabled == false && Near(reading.TimeWindowSeconds, 1), "valid zero values are distinct from unavailable");
            reading = CpuLimitMonitor.Decode(15UL | (15UL << 16), 1UL | (1UL << 32), null, null);
            Assert(Near(reading.LongPowerW, 1.0 / 32768) && Near(reading.TimeWindowSeconds, 1.0 / 32768), "full unit exponent width supported");

            DateTime now = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeBackend { ThermalFeatures = 0x50 };
            fake.Values[0x606] = units; fake.Values[0x610] = limits; fake.Values[0x1B1] = 0;
            int created = 0;
            var monitor = new CpuLimitMonitor(() => { created++; return fake; }, () => now);
            Assert(created == 0 && fake.Reads == 0, "constructing does not touch hardware");
            reading = monitor.Read();
            Assert(created == 1 && fake.Reads == 3 && Near(reading.LongPowerW, 15), "first read opens once and reads only three MSRs");
            reading.LongPowerW = 999;
            now = now.AddMilliseconds(1999);
            Assert(Near(monitor.Read().LongPowerW, 15) && fake.Reads == 3, "cached reading is isolated from caller mutation");
            fake.Values[0x610] = 160UL | (1UL << 15);
            now = now.AddMilliseconds(1);
            Assert(Near(monitor.Read().LongPowerW, 20) && fake.Reads == 6, "BIOS limit changes refresh at two seconds");
            fake.Values.Remove(0x610); now = now.AddSeconds(2);
            Assert(!monitor.Read().LongPowerW.HasValue, "failed refresh never keeps stale watts");
            fake.ThrowReads = true; now = now.AddSeconds(2);
            reading = monitor.Read();
            Assert(!reading.LongPowerW.HasValue && !reading.ThermalThrottling.HasValue && fake.Disposals == 1, "read exceptions clear data and close unusable backend");
            fake.ThrowReads = false; fake.Values[0x610] = limits; now = now.AddSeconds(2);
            Assert(Near(monitor.Read().LongPowerW, 15) && created == 2, "later successful read reconnects backend");
            int before = fake.Reads; now = now.AddSeconds(-1);
            monitor.Read(); Assert(fake.Reads == before + 3, "backward clock does not preserve a future cache");
            monitor.Dispose(); monitor.Dispose(); Assert(fake.Disposals == 2, "dispose is idempotent");
            bool rejected = false; try { monitor.Read(); } catch (ObjectDisposedException) { rejected = true; }
            Assert(rejected && fake.Disposals == 2, "disposed monitor never opens hardware again");
            var unavailable = new CpuLimitMonitor(() => { throw new InvalidOperationException(); }, () => now);
            reading = unavailable.Read();
            Assert(!reading.LongPowerW.HasValue && !reading.LongEnabled.HasValue && !reading.ThermalThrottling.HasValue, "unavailable driver leaves fields unknown");
            unavailable.Dispose();

            var pawn = new FakePawnIo { IsLoaded = false }; var wrapper = new FakeIntelMsr(pawn);
            var native = new LhmCpuLimitBackend(wrapper, 0x50);
            var nativeMonitor = new CpuLimitMonitor(() => native, () => now);
            reading = nativeMonitor.Read();
            Assert(!reading.LongPowerW.HasValue && !reading.LongEnabled.HasValue && !reading.ThermalThrottling.HasValue && !reading.PowerThrottling.HasValue,
                "unloaded native module never becomes false-zero limits or status");
            Assert(pawn.Calls == 0 && wrapper.MisleadingReads == 0 && wrapper.Closes == 1,
                "unloaded wrapper bypasses misleading ReadMsr and does not execute");
            nativeMonitor.Dispose();

            pawn = new FakePawnIo { IsLoaded = true }; wrapper = new FakeIntelMsr(pawn); native = new LhmCpuLimitBackend(wrapper, 0x50);
            ulong raw;
            Assert(native.TryRead(0x610, out raw) && raw == 0 && pawn.Calls == 1, "loaded successful eight-byte zero remains a valid register value");
            pawn.Result = unchecked((int)0x80070005);
            Assert(!native.TryRead(0x610, out raw), "driver access failure never becomes successful zero");
            pawn.Result = 0; pawn.ReturnSize = 0;
            Assert(!native.TryRead(0x610, out raw), "successful HRESULT with no returned data remains unavailable");
            pawn.ReturnSize = 2;
            Assert(!native.TryRead(0x610, out raw), "incorrect native response length rejected");
            pawn.ReturnSize = 1; pawn.Value = unchecked((long)(1UL << 63));
            Assert(native.TryRead(0x610, out raw) && raw == (1UL << 63), "native signed cell preserves the lock bit");
            pawn.IsLoaded = false;
            int calls = pawn.Calls;
            Assert(!native.TryRead(0x610, out raw) && pawn.Calls == calls, "module closing after initialization is checked before every read");
            Assert(wrapper.MisleadingReads == 0, "wrapper convenience ReadMsr is never used");
            native.Dispose(); native.Dispose();
            Assert(wrapper.Closes == 1, "native wrapper is closed exactly once");
            return assertions;
        }
    }
}
