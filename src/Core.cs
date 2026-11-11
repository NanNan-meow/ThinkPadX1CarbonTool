using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Xml.Serialization;

namespace CarbonControl
{
    public enum FanMode { Bios, Quiet, Balanced, Cool, Custom, Manual }
    public enum StartupFanPolicy { Bios, Quiet, Balanced, Cool, Custom, Slot1, Slot2, Slot3 }
    public class CurvePoint
    {
        public int Temperature { get; set; }
        public int Level { get; set; }
        public CurvePoint() { }
        public CurvePoint(int t, int l) { Temperature = t; Level = l; }
    }
    public class CurveSlot
    {
        public string Name { get; set; }
        public CurvePoint[] Curve { get; set; }
        [XmlIgnore]
        public bool HasCurve { get { return Curve != null && Curve.Length != 0; } }
    }
    public class Settings
    {
        public const int CurveSlotCount = 3;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string destination, uint flags);
        private CurveSlot[] curveSlots;
        private bool slotsProvided;
        public string Theme { get; set; }
        public bool CloseToTray { get; set; }
        public bool StartMinimized { get; set; }
        public int ManualLevel { get; set; }
        public StartupFanPolicy StartupPolicy { get; set; }
        public CurvePoint[] Curve { get; set; }
        public CurveSlot[] CurveSlots
        {
            get { return curveSlots; }
            set { curveSlots = value; slotsProvided = true; }
        }
        public int SelectedCurveSlot { get; set; }
        public Settings()
        {
            Theme = "System"; CloseToTray = true; ManualLevel = 4; StartupPolicy = StartupFanPolicy.Balanced;
            Curve = FanPolicy.Preset(FanMode.Balanced).ToArray();
            curveSlots = new CurveSlot[CurveSlotCount];
            for (int i = 0; i < curveSlots.Length; i++) curveSlots[i] = new CurveSlot { Name = DefaultSlotName(i) };
        }
        public static Settings Read(string path)
        {
            if (!File.Exists(path)) return new Settings();
            using (var f = File.OpenRead(path))
            {
                var s = (Settings)new XmlSerializer(typeof(Settings)).Deserialize(f);
                FanPolicy.Validate(s.Curve);
                if (s.ManualLevel < 0 || s.ManualLevel > 7) s.ManualLevel = 4;
                if (s.Theme != "System" && s.Theme != "Dark" && s.Theme != "Light") s.Theme = "System";
                s.NormalizeStartupPolicy();
                s.NormalizeCurveSlots(!s.slotsProvided);
                return s;
            }
        }
        public void Save(string path)
        {
            FanPolicy.Validate(Curve);
            NormalizeStartupPolicy();
            NormalizeCurveSlots(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            using (var f = File.Create(temp)) new XmlSerializer(typeof(Settings)).Serialize(f, this);
            if (!MoveFileEx(temp, path, 1 | 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void SaveCurveSlot(int index, string name, IList<CurvePoint> points)
        {
            CheckSlotIndex(index);
            FanPolicy.Validate(points);
            var stored = CloneCurve(points);
            NormalizeCurveSlots(false);
            curveSlots[index] = new CurveSlot { Name = SlotName(index, name), Curve = stored };
            SelectedCurveSlot = index;
        }
        public CurvePoint[] LoadCurveSlot(int index)
        {
            CheckSlotIndex(index);
            NormalizeCurveSlots(false);
            if (!curveSlots[index].HasCurve) throw new InvalidOperationException("此槽位尚未保存曲线");
            return CloneCurve(curveSlots[index].Curve);
        }
        private void NormalizeStartupPolicy()
        {
            if (!Enum.IsDefined(typeof(StartupFanPolicy), StartupPolicy)) StartupPolicy = StartupFanPolicy.Balanced;
        }
        private void NormalizeCurveSlots(bool migrateLegacy)
        {
            var normalized = new CurveSlot[CurveSlotCount];
            for (int i = 0; i < normalized.Length; i++)
            {
                CurveSlot source = curveSlots != null && i < curveSlots.Length ? curveSlots[i] : null;
                CurvePoint[] stored = null;
                if (source != null && source.Curve != null && source.Curve.Length != 0)
                {
                    try { FanPolicy.Validate(source.Curve); stored = CloneCurve(source.Curve); }
                    catch (ArgumentException) { }
                }
                normalized[i] = new CurveSlot { Name = SlotName(i, source == null ? null : source.Name), Curve = stored };
            }
            if (migrateLegacy) normalized[0].Curve = CloneCurve(Curve);
            curveSlots = normalized;
            SelectedCurveSlot = Math.Max(0, Math.Min(CurveSlotCount - 1, SelectedCurveSlot));
            slotsProvided = true;
        }
        private static string DefaultSlotName(int index) { return "我的曲线 " + (index + 1); }
        private static string SlotName(int index, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return DefaultSlotName(index);
            return name.Length > 24 ? name.Substring(0, 24) : name;
        }
        private static CurvePoint[] CloneCurve(IList<CurvePoint> points)
        { return points.Select(p => new CurvePoint(p.Temperature, p.Level)).ToArray(); }
        private static void CheckSlotIndex(int index)
        { if (index < 0 || index >= CurveSlotCount) throw new ArgumentOutOfRangeException("index"); }
    }
    public sealed class StartupPolicyGate
    {
        private bool pending = true;
        public bool Pending { get { return pending; } }
        public void Cancel() { pending = false; }
        public bool TryTake(bool fanReady, bool fanConfirmed, Snapshot sample, DateTime now)
        {
            if (!pending || !fanReady || !fanConfirmed || sample == null || !SensorMap.Valid(sample.CpuTemperature) || sample.CpuTemperature > 125)
                return false;
            double age = (now - sample.Timestamp).TotalSeconds;
            if (age > 5 || age < -1) return false;
            pending = false;
            return true;
        }
    }
    public static class StartupPolicyResolver
    {
        public static FanMode Resolve(Settings settings, out CurvePoint[] curve)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            StartupFanPolicy selected = Enum.IsDefined(typeof(StartupFanPolicy), settings.StartupPolicy)
                ? settings.StartupPolicy : StartupFanPolicy.Balanced;
            if (selected >= StartupFanPolicy.Slot1 && selected <= StartupFanPolicy.Slot3)
            {
                int index = (int)selected - (int)StartupFanPolicy.Slot1;
                var slots = settings.CurveSlots;
                if (slots == null || index >= slots.Length || slots[index] == null || !slots[index].HasCurve)
                    throw new InvalidOperationException("启动策略的曲线存档尚未保存，请先保存曲线或选择其他策略。");
                FanPolicy.Validate(slots[index].Curve);
                curve = Clone(slots[index].Curve);
                return FanMode.Custom;
            }
            if (selected == StartupFanPolicy.Custom)
            {
                FanPolicy.Validate(settings.Curve);
                curve = Clone(settings.Curve);
                return FanMode.Custom;
            }
            FanMode mode = selected == StartupFanPolicy.Bios ? FanMode.Bios :
                selected == StartupFanPolicy.Quiet ? FanMode.Quiet :
                selected == StartupFanPolicy.Cool ? FanMode.Cool : FanMode.Balanced;
            curve = FanPolicy.Preset(mode).ToArray();
            return mode;
        }
        private static CurvePoint[] Clone(IList<CurvePoint> points)
        { return points.Select(p => new CurvePoint(p.Temperature, p.Level)).ToArray(); }
    }
    public sealed class FanPolicy
    {
        private int previous = -1;
        private FanMode previousMode = FanMode.Bios;
        private DateTime lowerSince;
        private int lowerCandidate = -1;
        public static List<CurvePoint> Preset(FanMode mode)
        {
            int[] t = mode == FanMode.Quiet ? new[] { 35, 55, 65, 72, 80, 87 } :
                mode == FanMode.Cool ? new[] { 30, 40, 50, 60, 70, 80 } : new[] { 35, 48, 58, 68, 78, 85 };
            int[] l = mode == FanMode.Cool ? new[] { 2, 3, 4, 5, 6, 7 } : new[] { 0, 1, 2, 3, 5, 7 };
            return t.Select((x, i) => new CurvePoint(x, l[i])).ToList();
        }
        public static void Validate(IList<CurvePoint> points)
        {
            if (points == null || points.Count < 2 || points.Count > 12) throw new ArgumentException("曲线需要 2–12 个节点");
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (p == null || p.Temperature < 25 || p.Temperature > 95 || p.Level < 0 || p.Level > 7)
                    throw new ArgumentException("温度范围 25–95°C，档位范围 0–7");
                if (i > 0 && (p.Temperature <= points[i - 1].Temperature || p.Level < points[i - 1].Level))
                    throw new ArgumentException("温度需递增，档位需保持或递增");
            }
            if (points[points.Count - 1].Level != 7 || points[points.Count - 1].Temperature > 90)
                throw new ArgumentException("最后一个节点需在 90°C 以内达到 7 档");
        }
        public int Decide(FanMode mode, int manual, IList<CurvePoint> custom, double? temperature, DateTime sampled, DateTime now)
        {
            if (mode == FanMode.Bios || !temperature.HasValue || double.IsNaN(temperature.Value) ||
                double.IsInfinity(temperature.Value) || temperature < 0 || temperature > 125 ||
                (now - sampled).TotalSeconds > 5 || sampled > now.AddSeconds(1))
            { Reset(); return 128; }
            if (mode != previousMode) { previous = -1; lowerCandidate = -1; previousMode = mode; }
            if (temperature >= 92) { previous = 7; lowerCandidate = -1; return 7; }
            if (mode == FanMode.Manual) { previous = Math.Max(0, Math.Min(7, manual)); return previous; }
            var points = mode == FanMode.Custom ? custom : Preset(mode);
            Validate(points);
            int target = LevelAt(points, temperature.Value);
            if (previous < 0 || target >= previous) { previous = target; lowerCandidate = -1; return target; }
            // Cooling uses a 3°C hysteresis and an eight-second dwell; heating is immediate.
            int cooled = LevelAt(points, temperature.Value + 3);
            if (cooled >= previous) { lowerCandidate = -1; return previous; }
            if (lowerCandidate != cooled) { lowerCandidate = cooled; lowerSince = now; }
            if ((now - lowerSince).TotalSeconds >= 8) { previous = cooled; lowerCandidate = -1; }
            return previous;
        }
        private static int LevelAt(IList<CurvePoint> p, double t)
        {
            int level = p[0].Level;
            foreach (var point in p) { if (t >= point.Temperature) level = point.Level; else break; }
            return level;
        }
        public void Reset() { previous = -1; previousMode = FanMode.Bios; lowerCandidate = -1; }
    }
    public class SensorReading
    {
        public string Hardware, Kind, Name, Id;
        public string HardwareName, HardwareId;
        public double? Value;
    }
    public class Snapshot
    {
        public DateTime Timestamp = DateTime.UtcNow;
        public double? CpuTemperature, CpuClock, CpuPower, CpuLoad, GpuLoad, GpuPower;
        public string Status = "正在连接传感器";
        public List<SensorReading> Sensors = new List<SensorReading>();
        public List<BatteryReading> Batteries = new List<BatteryReading>();
        public List<StorageReading> Drives = new List<StorageReading>();
    }
    public static class SensorMap
    {
        public static Snapshot Map(List<SensorReading> sensors)
        {
            var s = new Snapshot { Sensors = sensors };
            var cpu = sensors.Where(x => x.Hardware == "Cpu").ToList();
            var gpu = sensors.Where(x => x.Hardware == "GpuIntel").ToList();
            s.CpuTemperature = First(cpu, "Temperature", "CPU Package");
            if (!s.CpuTemperature.HasValue) s.CpuTemperature = Max(cpu.Where(x => x.Kind == "Temperature" && !x.Name.Contains("Distance") && (x.Name.StartsWith("CPU Core") || x.Name == "Core Max")));
            s.CpuPower = First(cpu, "Power", "CPU Package");
            s.CpuLoad = First(cpu, "Load", "CPU Total");
            var clocks = cpu.Where(x => x.Kind == "Clock" && x.Name.Contains("Core") && Valid(x.Value)).Select(x => x.Value.Value).ToArray();
            s.CpuClock = clocks.Length == 0 ? (double?)null : clocks.Average() / 1000;
            s.GpuPower = First(gpu, "Power", "GPU Power") ?? First(cpu, "Power", "CPU Graphics");
            s.GpuLoad = Max(gpu.Where(x => x.Kind == "Load" && !x.Name.Contains("Memory")));
            return s;
        }
        private static double? First(IEnumerable<SensorReading> items, string kind, string name)
        { var x = items.FirstOrDefault(v => v.Kind == kind && v.Name == name && Valid(v.Value)); return x == null ? null : x.Value; }
        private static double? Max(IEnumerable<SensorReading> items)
        { var a = items.Where(x => Valid(x.Value)).Select(x => x.Value.Value).ToArray(); return a.Length == 0 ? (double?)null : a.Max(); }
        public static bool Valid(double? v) { return v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value) && v >= 0; }
    }
}
