using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace CarbonControl
{
    public static class DeviceTelemetryTests
    {
        private static int count;
        private static void Assert(bool condition, string label)
        { count++; if (!condition) throw new Exception("FAILED device telemetry: " + label); }
        private static bool Near(double? value, double expected)
        { return value.HasValue && Math.Abs(value.Value - expected) < 0.00001; }
        private static SensorReading Sensor(string hardware, string id, string kind, string name, double? value)
        {
            return new SensorReading { Hardware = hardware, HardwareId = id, HardwareName = id == "/battery/0" ? "Battery A" : "Drive " + id,
                Kind = kind, Name = name, Id = id + "/" + kind.ToLowerInvariant() + "/" + name, Value = value };
        }
        public static int Run(string directory)
        {
            count = 0;
            var s = new Snapshot();
            s.Sensors.Add(Sensor("Battery", "/battery/0", "Energy", "Designed Capacity", 51010));
            s.Sensors.Add(Sensor("Battery", "/battery/0", "Energy", "Fully-Charged Capacity", 37800));
            s.Sensors.Add(Sensor("Battery", "/battery/0", "Energy", "Remaining Capacity", 18900));
            s.Sensors.Add(Sensor("Battery", "/battery/0", "Level", "Charge Level", 50));
            s.Sensors.Add(Sensor("Battery", "/battery/0", "Power", "Discharge Rate", 6.25));
            s.Sensors.Add(Sensor("Battery", "/battery/0", "TimeSpan", "Remaining Time (Estimated)", 10800));
            s.Sensors.Add(Sensor("Battery", "/battery/0", "Voltage", "Voltage", 12.1));
            s.Sensors.Add(Sensor("Battery", "/battery/0", "Temperature", "Battery Temperature", 31));
            s.Sensors.Add(Sensor("Storage", "/nvme/0", "Temperature", "Composite Temperature", 43));
            s.Sensors.Add(Sensor("Storage", "/nvme/0", "Temperature", "Warning Temperature", 80));
            s.Sensors.Add(Sensor("Storage", "/nvme/0", "Temperature", "Critical Temperature", 85));
            s.Sensors.Add(Sensor("Storage", "/nvme/0", "Level", "Life", 91));
            s.Sensors.Add(Sensor("Storage", "/nvme/0", "Load", "Used Space", 73));
            s.Sensors.Add(Sensor("Storage", "/nvme/0", "Data", "Data Written", 12345.5));
            s.Sensors.Add(Sensor("Storage", "/nvme/0", "Data", "Data Read", 14000));
            s.Sensors.Add(Sensor("Storage", "/nvme/1", "Load", "Used Space", 12));
            s.Sensors.Add(Sensor("GpuIntel", "/intel/0", "Load", "GPU Core", 70));
            DeviceTelemetry.Fill(s);
            Assert(s.Batteries.Count == 1 && s.Drives.Count == 2, "groups physical devices and ignores GPU");
            var b = s.Batteries[0]; var d = s.Drives[0];
            Assert(b.Name == "Battery A" && b.Id == "/battery/0", "retains identity");
            Assert(Near(b.DesignCapacityWh, 51.01) && Near(b.FullChargeCapacityWh, 37.8), "battery mWh converted to Wh");
            Assert(Near(b.HealthPercent, 37.8 / 51.01 * 100), "health ratio");
            Assert(Near(b.ChargePercent, 50) && Near(b.RemainingCapacityWh, 18.9), "state of charge");
            Assert(Near(b.DischargePowerW, 6.25) && !b.ChargePowerW.HasValue && b.State == "放电", "discharge direction");
            Assert(Near(b.RemainingMinutes, 180), "seconds converted to minutes");
            Assert(Near(b.Voltage, 12.1) && Near(b.Temperature, 31), "voltage and battery temperature");
            Assert(!b.CycleCount.HasValue, "missing cycles remains unknown");
            Assert(Near(d.Temperature, 43), "temperature thresholds are not current readings");
            Assert(Near(d.HealthPercent, 91) && Near(d.UsedLifePercent, 9), "Life means remaining life");
            Assert(Near(d.UsedSpacePercent, 73) && Near(d.TotalWrittenGB, 12345.5) && Near(d.TotalReadGB, 14000), "space and host data counters");
            Assert(!s.Drives[1].HealthPercent.HasValue, "used disk space never becomes health");
            Assert(!d.HasWarning.HasValue, "missing SMART warning is unknown");
            DeviceTelemetry.Fill(s);
            Assert(s.Batteries.Count == 1 && s.Drives.Count == 2, "repeated mapping replaces lists");

            s = new Snapshot { Sensors = new List<SensorReading> {
                Sensor("Battery", "/battery/0", "Energy", "Designed Capacity", 4294967295),
                Sensor("Battery", "/battery/0", "Energy", "Fully-Charged Capacity", double.NaN),
                Sensor("Battery", "/battery/0", "Level", "Charge Level", -1),
                Sensor("Battery", "/battery/0", "Power", "Charge Rate", null),
                Sensor("Battery", "/battery/0", "TimeSpan", "Remaining Time (Estimated)", 4294967295),
                Sensor("Storage", "/nvme/0", "Temperature", "Critical Temperature", 85),
                Sensor("Storage", "/nvme/0", "Level", "Life", double.PositiveInfinity) } };
            DeviceTelemetry.Fill(s); b = s.Batteries[0]; d = s.Drives[0];
            Assert(!b.DesignCapacityWh.HasValue && !b.FullChargeCapacityWh.HasValue && !b.HealthPercent.HasValue, "capacity sentinels rejected");
            Assert(!b.ChargePercent.HasValue && !b.ChargePowerW.HasValue && !b.RemainingMinutes.HasValue, "unavailable values not fabricated as zero");
            Assert(!d.Temperature.HasValue && !d.HealthPercent.HasValue, "threshold-only and infinity remain unavailable");

            s = new Snapshot { Sensors = new List<SensorReading> {
                Sensor("Battery", "/battery/0", "Energy", "Fully-Charged Capacity", 40000),
                Sensor("Battery", "/battery/0", "Energy", "Remaining Capacity", 0),
                Sensor("Battery", "/battery/0", "Power", "Charge/Discharge Rate", 0),
                Sensor("Storage", "/nvme/0", "Level", "Percentage Used", 101),
                Sensor("Storage", "/nvme/0", "Temperature", "Composite Temperature", 81),
                Sensor("Storage", "/nvme/0", "Temperature", "Warning Temperature", 80) } };
            DeviceTelemetry.Fill(s);
            Assert(Near(s.Batteries[0].ChargePercent, 0) && Near(s.Batteries[0].RemainingCapacityWh, 0), "zero charge valid");
            Assert(Near(s.Batteries[0].ChargePowerW, 0) && Near(s.Batteries[0].DischargePowerW, 0), "measured idle is valid zero");
            Assert(Near(s.Drives[0].HealthPercent, 0) && Near(s.Drives[0].UsedLifePercent, 101), "NVMe wear above 100 retains used life");
            Assert(s.Drives[0].HasWarning == true && s.Drives[0].Status == "需要关注", "device warning temperature comparison");
            s = new Snapshot { Sensors = new List<SensorReading> { Sensor("Battery", "/battery/0", "Power", "Charge Rate", 12.2) } };
            DeviceTelemetry.Fill(s);
            Assert(s.Batteries[0].State == "充电" && Near(s.Batteries[0].ChargePowerW, 12.2) && !s.Batteries[0].DischargePowerW.HasValue, "charge direction");

            const string report = "<BatteryReport xmlns='http://schemas.microsoft.com/battery/2012'><Batteries><Battery><Id>5B10W13931</Id><SerialNumber>private-example</SerialNumber><DesignCapacity>51010</DesignCapacity><FullChargeCapacity>37800</FullChargeCapacity><CycleCount>1184</CycleCount></Battery><Battery><Name>Second</Name><DesignCapacity>0</DesignCapacity><FullChargeCapacity>4294967295</FullChargeCapacity><CycleCount>-</CycleCount></Battery></Batteries></BatteryReport>";
            var parsed = BatteryReportCache.Parse(report);
            Assert(parsed.Count == 2 && parsed[0].Name == "5B10W13931", "namespaced Windows report and model ID");
            Assert(Near(parsed[0].DesignCapacityWh, 51.01) && Near(parsed[0].FullChargeCapacityWh, 37.8) && parsed[0].CycleCount == 1184, "report unit and cycles");
            Assert(!parsed[1].CycleCount.HasValue && !parsed[1].DesignCapacityWh.HasValue && !parsed[1].FullChargeCapacityWh.HasValue, "report unsupported fields");
            Assert(BatteryReportCache.Parse("<BatteryReport><Batteries><Battery><CycleCount>0</CycleCount></Battery></Batteries></BatteryReport>")[0].CycleCount == 0, "cycle zero valid");
            Assert(BatteryReportCache.Parse("<BatteryReport/>").Count == 0, "no batteries");
            Assert(!BatteryReportCache.Parse("<BatteryReport><Batteries><Battery><RelativeCapacity>1</RelativeCapacity><DesignCapacity>100</DesignCapacity><FullChargeCapacity>80</FullChargeCapacity></Battery></Batteries></BatteryReport>")[0].DesignCapacityWh.HasValue, "relative capacities are not energy units");
            bool rejected = false;
            try { BatteryReportCache.Parse("<!DOCTYPE BatteryReport [<!ENTITY x SYSTEM 'file:///private'>]><BatteryReport>&x;</BatteryReport>"); }
            catch (XmlException) { rejected = true; }
            Assert(rejected, "external entities disabled");

            string cacheDirectory = Path.Combine(directory, "release", "device-telemetry-test");
            Directory.CreateDirectory(cacheDirectory);
            File.WriteAllText(Path.Combine(cacheDirectory, "battery-health.xml"), "<BatteryCache UpdatedUtc='" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "'><Battery><Name>Battery A</Name><DesignWh>51.01</DesignWh><FullWh>37.8</FullWh><CycleCount>1184</CycleCount></Battery></BatteryCache>");
            var cache = new BatteryReportCache(cacheDirectory);
            cache.RefreshIfDue(); // Recent fixture must not launch powercfg.
            s = new Snapshot(); cache.Apply(s);
            Assert(s.Batteries.Count == 1 && s.Batteries[0].CycleCount == 1184 && Near(s.Batteries[0].HealthPercent, 37.8 / 51.01 * 100), "report-only fallback");
            s = new Snapshot(); s.Batteries.Add(new BatteryReading { Name = "Battery A", DesignCapacityWh = 50, FullChargeCapacityWh = 40 }); cache.Apply(s);
            Assert(Near(s.Batteries[0].FullChargeCapacityWh, 40) && Near(s.Batteries[0].HealthPercent, 80) && s.Batteries[0].CycleCount == 1184, "live capacity wins and cycles supplemented");
            s = new Snapshot(); s.Batteries.Add(new BatteryReading { Name = "Unmatched A" }); s.Batteries.Add(new BatteryReading { Name = "Unmatched B" }); cache.Apply(s);
            Assert(!s.Batteries[0].CycleCount.HasValue && !s.Batteries[1].CycleCount.HasValue, "ambiguous batteries are not paired");
            return count;
        }
    }
}
