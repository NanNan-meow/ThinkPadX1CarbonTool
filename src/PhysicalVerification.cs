using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace CarbonControl
{
    public static class PhysicalVerification
    {
        private static string N(double? value) { return value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "—"; }
        private static string Error(Exception ex) { while (ex.InnerException != null) ex = ex.InnerException; return ex.Message.Replace("\r", " ").Replace("\n", " "); }
        public static int Run(string output)
        {
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            Log.DirectoryPath = Path.GetDirectoryName(output);
            var text = new StringBuilder();
            int unverified = 0;
            Action<string, string, string> record = (item, result, evidence) =>
            {
                text.AppendLine(item + " | " + result + " | " + evidence);
                if (result != "已验证" && result != "读数") unverified++;
                try { File.WriteAllText(output, text.ToString(), Encoding.UTF8); } catch (Exception ex) { Log.Write(ex); }
            };
            text.AppendLine("Carbon Control 实机测试 · " + DateTime.Now.ToString("s"));
            bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            text.AppendLine("Administrator: " + admin);
            bool first;
            using (var instance = new Mutex(true, "Local\\CarbonControl.Main", out first))
            {
                if (!first)
                {
                    record("测试状态", "未测试", "请从托盘退出 Carbon Control 后重试。");
                    return 2;
                }
                try
                {
                    text.AppendLine("机型: " + Machine.Model + " · " + Machine.Cpu);
                    using (var monitor = new HardwareMonitor(Path.Combine(Log.DirectoryPath, "telemetry")))
                    {
                        var sample = monitor.Read();
                        for (int i = 0; i < 10; i++)
                        {
                            Thread.Sleep(1000); sample = monitor.Read();
                            if (i >= 2 && sample.Batteries.Any(b => b.CycleCount.HasValue) && sample.Drives.Count > 0 && sample.CpuTemperature.HasValue) break;
                        }
                        record("CPU/GPU", "读数", "CPU " + N(sample.CpuLoad) + "% / " + N(sample.CpuClock) + " GHz / " + N(sample.CpuTemperature) + " °C / " + N(sample.CpuPower) + " W; GPU " + N(sample.GpuLoad) + "% / " + N(sample.GpuPower) + " W");
                        if (!sample.CpuTemperature.HasValue || !sample.CpuPower.HasValue || sample.CpuPower <= 0)
                            record("CPU 温度与功耗", "未验证", "未取得有效硬件读数。");
                        else record("CPU 温度与功耗", "已验证", "来自处理器传感器。");
                        if (sample.Batteries.Count == 0) record("电池", "未验证", "没有电池读数。");
                        foreach (var b in sample.Batteries)
                        {
                            record("电池健康与容量", b.HealthPercent.HasValue && b.FullChargeCapacityWh.HasValue ? "已验证" : "未验证",
                                b.Name + "; 健康 " + N(b.HealthPercent) + "%; 设计 " + N(b.DesignCapacityWh) + " Wh; 满充 " + N(b.FullChargeCapacityWh) + " Wh; 循环 " + b.CycleCount);
                            record(b.State == "充电" ? "电池充电功率" : b.State == "放电" ? "电池放电功率" : "电池当前功率", b.ChargePowerW.HasValue || b.DischargePowerW.HasValue ? "已验证" : "未验证",
                                b.State + "; 充电 " + N(b.ChargePowerW) + " W; 放电 " + N(b.DischargePowerW) + " W; 电压 " + N(b.Voltage) + " V");
                        }
                        if (sample.Drives.Count == 0) record("SSD", "未验证", admin ? "没有存储设备读数。" : "当前进程没有管理员权限，未取得存储设备读数。");
                        foreach (var d in sample.Drives)
                        {
                            record("SSD 温度", d.Temperature.HasValue ? "已验证" : "未验证", d.Name + "; " + N(d.Temperature) + " °C");
                            record("SSD 剩余寿命", d.HealthPercent.HasValue ? "已验证" : "未验证", N(d.HealthPercent) + "%");
                            record("SSD 累计写入", d.TotalWrittenGB.HasValue ? "已验证" : "未验证", N(d.TotalWrittenGB) + " GB");
                            record("SSD 累计读取", d.TotalReadGB.HasValue ? "已验证" : "未验证", N(d.TotalReadGB) + " GB");
                        }
                        TestFan(monitor, record);
                        TestPower(monitor, record);
                    }
                }
                catch (Exception ex) { record("测试过程", "未完成", Error(ex)); }
                finally { instance.ReleaseMutex(); }
            }
            text.AppendLine("完成时间: " + DateTime.Now.ToString("s"));
            text.AppendLine("未完成验证的项目: " + unverified);
            File.WriteAllText(output, text.ToString(), Encoding.UTF8);
            return unverified == 0 ? 0 : 2;
        }
        private static void TestPower(HardwareMonitor monitor, Action<string, string, string> record)
        {
            var backend = new WindowsPowerPlanBackend();
            string journal = Path.Combine(Log.DirectoryPath, "physical-power-recovery.xml");
            var controller = new PerformanceController(backend, journal);
            Guid original;
            PowerPlanValues values;
            try { StartPowerWatchdog(journal); controller.RecoverPreviousSession(); original = backend.GetActiveScheme(); values = backend.Read(original); }
            catch (Exception ex) { record("电源方案", "未验证", Error(ex)); return; }
            double? lowClock = null, highClock = null, lowLoad = null, highLoad = null;
            ThinkPadFan linkedFan = null; Watchdog linkedWatchdog = null; FanController linkedControl = null;
            bool linkedChanged = false;
            try
            {
                linkedFan = new ThinkPadFan();
                if ((linkedFan.ReadLevel() & 128) == 0) throw new IOException("风扇当前处于外部手动模式");
                linkedWatchdog = new Watchdog(); linkedControl = new FanController(linkedFan);
            }
            catch (Exception ex)
            {
                record("模式风扇联动", "未验证", Error(ex));
                if (linkedFan != null) linkedFan.Dispose(); linkedFan = null;
            }
            try
            {
                foreach (PerformanceMode mode in new[] { PerformanceMode.LowTemperature, PerformanceMode.Balanced, PerformanceMode.Performance })
                {
                    controller.Apply(mode);
                    var desired = PerformanceController.ValuesFor(mode);
                    bool applied = backend.GetActiveScheme() != original && backend.Read(backend.GetActiveScheme()).Same(desired);
                    record("电源模式 " + mode, applied ? "已验证" : "未验证", "活动方案 AC/DC 睿频=" + desired.AcBoost + "/" + desired.DcBoost + "; 设置已读回。");
                    var sample = monitor.Read();
                    FanMode fanMode = mode == PerformanceMode.Performance ? FanMode.Cool : FanMode.Balanced;
                    if (linkedControl != null)
                    {
                        linkedChanged = true; linkedControl.ConfigurationChanged();
                        linkedControl.Tick(fanMode, 0, FanPolicy.Preset(fanMode), sample, DateTime.UtcNow);
                        if (!linkedControl.Healthy || !linkedControl.OwnsFan) throw new IOException(linkedControl.Status);
                        linkedWatchdog.Pulse();
                        record("电源与风扇联动 " + mode, linkedControl.Healthy && linkedControl.OwnsFan ? "已验证" : "未确认", linkedControl.Status);
                    }
                    if (applied && linkedControl != null && mode != PerformanceMode.Balanced && sample.CpuTemperature.HasValue && HasHardwareClock(sample))
                    {
                        var measured = MeasureLoad(monitor, s => {
                            linkedControl.Tick(fanMode, 0, FanPolicy.Preset(fanMode), s, DateTime.UtcNow);
                            if (!linkedControl.Healthy || !linkedControl.OwnsFan) throw new IOException(linkedControl.Status);
                            linkedWatchdog.Pulse();
                        });
                        if (mode == PerformanceMode.LowTemperature) { lowClock = measured.CpuClock; lowLoad = measured.CpuLoad; }
                        else { highClock = measured.CpuClock; highLoad = measured.CpuLoad; }
                        record("处理器负载 " + mode, "读数", "CPU " + N(measured.CpuLoad) + "%; 平均频率 " + N(measured.CpuClock) + " GHz; 功耗 " + N(measured.CpuPower) + " W; 温度 " + N(measured.CpuTemperature) + " °C");
                    }
                }
                if (lowClock.HasValue && highClock.HasValue)
                    record("低温/性能物理响应", highClock > lowClock * 1.05 && lowLoad >= 70 && highLoad >= 70 && Math.Abs(highLoad.Value - lowLoad.Value) <= 15 ? "已验证" : "未确认", "低温 " + N(lowClock) + " GHz / " + N(lowLoad) + "%负载，性能 " + N(highClock) + " GHz / " + N(highLoad) + "%负载。");
                else record("低温/性能物理响应", "未验证", "没有可比较的处理器硬件频率。");
            }
            catch (Exception ex) { record("电源模式切换", "未验证", Error(ex)); }
            finally
            {
                try
                {
                    controller.Apply(PerformanceMode.System);
                    bool restored = backend.GetActiveScheme() == original && backend.Read(original).Same(values);
                    record("原电源方案状态", restored ? "已验证" : "未确认", "活动方案=" + backend.GetActiveScheme());
                }
                catch (Exception ex) { record("原电源方案恢复", "未完成", Error(ex)); }
                controller.Dispose();
                bool fanRestored = true;
                if (linkedFan != null)
                {
                    if (linkedChanged)
                    {
                        fanRestored = false;
                        try { linkedFan.SetLevel(128); fanRestored = (linkedFan.ReadLevel() & 128) != 0; record("联动测试风扇恢复", fanRestored ? "已验证" : "未确认", "系统自动"); }
                        catch (Exception ex) { record("联动测试风扇恢复", "未完成", Error(ex)); }
                    }
                    if (linkedWatchdog != null) linkedWatchdog.Close(fanRestored);
                    linkedFan.Dispose();
                }
            }
        }
        private static Snapshot MeasureLoad(HardwareMonitor monitor, Action<Snapshot> updateFan)
        {
            var stop = new ManualResetEvent(false);
            var workers = new List<Thread>();
            var readings = new List<Snapshot>();
            try
            {
                for (int i = 0; i < Math.Min(8, Environment.ProcessorCount); i++)
                {
                    var worker = new Thread(delegate() { while (!stop.WaitOne(0)) Thread.SpinWait(50000); }) { IsBackground = true };
                    workers.Add(worker); worker.Start();
                }
                for (int i = 0; i < 6; i++)
                {
                    Thread.Sleep(1000); var s = monitor.Read();
                    updateFan(s);
                    if (HasHardwareClock(s)) readings.Add(s);
                    if (s.CpuTemperature >= 85) break;
                }
            }
            finally { stop.Set(); foreach (var worker in workers) worker.Join(); stop.Dispose(); }
            return new Snapshot { CpuClock = Average(readings.Select(x => x.CpuClock)), CpuPower = Average(readings.Select(x => x.CpuPower)),
                CpuTemperature = Average(readings.Select(x => x.CpuTemperature)), CpuLoad = Average(readings.Select(x => x.CpuLoad)) };
        }
        private static double? Average(IEnumerable<double?> source)
        { var values = source.Where(SensorMap.Valid).Select(x => x.Value).ToArray(); return values.Length == 0 ? (double?)null : values.Average(); }
        private static bool HasHardwareClock(Snapshot sample)
        { return sample.Sensors.Any(x => x.Hardware == "Cpu" && x.Kind == "Clock" && x.Name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase) && SensorMap.Valid(x.Value) && x.Value > 0); }
        private static void StartPowerWatchdog(string journal)
        {
            string signal = "Local\\CarbonPowerReady-" + Guid.NewGuid().ToString("N");
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, signal))
            using (var current = Process.GetCurrentProcess())
            using (var child = Process.Start(new ProcessStartInfo(current.MainModule.FileName,
                "--power-watchdog " + current.Id + " \"" + journal + "\" " + signal) { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (!ready.WaitOne(4000)) throw new IOException("电源测试进程未就绪");
            }
        }
        public static void WatchPower(int parentId, string journal, string readySignal)
        {
            Log.DirectoryPath = Path.GetDirectoryName(Path.GetFullPath(journal));
            try
            {
                using (var ready = EventWaitHandle.OpenExisting(readySignal))
                {
                    Process parent = null;
                    try { parent = Process.GetProcessById(parentId); } catch (ArgumentException) { }
                    ready.Set();
                    if (parent != null) using (parent) parent.WaitForExit();
                }
                for (int i = 0; i < 3 && File.Exists(journal); i++)
                {
                    try { using (var recovery = new PerformanceController(new WindowsPowerPlanBackend(), journal)) recovery.RecoverPreviousSession(); }
                    catch (Exception ex) { Log.Write(ex); }
                    if (File.Exists(journal)) Thread.Sleep(1000);
                }
            }
            catch (Exception ex) { Log.Write(ex); }
        }
        private static void TestFan(HardwareMonitor monitor, Action<string, string, string> record)
        {
            ThinkPadFan fan = null; Watchdog watchdog = null; int? original = null; bool changed = false, restored = true;
            try
            {
                fan = new ThinkPadFan(); var initial = fan.ReadTelemetry();
                original = (initial.Level & 128) != 0 ? 128 : initial.Level & 7;
                record("风扇初始", "读数", "寄存器=" + initial.Level + "; RPM=" + initial.Rpm);
                if ((initial.Level & 128) == 0) { record("风扇物理响应", "未测试", "当前为外部手动模式，寄存器=" + initial.Level); return; }
                var s = monitor.Read();
                if (!s.CpuTemperature.HasValue || s.CpuTemperature > 90) { record("风扇物理响应", "未验证", "没有适合测试的处理器温度读数。"); return; }
                watchdog = new Watchdog(); changed = true; restored = false;
                var control = new FanController(fan);
                foreach (FanMode mode in new[] { FanMode.Balanced, FanMode.Cool })
                {
                    s = monitor.Read();
                    var policy = new FanPolicy();
                    int desired = policy.Decide(mode, 0, FanPolicy.Preset(mode), s.CpuTemperature, s.Timestamp, DateTime.UtcNow);
                    control.ConfigurationChanged(); control.Tick(mode, 0, FanPolicy.Preset(mode), s, DateTime.UtcNow);
                    for (int j = 0; j < 6; j++) { watchdog.Pulse(); Thread.Sleep(1000); }
                    var actual = fan.ReadTelemetry();
                    bool matches = desired != 128 && (actual.Level & 0xC7) == desired;
                    record("联动风扇曲线 " + mode, matches ? "已验证" : "未确认", "CPU " + N(s.CpuTemperature) + " °C; 目标=" + desired + "; 读回=" + actual.Level + "; RPM=" + actual.Rpm);
                }
                fan.SetLevel(7); watchdog.Pulse();
                var samples = new List<int>(); bool confirmed = true;
                for (int i = 0; i < 4; i++)
                {
                    for (int j = 0; j < 4; j++) { watchdog.Pulse(); Thread.Sleep(1000); }
                    var reading = fan.ReadTelemetry(); confirmed &= (reading.Level & 0xC7) == 7;
                    if (reading.Rpm.HasValue) samples.Add(reading.Rpm.Value);
                }
                record("风扇 7 档读回", confirmed ? "已验证" : "未确认", "控制寄存器按 4 秒间隔读回。");
                bool response = samples.Count > 0 && samples.Last() > 0 &&
                    ((!initial.Rpm.HasValue || initial.Rpm <= 0) ? samples.Last() > 1000 : samples.Last() > initial.Rpm * 1.05);
                record("风扇物理响应", response ? "已验证" : "未确认", "初始 " + initial.Rpm + " RPM; 7 档 " + string.Join(", ", samples) + " RPM" + (original == 7 ? "; 初始已为 7 档，无升速对照。" : ""));
            }
            catch (Exception ex) { record("风扇控制", "未验证", Error(ex)); }
            finally
            {
                if (fan != null && changed)
                {
                    try
                    {
                        fan.SetLevel(original.Value); var actual = fan.ReadLevel();
                        restored = original == 128 ? (actual & 128) != 0 : (actual & 0xC7) == original;
                        record("原风扇档位恢复", restored ? "已验证" : "未确认", "目标=" + original + "; 读回=" + actual);
                    }
                    catch (Exception ex) { record("原风扇档位恢复", "未完成", Error(ex)); }
                }
                if (watchdog != null) watchdog.Close(restored);
                if (fan != null) fan.Dispose();
            }
        }
    }
}
