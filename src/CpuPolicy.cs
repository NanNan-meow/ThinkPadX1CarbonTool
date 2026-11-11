using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CarbonControl
{
    public sealed class CpuPolicyReading
    {
        public uint? BoostMode, MaximumFrequencyMhz;
        public bool? OnAcPower;
        public string SchemeName;
        public DateTime Timestamp;
        internal CpuPolicyReading Copy() { return (CpuPolicyReading)MemberwiseClone(); }
    }

    public sealed class CpuPolicyMonitor
    {
        private readonly IPowerPlanBackend backend;
        private readonly Func<byte?> powerSource;
        private readonly Func<DateTime> utcNow;
        private readonly object gate = new object();
        private CpuPolicyReading cached;

        public CpuPolicyMonitor() : this(new WindowsPowerPlanBackend(), ReadPowerSource, () => DateTime.UtcNow) { }
        public CpuPolicyMonitor(IPowerPlanBackend policyBackend, Func<byte?> readPowerSource, Func<DateTime> clock)
        {
            if (policyBackend == null) throw new ArgumentNullException("policyBackend");
            if (readPowerSource == null) throw new ArgumentNullException("readPowerSource");
            if (clock == null) throw new ArgumentNullException("clock");
            backend = policyBackend; powerSource = readPowerSource; utcNow = clock;
        }
        public CpuPolicyReading Read()
        {
            lock (gate)
            {
                DateTime now = utcNow();
                if (cached != null && now >= cached.Timestamp && now - cached.Timestamp < TimeSpan.FromSeconds(2)) return cached.Copy();
                var reading = new CpuPolicyReading { Timestamp = now };
                try
                {
                    // Verify that the power source and active plan remained the same while
                    // their values were read, so a transition cannot mix AC and DC settings.
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        SetPowerSource(reading, powerSource());
                        if (!reading.OnAcPower.HasValue) break;
                        Guid active = backend.GetActiveScheme();
                        PowerPlanValues values = backend.Read(active);
                        Guid after = backend.GetActiveScheme();
                        bool? sourceAfter = ParsePowerSource(powerSource());
                        if (active != after || reading.OnAcPower != sourceAfter)
                        {
                            reading.OnAcPower = sourceAfter; reading.SchemeName = SourceName(sourceAfter);
                            continue;
                        }
                        reading.BoostMode = reading.OnAcPower.Value ? values.AcBoost : values.DcBoost;
                        reading.MaximumFrequencyMhz = reading.OnAcPower.Value ? values.AcFrequency : values.DcFrequency;
                        break;
                    }
                }
                catch (Exception ex) { Log.Write(ex); }
                cached = reading;
                return reading.Copy();
            }
        }
        public void Invalidate()
        { lock (gate) cached = null; }
        private static void SetPowerSource(CpuPolicyReading reading, byte? line)
        { reading.OnAcPower = ParsePowerSource(line); reading.SchemeName = SourceName(reading.OnAcPower); }
        private static bool? ParsePowerSource(byte? line)
        { return line == 1 ? true : line == 0 ? false : (bool?)null; }
        private static string SourceName(bool? onAc)
        { return !onAc.HasValue ? "" : onAc.Value ? "接电" : "电池"; }

        // PERFBOOSTMODE values are defined by Microsoft's WinNT.h.
        // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/winnt.h
        public static string BoostLabel(uint? mode)
        {
            switch (mode)
            {
                case 0: return "禁用";
                case 1: return "启用";
                case 2: return "积极";
                case 3: return "高效启用";
                case 4: return "高效积极";
                case 5: return "积极(保障)";
                case 6: return "高效积极(保障)";
                default: return "—";
            }
        }
        public static string FrequencyLabel(uint? megahertz)
        {
            if (!megahertz.HasValue) return "—";
            return megahertz.Value == 0 ? "系统调节" : (megahertz.Value / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " GHz";
        }

        // https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-system_power_status
        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public uint BatteryLifeTime, BatteryFullLifeTime;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
        private static byte? ReadPowerSource()
        {
            SystemPowerStatus status;
            return GetSystemPowerStatus(out status) ? (byte?)status.AcLineStatus : null;
        }
    }
}
