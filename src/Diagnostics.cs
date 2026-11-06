using System;
using System.IO;
using System.Text;
using System.Threading;

namespace CarbonControl
{
    public static class Diagnostics
    {
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
