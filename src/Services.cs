using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;

namespace CarbonControl
{
    public static class Log
    {
        private static readonly object gate = new object();
        public static string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CarbonControl");
        public static void Write(object message)
        {
            try { lock (gate) { Directory.CreateDirectory(DirectoryPath); var p = Path.Combine(DirectoryPath, "carbon.log");
                if (File.Exists(p) && new FileInfo(p).Length > 1024 * 1024) File.Move(p, p + "." + DateTime.UtcNow.Ticks);
                File.AppendAllText(p, DateTime.Now.ToString("s") + " " + message + Environment.NewLine); } } catch { }
        }
    }
    public static class StartupTask
    {
        private static string Name { get { return "CarbonControl-" + WindowsIdentity.GetCurrent().User.Value; } }
        public static bool IsEnabled()
        { try { dynamic s = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); s.Connect(); return s.GetFolder("\\").GetTask(Name).Enabled; } catch { return false; } }
        public static void Set(bool enabled)
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); service.Connect();
            dynamic root = service.GetFolder("\\");
            if (!enabled) { if (IsEnabled()) root.DeleteTask(Name, 0); return; }
            dynamic task = service.NewTask(0);
            string user = WindowsIdentity.GetCurrent().User.Value;
            task.RegistrationInfo.Description = "Carbon Control 温度监控与风扇管理";
            task.Principal.UserId = user; task.Principal.LogonType = 3; task.Principal.RunLevel = 1;
            task.Settings.Enabled = true; task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false; task.Settings.ExecutionTimeLimit = "PT0S";
            task.Settings.MultipleInstances = 2; task.Settings.StartWhenAvailable = true;
            dynamic trigger = task.Triggers.Create(9); trigger.UserId = user; trigger.Delay = "PT10S";
            dynamic action = task.Actions.Create(0); action.Path = Process.GetCurrentProcess().MainModule.FileName;
            action.Arguments = "--tray"; action.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            root.RegisterTaskDefinition(Name, task, 6, user, null, 3, null);
        }
    }
    public sealed class FanController
    {
        public const int TelemetryIntervalSeconds = 5;
        public const int RefreshIntervalSeconds = 15;
        private readonly IFanDevice device;
        private readonly FanPolicy policy = new FanPolicy();
        private DateTime nextTelemetry = DateTime.MinValue, nextRefresh = DateTime.MinValue, nextRetry = DateTime.MinValue;
        private int lastRequest = 128, telemetryFailures, commandFailures;
        private int? confirmedLevel;
        private bool restorePending;
        public string Status { get; private set; }
        public int? Rpm { get; private set; }
        public int? ActualLevel { get; private set; }
        public bool OwnsFan { get; private set; }
        public bool Healthy { get; private set; }
        public FanController(IFanDevice d) { device = d; Status = "系统自动"; }
        public void ConfigurationChanged()
        {
            // A newly applied curve starts from its own thresholds rather than
            // inheriting the previous curve's cooling dwell or retry delay.
            policy.Reset(); lastRequest = -1; nextRetry = DateTime.MinValue;
        }
        public void Tick(FanMode mode, int manual, IList<CurvePoint> curve, Snapshot sample, DateTime now)
        {
            Healthy = false;
            int requested = policy.Decide(mode, manual, curve, sample.CpuTemperature, sample.Timestamp, now);
            bool changed = requested != lastRequest; lastRequest = requested;
            bool restoring = false;
            try
            {
                if (restorePending)
                {
                    if (now < nextRetry && !changed) { Status = "正在恢复系统自动"; return; }
                    restoring = true; Restore(); restoring = false; restorePending = false;
                }
                if (requested == 128)
                {
                    if (OwnsFan) { restoring = true; Restore(); restoring = false; }
                }
                else if (!OwnsFan || confirmedLevel != requested || now >= nextRefresh)
                {
                    if (now < nextRetry && !changed)
                    { Status = "系统自动 · 正在重连"; Healthy = !OwnsFan; return; }
                    Apply(requested, now);
                }
                if (now >= nextTelemetry)
                {
                    try
                    {
                        var combined = device as IFanTelemetryDevice;
                        if (combined != null)
                        { var reading = combined.ReadTelemetry(); ActualLevel = reading.Level; Rpm = reading.Rpm; }
                        else { ActualLevel = device.ReadLevel(); Rpm = device.ReadRpm(); }
                        telemetryFailures = 0; nextTelemetry = now.AddSeconds(TelemetryIntervalSeconds);
                    }
                    catch (Exception ex)
                    {
                        telemetryFailures++;
                        if (telemetryFailures == 1) Log.Write(ex);
                        nextTelemetry = now.AddSeconds(Math.Min(4, 1 << Math.Min(2, telemetryFailures - 1)));
                        Status = "转速刷新中";
                        // A single busy read does not undo an acknowledged fan setting.
                        // No failed read renews the watchdog lease. Three failures restore auto.
                        if (telemetryFailures >= 3)
                        {
                            Rpm = null; ActualLevel = null; restoring = true; Restore(); restoring = false;
                            nextRetry = now.AddSeconds(2); nextTelemetry = now.AddSeconds(TelemetryIntervalSeconds);
                            Status = "系统自动 · 正在重连"; Healthy = true;
                        }
                        return;
                    }
                    if (requested != 128 && ActualLevel.HasValue && (ActualLevel.Value & 0xC7) != requested)
                        Apply(requested, now); // Reassert if firmware/another client changed the mode.
                }
                if (telemetryFailures != 0) { Status = "转速刷新中"; return; }
                Status = requested == 128 ? "系统自动" : "已应用 · " + requested + " 档";
                if (mode != FanMode.Bios && requested == 128) Status = "系统自动 · 等待温度数据";
                if (requested == 7 && sample.CpuTemperature >= 92) Status = "温度调节 · 7 档";
                Healthy = true;
            }
            catch (Exception ex)
            {
                Rpm = null; ActualLevel = null; Log.Write(ex);
                confirmedLevel = null; commandFailures++;
                nextRetry = now.AddSeconds(Math.Min(4, 1 << Math.Min(2, commandFailures - 1)));
                nextTelemetry = nextRetry;
                if (restoring) { restorePending = true; Status = "风扇连接中断 · 正在恢复自动"; return; }
                try { Restore(); Status = "系统自动 · 正在重连"; Healthy = true; }
                catch (Exception restoreError) { restorePending = true; Log.Write(restoreError); Status = "风扇连接中断 · 正在恢复自动"; }
            }
        }
        private void Apply(int requested, DateTime now)
        {
            OwnsFan = true; // A partial write also needs automatic-mode recovery.
            device.SetLevel(requested); // ThinkPadFan verifies 0x2F inside this call.
            confirmedLevel = requested; ActualLevel = requested;
            nextRefresh = now.AddSeconds(RefreshIntervalSeconds);
            commandFailures = 0; nextRetry = DateTime.MinValue;
        }
        public void Restore()
        {
            if (OwnsFan) { device.SetLevel(128); OwnsFan = false; confirmedLevel = 128; ActualLevel = 128; }
            restorePending = false; telemetryFailures = 0;
            policy.Reset();
        }
    }
    public sealed class Engine : IDisposable
    {
        private readonly object gate = new object();
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly ManualResetEvent suspendAck = new ManualResetEvent(false);
        private readonly AutoResetEvent configurationChanged = new AutoResetEvent(false);
        private Thread sensorThread, fanThread;
        private Snapshot current = new Snapshot();
        private FanMode mode = FanMode.Bios;
        private int manual = 4;
        private List<CurvePoint> curve = FanPolicy.Preset(FanMode.Balanced);
        private bool suspended;
        private int configurationRevision;
        public string FanStatus = "正在连接风扇";
        public int? FanRpm, FanLevel;
        public bool FanReady;
        public bool Stopped { get { return (fanThread == null || !fanThread.IsAlive) && (sensorThread == null || !sensorThread.IsAlive); } }
        public Snapshot Current { get { lock (gate) return current; } }
        public FanMode Mode { get { lock (gate) return mode; } }
        public void Configure(FanMode m, int level, IList<CurvePoint> c)
        { FanPolicy.Validate(c); lock (gate) { mode = m; manual = level; curve = c.Select(p => new CurvePoint(p.Temperature, p.Level)).ToList(); configurationRevision++; } configurationChanged.Set(); }
        public void Suspend(bool value)
        {
            suspendAck.Reset(); lock (gate) { suspended = value; current = new Snapshot(); configurationRevision++; }
            configurationChanged.Set();
            if (value && fanThread != null && fanThread.IsAlive) suspendAck.WaitOne(2000);
        }
        public void Start()
        {
            sensorThread = new Thread(Sensors) { IsBackground = true, Name = "Carbon sensors" };
            fanThread = new Thread(Fans) { IsBackground = true, Name = "Carbon fan policy" };
            sensorThread.Start(); fanThread.Start();
        }
        private void Sensors()
        {
            try
            {
                using (var monitor = new HardwareMonitor())
                    while (!stop.WaitOne(0))
                    {
                        bool pause; lock (gate) pause = suspended;
                        if (!pause) { var s = monitor.Read(); lock (gate) current = s; }
                        if (stop.WaitOne(1000)) break;
                    }
            }
            catch (Exception ex) { Log.Write(ex); lock (gate) current = new Snapshot { Status = ex.Message }; }
        }
        private void Fans()
        {
            IFanDevice device = null; FanController controller = null; Watchdog watchdog = null;
            int appliedRevision = -1;
            try
            {
                device = new ThinkPadFan(); controller = new FanController(device); FanReady = true;
                while (!stop.WaitOne(0))
                {
                    FanMode selected; int level, revision; List<CurvePoint> points; Snapshot s;
                    lock (gate) { selected = suspended ? FanMode.Bios : mode; level = manual; points = curve; s = current; revision = configurationRevision; }
                    if (revision != appliedRevision) { controller.ConfigurationChanged(); appliedRevision = revision; }
                    if (selected != FanMode.Bios && watchdog == null) watchdog = new Watchdog();
                    // A successful control iteration renews the watchdog lease.
                    controller.Tick(selected, level, points, s, DateTime.UtcNow);
                    if (watchdog != null && controller.Healthy) watchdog.Pulse();
                    FanStatus = controller.Status; FanRpm = controller.Rpm; FanLevel = controller.ActualLevel;
                    if (selected == FanMode.Bios && !controller.OwnsFan) suspendAck.Set();
                    if (WaitHandle.WaitAny(new WaitHandle[] { stop, configurationChanged }, 1000) == 0) break;
                }
            }
            catch (Exception ex) { FanReady = false; FanStatus = ex.Message; Log.Write(ex); }
            finally
            {
                bool restored = true;
                if (controller != null) { try { controller.Restore(); } catch (Exception ex) { restored = false; Log.Write(ex); } }
                if (watchdog != null) watchdog.Close(restored);
                if (device != null) device.Dispose();
            }
        }
        public void Dispose()
        { stop.Set(); if (fanThread != null) fanThread.Join(3000); if (sensorThread != null) sensorThread.Join(1500); }
    }
    public sealed class Watchdog
    {
        private EventWaitHandle pulse, done, ready;
        private Process child;
        public Watchdog()
        {
            string token = Guid.NewGuid().ToString("N");
            pulse = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CarbonPulse-" + token);
            done = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\CarbonDone-" + token);
            ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\CarbonReady-" + token);
            var p = Process.GetCurrentProcess();
            try
            {
                child = Process.Start(new ProcessStartInfo(p.MainModule.FileName, "--watchdog " + p.Id + " " + token) { UseShellExecute = false, CreateNoWindow = true });
                if (!ready.WaitOne(4000)) throw new IOException("风扇守护进程未就绪");
            }
            catch { Close(true); throw; }
        }
        public void Pulse() { if (child == null || child.HasExited) throw new IOException("风扇守护进程已退出，恢复系统自动"); pulse.Set(); }
        public void Close(bool restored) { if (restored) done.Set(); pulse.Dispose(); done.Dispose(); ready.Dispose(); if (child != null) child.Dispose(); }
        public static void Run(int parentId, string token)
        {
            try
            {
                using (var parent = Process.GetProcessById(parentId))
                using (var pulse = EventWaitHandle.OpenExisting("Local\\CarbonPulse-" + token))
                using (var done = EventWaitHandle.OpenExisting("Local\\CarbonDone-" + token))
                using (var ready = EventWaitHandle.OpenExisting("Local\\CarbonReady-" + token))
                using (var fan = new ThinkPadFan())
                {
                    ready.Set(); var clock = Stopwatch.StartNew();
                    while (true)
                    {
                        int result = WaitHandle.WaitAny(new WaitHandle[] { done, pulse }, 1000);
                        if (result == 0) return;
                        if (result == 1) clock.Restart();
                        if (parent.HasExited || clock.ElapsedMilliseconds > 7000)
                        {
                            try { fan.SetLevel(128); if (parent.HasExited) return; }
                            catch (Exception ex) { Log.Write(ex); }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write(ex); }
        }
    }
}
