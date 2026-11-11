using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace CarbonControl
{
    public sealed class BatteryReading
    {
        public string Name, Id, State;
        public double? ChargePercent, HealthPercent, DesignCapacityWh, FullChargeCapacityWh, RemainingCapacityWh;
        public double? ChargePowerW, DischargePowerW, Voltage, Temperature, RemainingMinutes;
        public int? CycleCount;
    }

    public sealed class StorageReading
    {
        public string Name, Id, Status;
        public double? Temperature, HealthPercent, UsedLifePercent, TotalWrittenGB, TotalReadGB, UsedSpacePercent;
        public bool? HasWarning;
    }

    public static class DeviceTelemetry
    {
        public static void Fill(Snapshot sample)
        {
            sample.Batteries = new List<BatteryReading>();
            sample.Drives = new List<StorageReading>();
            if (sample.Sensors == null) return;
            foreach (var group in sample.Sensors.Where(IsBattery).GroupBy(DeviceId))
                sample.Batteries.Add(Battery(group.Key, group.ToList()));
            foreach (var group in sample.Sensors.Where(IsStorage).GroupBy(DeviceId))
                sample.Drives.Add(Storage(group.Key, group.ToList()));
        }

        private static bool IsBattery(SensorReading s) { return string.Equals(s.Hardware, "Battery", StringComparison.OrdinalIgnoreCase); }
        private static bool IsStorage(SensorReading s)
        {
            return string.Equals(s.Hardware, "Storage", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Hardware, "HDD", StringComparison.OrdinalIgnoreCase);
        }
        private static string DeviceId(SensorReading s)
        {
            if (!string.IsNullOrWhiteSpace(s.HardwareId)) return s.HardwareId;
            string id = s.Id ?? "";
            int sensor = id.LastIndexOf('/');
            if (sensor > 0) { id = id.Substring(0, sensor); sensor = id.LastIndexOf('/'); if (sensor > 0) id = id.Substring(0, sensor); }
            return id.Length == 0 ? (s.HardwareName ?? s.Hardware ?? "") : id;
        }
        private static string Name(List<SensorReading> sensors, string fallback)
        { return sensors.Select(x => x.HardwareName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? fallback; }
        private static double? Value(List<SensorReading> sensors, string kind, params string[] names)
        {
            foreach (string name in names)
            {
                var found = sensors.FirstOrDefault(x => string.Equals(x.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && Finite(x.Value));
                if (found != null) return found.Value;
            }
            return null;
        }
        internal static bool Finite(double? value) { return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value); }
        private static double? Range(double? v, double min, double max) { return Finite(v) && v >= min && v <= max ? v : null; }
        private static double? Percent(double? v) { return Range(v, 0, 100); }
        private static double? Capacity(double? mWh) { return Range(mWh, 1, 1000000) / 1000; }

        private static BatteryReading Battery(string id, List<SensorReading> sensors)
        {
            var b = new BatteryReading { Id = id, Name = Name(sensors, "电池"), State = "状态未知" };
            // LibreHardwareMonitor Battery Energy values are mWh, TimeSpan values are seconds.
            b.DesignCapacityWh = Capacity(Value(sensors, "Energy", "Designed Capacity", "Design Capacity"));
            b.FullChargeCapacityWh = Capacity(Value(sensors, "Energy", "Fully-Charged Capacity", "Full Charge Capacity"));
            b.RemainingCapacityWh = Range(Value(sensors, "Energy", "Remaining Capacity"), 0, 1000000) / 1000;
            b.ChargePercent = Percent(Value(sensors, "Level", "Charge Level"));
            if (!b.ChargePercent.HasValue && b.RemainingCapacityWh.HasValue && b.FullChargeCapacityWh > 0)
                b.ChargePercent = Percent(100 * b.RemainingCapacityWh / b.FullChargeCapacityWh);
            SetBatteryHealth(b);
            if (!b.HealthPercent.HasValue)
            {
                double? degradation = Percent(Value(sensors, "Level", "Degradation Level"));
                if (degradation.HasValue) b.HealthPercent = 100 - degradation.Value;
            }
            b.ChargePowerW = Range(Value(sensors, "Power", "Charge Rate", "Charge Power"), 0, 500);
            b.DischargePowerW = Range(Value(sensors, "Power", "Discharge Rate", "Discharge Power"), 0, 500);
            double? idle = Range(Value(sensors, "Power", "Charge/Discharge Rate"), 0, 500);
            if (idle == 0) { b.ChargePowerW = 0; b.DischargePowerW = 0; }
            b.Voltage = Range(Value(sensors, "Voltage", "Voltage"), 1, 100);
            b.Temperature = Range(Value(sensors, "Temperature", "Battery Temperature", "Temperature"), -40, 120);
            b.RemainingMinutes = Range(Value(sensors, "TimeSpan", "Remaining Time (Estimated)", "Remaining Time"), 1, 604800) / 60;
            double? cycles = Range(Value(sensors, "Factor", "Cycle Count"), 0, 1000000);
            if (cycles.HasValue && cycles.Value == Math.Truncate(cycles.Value)) b.CycleCount = (int)cycles.Value;
            if (b.ChargePowerW > 0) b.State = "充电";
            else if (b.DischargePowerW > 0) b.State = "放电";
            else if (idle == 0 || b.ChargePowerW == 0 || b.DischargePowerW == 0)
                b.State = b.ChargePercent >= 99.5 ? "已充满" : "待机";
            return b;
        }
        internal static void SetBatteryHealth(BatteryReading b)
        {
            if (b.DesignCapacityWh > 0 && b.FullChargeCapacityWh > 0)
                b.HealthPercent = Math.Max(0, Math.Min(100, b.FullChargeCapacityWh.Value / b.DesignCapacityWh.Value * 100));
        }
        private static StorageReading Storage(string id, List<SensorReading> sensors)
        {
            var d = new StorageReading { Id = id, Name = Name(sensors, "SSD"), Status = "健康信息未提供" };
            // Warning/Critical Temperature are thresholds, not measured device temperatures.
            d.Temperature = Range(Value(sensors, "Temperature", "Composite Temperature", "Temperature", "Temperature #1"), -40, 150);
            d.HealthPercent = Percent(Value(sensors, "Level", "Life", "Remaining Life"));
            d.UsedLifePercent = Range(Value(sensors, "Level", "Percentage Used", "Used Life"), 0, 255);
            if (!d.HealthPercent.HasValue && d.UsedLifePercent.HasValue) d.HealthPercent = Math.Max(0, 100 - d.UsedLifePercent.Value);
            if (!d.UsedLifePercent.HasValue && d.HealthPercent.HasValue) d.UsedLifePercent = 100 - d.HealthPercent.Value;
            d.TotalReadGB = Range(Value(sensors, "Data", "Data Read"), 0, 1000000000);
            d.TotalWrittenGB = Range(Value(sensors, "Data", "Data Written"), 0, 1000000000);
            d.UsedSpacePercent = Percent(Value(sensors, "Load", "Used Space"));
            double? warning = Range(Value(sensors, "Factor", "Critical Warning", "SMART Warning"), 0, 255);
            if (warning.HasValue) d.HasWarning = warning.Value != 0;
            double? warningTemperature = Range(Value(sensors, "Temperature", "Warning Temperature"), 1, 150);
            if (d.Temperature.HasValue && warningTemperature.HasValue && d.Temperature >= warningTemperature) d.HasWarning = true;
            if (d.HasWarning == true || d.HealthPercent == 0) d.Status = "需要关注";
            else if (d.HealthPercent.HasValue) d.Status = "剩余寿命 " + d.HealthPercent.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
            return d;
        }
    }

    // Keeps only capacity/cycle fields. Windows' full report, which includes usage history, is deleted after parsing.
    internal sealed class BatteryReportCache
    {
        private readonly string directory;
        private readonly object gate = new object();
        private List<BatteryReading> batteries = new List<BatteryReading>();
        private DateTime nextRefresh;
        private bool loaded;
        internal BatteryReportCache(string directory) { this.directory = directory; }
        internal void RefreshIfDue()
        {
            if (!loaded) { loaded = true; LoadCache(); }
            if (DateTime.UtcNow < nextRefresh) return;
            nextRefresh = DateTime.UtcNow.AddMinutes(10);
            string report = null;
            try
            {
                Directory.CreateDirectory(directory);
                report = Path.Combine(directory, "battery-report-" + Guid.NewGuid().ToString("N") + ".xml");
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe"),
                    "/batteryreport /xml /output \"" + report + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(start))
                {
                    if (!p.WaitForExit(15000)) { try { p.Kill(); p.WaitForExit(2000); } catch { } return; }
                    if (p.ExitCode != 0 || !File.Exists(report)) return;
                }
                var parsed = Parse(File.ReadAllText(report));
                if (parsed.Count == 0) return;
                lock (gate) batteries = parsed;
                nextRefresh = DateTime.UtcNow.AddHours(6);
                SaveCache(parsed);
            }
            catch (Exception ex) { Log.Write(ex); }
            finally { if (report != null) try { File.Delete(report); } catch { } }
        }
        internal void Apply(Snapshot sample)
        {
            List<BatteryReading> cached;
            lock (gate) cached = batteries;
            if (cached.Count == 0) return;
            if (sample.Batteries.Count == 0)
            {
                for (int i = 0; i < cached.Count; i++)
                {
                    var c = cached[i];
                    sample.Batteries.Add(new BatteryReading { Id = "battery-report/" + i, Name = c.Name, State = "等待电池传感器" });
                }
            }
            foreach (var b in sample.Batteries)
            {
                var c = cached.FirstOrDefault(x => string.Equals(x.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                if (c == null && cached.Count == 1 && sample.Batteries.Count == 1) c = cached[0];
                if (c == null) continue;
                if (!b.DesignCapacityWh.HasValue) b.DesignCapacityWh = c.DesignCapacityWh;
                if (!b.FullChargeCapacityWh.HasValue) b.FullChargeCapacityWh = c.FullChargeCapacityWh;
                if (!b.CycleCount.HasValue) b.CycleCount = c.CycleCount;
                DeviceTelemetry.SetBatteryHealth(b);
            }
        }
        internal static List<BatteryReading> Parse(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
            var found = new List<BatteryReading>();
            foreach (XmlNode n in doc.SelectNodes("/*[local-name()='BatteryReport']/*[local-name()='Batteries']/*[local-name()='Battery']"))
            {
                var b = new BatteryReading { Name = Child(n, "Name") };
                if (string.IsNullOrWhiteSpace(b.Name)) b.Name = Child(n, "Id");
                if (string.IsNullOrWhiteSpace(b.Name)) b.Name = "电池";
                if (Child(n, "RelativeCapacity") != "1")
                {
                    b.DesignCapacityWh = ReportCapacity(Child(n, "DesignCapacity"));
                    b.FullChargeCapacityWh = ReportCapacity(Child(n, "FullChargeCapacity"));
                }
                int cycles;
                if (int.TryParse(Child(n, "CycleCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out cycles) && cycles >= 0 && cycles < 1000000) b.CycleCount = cycles;
                DeviceTelemetry.SetBatteryHealth(b); found.Add(b);
            }
            return found;
        }
        private static string Child(XmlNode node, string name)
        { var child = node.SelectSingleNode("*[local-name()='" + name + "']"); return child == null ? "" : child.InnerText.Trim(); }
        private static double? ReportCapacity(string text)
        {
            double mWh;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out mWh) &&
                DeviceTelemetry.Finite(mWh) && mWh > 0 && mWh <= 1000000 ? (double?)(mWh / 1000) : null;
        }
        private void SaveCache(List<BatteryReading> values)
        {
            using (var writer = XmlWriter.Create(Path.Combine(directory, "battery-health.xml"), new XmlWriterSettings { Indent = true }))
            {
                writer.WriteStartElement("BatteryCache"); writer.WriteAttributeString("UpdatedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                foreach (var b in values)
                {
                    writer.WriteStartElement("Battery"); writer.WriteElementString("Name", b.Name);
                    if (b.DesignCapacityWh.HasValue) writer.WriteElementString("DesignWh", b.DesignCapacityWh.Value.ToString("R", CultureInfo.InvariantCulture));
                    if (b.FullChargeCapacityWh.HasValue) writer.WriteElementString("FullWh", b.FullChargeCapacityWh.Value.ToString("R", CultureInfo.InvariantCulture));
                    if (b.CycleCount.HasValue) writer.WriteElementString("CycleCount", b.CycleCount.Value.ToString(CultureInfo.InvariantCulture));
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
        }
        private void LoadCache()
        {
            try
            {
                string path = Path.Combine(directory, "battery-health.xml");
                if (!File.Exists(path)) return;
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
                DateTime updated;
                if (!DateTime.TryParse(doc.DocumentElement.GetAttribute("UpdatedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out updated)) return;
                var values = new List<BatteryReading>();
                foreach (XmlNode node in doc.DocumentElement.SelectNodes("Battery"))
                {
                    var b = new BatteryReading { Name = Child(node, "Name") };
                    double value; int cycles;
                    if (double.TryParse(Child(node, "DesignWh"), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0 && value <= 1000) b.DesignCapacityWh = value;
                    if (double.TryParse(Child(node, "FullWh"), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0 && value <= 1000) b.FullChargeCapacityWh = value;
                    if (int.TryParse(Child(node, "CycleCount"), out cycles) && cycles >= 0 && cycles < 1000000) b.CycleCount = cycles;
                    DeviceTelemetry.SetBatteryHealth(b); values.Add(b);
                }
                lock (gate) batteries = values;
                if (updated.ToUniversalTime() <= DateTime.UtcNow) nextRefresh = updated.ToUniversalTime().AddHours(6);
            }
            catch (Exception ex) { Log.Write(ex); }
        }
    }
}
