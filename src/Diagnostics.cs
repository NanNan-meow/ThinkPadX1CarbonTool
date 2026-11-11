using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace CarbonControl
{
    public static class Diagnostics
    {
        public static int CheckComponents(string output)
        {
            var text = new StringBuilder();
            text.AppendLine("Hardware components · " + DateTime.Now.ToString("s"));
            text.AppendLine("Application: " + AppDomain.CurrentDomain.BaseDirectory);
            text.AppendLine("Library: " + HardwareComponents.LibraryPath);
            try
            {
                var assembly = HardwareComponents.LoadLibrary();
                var type = assembly.GetType("LibreHardwareMonitor.Hardware.Computer", true);
                Activator.CreateInstance(type); // Construct settings only; do not call Open or access hardware.
                foreach (var reference in assembly.GetReferencedAssemblies())
                    text.AppendLine("Dependency: " + Assembly.Load(reference).GetName().Name);
                if (!assembly.GetManifestResourceNames().Any(n => n.EndsWith("LpcACPIEC.bin", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("缺少风扇接口模块。");
                text.AppendLine("Version: " + assembly.GetName().Version);
                text.AppendLine("Status: 组件加载正常");
                File.WriteAllText(output, text.ToString(), Encoding.UTF8); return 0;
            }
            catch (Exception ex)
            {
                text.AppendLine("Status: 组件加载失败"); text.AppendLine(ex.ToString());
                File.WriteAllText(output, text.ToString(), Encoding.UTF8); return 1;
            }
        }
        public static int Write(string output)
        {
            Log.DirectoryPath = Path.GetDirectoryName(Path.GetFullPath(output));
            try
            {
                using (var monitor = new HardwareMonitor(Path.Combine(Log.DirectoryPath, "telemetry")))
                {
                    var s = monitor.Read();
                    for (int i = 0; i < 8; i++)
                    {
                        Thread.Sleep(1000); s = monitor.Read();
                        if (s.Batteries.Count > 0 && s.Drives.Count > 0 && s.CpuTemperature.HasValue) break;
                    }
                    var text = new StringBuilder();
                    text.AppendLine("Read-only hardware diagnostic · " + DateTime.Now.ToString("s"));
                    text.AppendLine("Model: " + Machine.Model); text.AppendLine("CPU: " + Machine.Cpu);
                    text.AppendLine("Target model match: " + Machine.Supported); text.AppendLine("Status: " + s.Status);
                    text.AppendLine("CPU load: " + s.CpuLoad); text.AppendLine("CPU clock (GHz): " + s.CpuClock);
                    text.AppendLine("CPU temperature: " + s.CpuTemperature); text.AppendLine("CPU package power: " + s.CpuPower);
                    text.AppendLine("GPU load: " + s.GpuLoad); text.AppendLine("GPU power: " + s.GpuPower);
                    foreach (var v in s.Sensors) text.AppendLine(v.Id + " | " + v.Name + " | " + v.Value);
                    foreach (var b in s.Batteries) text.AppendLine("Battery: " + b.Name + " | State=" + b.State + " | Health=" + b.HealthPercent + "% | Cycles=" + b.CycleCount + " | Charge=" + b.ChargePowerW + "W | Discharge=" + b.DischargePowerW + "W");
                    foreach (var d in s.Drives) text.AppendLine("Storage: " + d.Name + " | Temperature=" + d.Temperature + "C | Life remaining=" + d.HealthPercent + "% | Written=" + d.TotalWrittenGB + "GB");
                    text.AppendLine("Power settings: " + PerformanceController.Probe());
                    var l = s.CpuLimits; var p = s.CpuPolicy;
                    text.AppendLine("CPU limits: PL1=" + l.LongPowerW + "W; Enabled=" + l.LongEnabled + "; PL2=" + l.ShortPowerW + "W; Enabled=" + l.ShortEnabled + "; Tau=" + l.TimeWindowSeconds + "s; Locked=" + l.Locked + "; Source=" + l.Source);
                    text.AppendLine("CPU policy: AC=" + p.OnAcPower + "; Boost=" + p.BoostMode + "; Max MHz=" + p.MaximumFrequencyMhz);
                    text.AppendLine("CPU limiting: Thermal=" + l.ThermalThrottling + "; Power=" + l.PowerThrottling);
                    text.AppendLine("No fan device opened; no EC writes."); File.WriteAllText(output, text.ToString(), Encoding.UTF8);
                }
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(output, ex.ToString(), Encoding.UTF8); return 1; }
        }
    }
}
