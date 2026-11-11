using System;
using System.Reflection;

namespace CarbonControl
{
    public sealed class CpuLimitReading
    {
        public double? LongPowerW, ShortPowerW, TimeWindowSeconds;
        public bool? LongEnabled, ShortEnabled, Locked, ThermalThrottling, PowerThrottling;
        public string Source = "封装 MSR";
        internal CpuLimitReading Copy() { return (CpuLimitReading)MemberwiseClone(); }
    }

    internal interface ICpuLimitBackend : IDisposable
    {
        uint? ThermalFeatures { get; }
        bool TryRead(uint index, out ulong value);
    }

    // Intel SDM, RAPL package domain and IA32_PACKAGE_THERM_STATUS.
    // https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html
    // This reader invokes only ioctl_read_msr; it never invokes a write endpoint.
    public sealed class CpuLimitMonitor : IDisposable
    {
        private const uint RaplUnit = 0x606, PackageLimit = 0x610, PackageThermal = 0x1B1;
        private readonly object gate = new object();
        private readonly Func<ICpuLimitBackend> createBackend;
        private readonly Func<DateTime> clock;
        private ICpuLimitBackend backend;
        private CpuLimitReading cached;
        private DateTime sampled;
        private bool disposed;
        public CpuLimitMonitor() : this(() => new LhmCpuLimitBackend(), () => DateTime.UtcNow) { }
        internal CpuLimitMonitor(Func<ICpuLimitBackend> createBackend, Func<DateTime> clock)
        {
            if (createBackend == null || clock == null) throw new ArgumentNullException();
            this.createBackend = createBackend; this.clock = clock;
        }
        public CpuLimitReading Read()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("CpuLimitMonitor");
                DateTime now = clock();
                if (cached != null && now >= sampled && now - sampled < TimeSpan.FromSeconds(2)) return cached.Copy();
                var result = new CpuLimitReading();
                try
                {
                    if (backend == null) backend = createBackend();
                    ulong units, limits, thermal;
                    bool hasUnits = TryRead(RaplUnit, out units);
                    bool hasLimits = TryRead(PackageLimit, out limits);
                    bool hasThermal = TryRead(PackageThermal, out thermal);
                    result = Decode(hasUnits ? (ulong?)units : null, hasLimits ? (ulong?)limits : null,
                        hasThermal ? (ulong?)thermal : null, backend.ThermalFeatures);
                    if (!hasUnits && !hasLimits && !hasThermal) CloseBackend();
                }
                catch { CloseBackend(); }
                cached = result; sampled = now;
                return result.Copy();
            }
        }
        private bool TryRead(uint index, out ulong value)
        {
            value = 0;
            try { return backend.TryRead(index, out value) && value != ulong.MaxValue; }
            catch { return false; }
        }
        internal static CpuLimitReading Decode(ulong? units, ulong? limits, ulong? thermal, uint? thermalFeatures)
        {
            var reading = new CpuLimitReading();
            if (units == ulong.MaxValue) units = null;
            if (limits == ulong.MaxValue) limits = null;
            if (thermal == ulong.MaxValue) thermal = null;
            if (limits.HasValue)
            {
                ulong raw = limits.Value;
                reading.LongEnabled = (raw & (1UL << 15)) != 0;
                reading.ShortEnabled = (raw & (1UL << 47)) != 0;
                reading.Locked = (raw & (1UL << 63)) != 0;
                if (units.HasValue)
                {
                    double powerUnit = Math.Pow(2, -(int)(units.Value & 15));
                    double timeUnit = Math.Pow(2, -(int)((units.Value >> 16) & 15));
                    reading.LongPowerW = (raw & 0x7FFFUL) * powerUnit;
                    reading.ShortPowerW = ((raw >> 32) & 0x7FFFUL) * powerUnit;
                    uint window = (uint)((raw >> 17) & 0x7F);
                    reading.TimeWindowSeconds = Math.Pow(2, window & 31) * (1 + ((window >> 5) & 3) / 4.0) * timeUnit;
                }
            }
            // EAX[6] advertises package thermal management; EAX[4] advertises
            // power-limit notification. Read current bits 0/10, never log bits 1/11.
            if (thermal.HasValue && thermalFeatures.HasValue && (thermalFeatures.Value & (1u << 6)) != 0)
            {
                reading.ThermalThrottling = (thermal.Value & 1UL) != 0;
                if ((thermalFeatures.Value & (1u << 4)) != 0)
                    reading.PowerThrottling = (thermal.Value & (1UL << 10)) != 0;
            }
            return reading;
        }
        private void CloseBackend()
        {
            if (backend == null) return;
            try { backend.Dispose(); } catch { }
            backend = null;
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; CloseBackend(); cached = null; }
        }
    }

    // IntelMsr in LHM v0.9.6 loads its embedded, signed IntelMSR module.
    // https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/PawnIo/IntelMsr.cs
    // Its whitelist includes 0x606, 0x610 and 0x1B1:
    // https://github.com/namazso/PawnIO.Modules/blob/0.1.6/IntelMSR.p
    internal sealed class LhmCpuLimitBackend : ICpuLimitBackend
    {
        private object module;
        private MethodInfo close;
        private CpuMsrReadSession reader;
        public uint? ThermalFeatures { get; private set; }
        internal LhmCpuLimitBackend()
        {
            if (!Machine.Supported) throw new PlatformNotSupportedException();
            var assembly = HardwareComponents.LoadLibrary();
            Type type = assembly.GetType("LibreHardwareMonitor.PawnIo.IntelMsr", true);
            Attach(Activator.CreateInstance(type));
            try
            {
                // HardwareMonitor has opened LHM's OpCode before the first CPU sample.
                var cpuIdType = assembly.GetType("LibreHardwareMonitor.Hardware.Cpu.CpuId", true);
                object cpuId = cpuIdType.GetMethod("Get", new[] { typeof(int), typeof(int) }).Invoke(null, new object[] { 0, 0 });
                if (cpuId != null)
                {
                    var data = (uint[,])cpuIdType.GetProperty("Data").GetValue(cpuId, null);
                    if (data.GetLength(0) > 6 && data.GetLength(1) > 0) ThermalFeatures = data[6, 0];
                }
            }
            catch { }
        }
        internal LhmCpuLimitBackend(object wrapper, uint? thermalFeatures)
        { Attach(wrapper); ThermalFeatures = thermalFeatures; }
        private void Attach(object wrapper)
        {
            if (wrapper == null) throw new ArgumentNullException("wrapper");
            module = wrapper;
            try
            {
                Type type = wrapper.GetType();
                close = type.GetMethod("Close", Type.EmptyTypes);
                if (close == null) throw new MissingMethodException("IntelMsr.Close");
                var field = type.GetField("_pawnIO", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null) throw new MissingFieldException("IntelMsr._pawnIO");
                reader = new CpuMsrReadSession(field.GetValue(wrapper));
            }
            catch { Dispose(); throw; }
        }
        public bool TryRead(uint index, out ulong value)
        {
            value = 0;
            return module != null && reader.TryRead(index, out value);
        }
        public void Dispose()
        {
            object current = module; module = null;
            reader = null;
            if (current != null && close != null) close.Invoke(current, null);
        }
    }

    // LHM's IntelMsr.ReadMsr / PawnIo.Execute return a zero-filled array even
    // if the module is unavailable or the driver call fails. Check both the
    // loaded state and ExecuteHr's actual returned length before accepting data.
    // https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/PawnIo/PawnIo.cs
    internal sealed class CpuMsrReadSession
    {
        private readonly object pawn;
        private readonly PropertyInfo loaded;
        private readonly MethodInfo execute;
        internal CpuMsrReadSession(object pawn)
        {
            if (pawn == null) throw new InvalidOperationException("Intel MSR module unavailable");
            this.pawn = pawn;
            Type type = pawn.GetType();
            loaded = type.GetProperty("IsLoaded", BindingFlags.Instance | BindingFlags.Public);
            execute = type.GetMethod("ExecuteHr", new[] { typeof(string), typeof(long[]), typeof(uint), typeof(long[]), typeof(uint), typeof(uint).MakeByRefType() });
            if (loaded == null || execute == null) throw new MissingMethodException("PawnIo.ExecuteHr / IsLoaded");
        }
        internal bool TryRead(uint index, out ulong value)
        {
            value = 0;
            if (!(bool)loaded.GetValue(pawn, null)) return false;
            var output = new long[1];
            object[] args = { "ioctl_read_msr", new long[] { index }, 1u, output, 1u, 0u };
            int result = (int)execute.Invoke(pawn, args);
            if (result != 0 || (uint)args[5] != 1) return false;
            value = unchecked((ulong)output[0]);
            return true;
        }
    }
}
