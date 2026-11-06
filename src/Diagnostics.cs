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
                using (var monitor = new HardwareMonitor())
                {
                    monitor.Read(); Thread.Sleep(1100); var s = monitor.Read();
                    var text = new StringBuilder();
                    text.AppendLine("Read-only hardware diagnostic · " + DateTime.Now.ToString("s"));
                    text.AppendLine("Model: " + Machine.Model); text.AppendLine("CPU: " + Machine.Cpu);
                    text.AppendLine("Target model match: " + Machine.Supported); text.AppendLine("Status: " + s.Status);
                    text.AppendLine("CPU load: " + s.CpuLoad); text.AppendLine("CPU clock (GHz): " + s.CpuClock);
                    text.AppendLine("CPU temperature: " + s.CpuTemperature); text.AppendLine("CPU package power: " + s.CpuPower);
                    text.AppendLine("GPU load: " + s.GpuLoad); text.AppendLine("GPU power: " + s.GpuPower);
                    foreach (var v in s.Sensors) text.AppendLine(v.Id + " | " + v.Name + " | " + v.Value);
                    text.AppendLine("No fan device opened; no EC writes."); File.WriteAllText(output, text.ToString(), Encoding.UTF8);
                }
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(output, ex.ToString(), Encoding.UTF8); return 1; }
        }
    }
}
