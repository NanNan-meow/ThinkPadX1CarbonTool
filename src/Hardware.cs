using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CarbonControl
{
    public static class Machine
    {
        public static string Value(string path, string name)
        { try { return Convert.ToString(Registry.GetValue(path, name, "")); } catch { return ""; } }
        public static string Model { get { return Value(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName"); } }
        public static string Cpu { get { return Value(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString").Trim(); } }
        public static bool Supported
        {
            get
            {
                string brand = Value(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer");
                return brand.Equals("LENOVO", StringComparison.OrdinalIgnoreCase) &&
                    (Model.StartsWith("20U9", StringComparison.OrdinalIgnoreCase) || Model.StartsWith("20UA", StringComparison.OrdinalIgnoreCase)) && (Cpu.Contains("10210U") || Cpu.Contains("10310U"));
            }
        }
    }
    public sealed class BasicSensors
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
        [DllImport("powrprof.dll")] private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputSize, [Out] ProcessorPower[] output, uint outputSize);
        [StructLayout(LayoutKind.Sequential)] private struct ProcessorPower { public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdle, CurrentIdle; }
        private ulong oldIdle, oldTotal;
        public void Fill(Snapshot s)
        {
            ulong idle, kernel, user;
            if (GetSystemTimes(out idle, out kernel, out user))
            {
                ulong total = kernel + user;
                if (!s.CpuLoad.HasValue && oldTotal != 0 && total > oldTotal)
                    s.CpuLoad = Math.Max(0, Math.Min(100, 100.0 * (1.0 - (double)(idle - oldIdle) / (total - oldTotal))));
                oldTotal = total; oldIdle = idle;
            }
            if (!s.CpuClock.HasValue)
            {
                var p = new ProcessorPower[Environment.ProcessorCount];
                if (CallNtPowerInformation(11, IntPtr.Zero, 0, p, (uint)(p.Length * Marshal.SizeOf(typeof(ProcessorPower)))) == 0)
                    s.CpuClock = p.Average(x => (double)x.CurrentMhz) / 1000;
            }
        }
    }
    public sealed class HardwareMonitor : IDisposable
    {
        private object computer;
        private readonly BasicSensors basic = new BasicSensors();
        private readonly object deviceGate = new object();
        private readonly ManualResetEvent stopDevices = new ManualResetEvent(false);
        private readonly BatteryReportCache batteryReport;
        private List<SensorReading> batterySensors = new List<SensorReading>(), storageSensors = new List<SensorReading>();
        private DateTime batteryUpdated, storageUpdated;
        private Thread deviceWorker;
        private int disposed;
        private string status;
        public HardwareMonitor() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CarbonControl", "telemetry")) { }
        internal HardwareMonitor(string telemetryDirectory)
        {
            batteryReport = new BatteryReportCache(telemetryDirectory);
            try
            {
                if (!HardwareComponents.IsInstalled) { status = "点击「配置硬件组件」连接温度与功耗传感器"; return; }
                var a = HardwareComponents.LoadLibrary();
                computer = Activator.CreateInstance(a.GetType("LibreHardwareMonitor.Hardware.Computer", true));
                Set(computer, "IsCpuEnabled", true); Set(computer, "IsGpuEnabled", true);
                Set(computer, "IsBatteryEnabled", true); Set(computer, "IsStorageEnabled", true);
                Call(computer, "Open"); status = "传感器已连接";
            }
            catch (Exception ex) { status = "传感器连接失败：" + Root(ex).Message; Log.Write(ex); Close(computer); computer = null; }
            finally
            {
                object owner = computer;
                deviceWorker = new Thread(delegate() { ReadDevices(owner); }) { IsBackground = true, Name = "Battery and SSD sensors" };
                deviceWorker.Start();
            }
        }
        public Snapshot Read()
        {
            var data = new List<SensorReading>();
            try
            {
                object owner = computer;
                if (owner != null)
                    foreach (object h in (IEnumerable)Get(owner, "Hardware"))
                        if (!IsDevice(h)) ReadHardware(h, data);
                MergeDevices(data);
                var s = SensorMap.Map(data); DeviceTelemetry.Fill(s); batteryReport.Apply(s); basic.Fill(s); s.Status = status; return s;
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                MergeDevices(data);
                var s = SensorMap.Map(data); s.Status = "传感器读取失败：" + Root(ex).Message;
                DeviceTelemetry.Fill(s); batteryReport.Apply(s); basic.Fill(s); return s;
            }
        }
        private static bool IsDevice(object hardware)
        {
            string type = Get(hardware, "HardwareType").ToString();
            return type == "Battery" || type == "Storage" || type == "HDD";
        }
        private void ReadDevices(object owner)
        {
            DateTime nextBattery = DateTime.MinValue, nextStorage = DateTime.MinValue;
            while (!stopDevices.WaitOne(0))
            {
                DateTime now = DateTime.UtcNow;
                if (owner != null)
                {
                    bool batteryDue = now >= nextBattery, storageDue = now >= nextStorage;
                    var batteries = new List<SensorReading>(); var drives = new List<SensorReading>();
                    try
                    {
                        foreach (object hardware in (IEnumerable)Get(owner, "Hardware"))
                        {
                            string type = Get(hardware, "HardwareType").ToString();
                            if ((type == "Battery" && batteryDue) || ((type == "Storage" || type == "HDD") && storageDue))
                            {
                                var readings = type == "Battery" ? batteries : drives;
                                try { ReadHardware(hardware, readings); }
                                catch (Exception ex)
                                {
                                    Log.Write(ex);
                                    readings.Add(new SensorReading { Hardware = type, HardwareName = Get(hardware, "Name").ToString(),
                                        HardwareId = Get(hardware, "Identifier").ToString(), Name = "Unavailable", Kind = "Status" });
                                }
                            }
                        }
                        lock (deviceGate)
                        {
                            if (batteryDue) { batterySensors = batteries; batteryUpdated = DateTime.UtcNow; }
                            if (storageDue) { storageSensors = drives; storageUpdated = DateTime.UtcNow; }
                        }
                    }
                    catch (Exception ex) { Log.Write(ex); }
                    if (batteryDue) nextBattery = DateTime.UtcNow.AddSeconds(2);
                    if (storageDue) nextStorage = DateTime.UtcNow.AddSeconds(10);
                }
                batteryReport.RefreshIfDue();
                if (stopDevices.WaitOne(250)) return;
            }
        }
        private void MergeDevices(List<SensorReading> data)
        {
            lock (deviceGate)
            {
                AppendCached(data, batterySensors, DateTime.UtcNow - batteryUpdated < TimeSpan.FromSeconds(10));
                AppendCached(data, storageSensors, DateTime.UtcNow - storageUpdated < TimeSpan.FromSeconds(30));
            }
        }
        private static void AppendCached(List<SensorReading> data, List<SensorReading> cached, bool fresh)
        {
            foreach (var sensor in cached)
                data.Add(fresh ? sensor : new SensorReading { Hardware = sensor.Hardware, HardwareName = sensor.HardwareName,
                    HardwareId = sensor.HardwareId, Id = sensor.Id, Kind = sensor.Kind, Name = sensor.Name, Value = null });
        }
        private static void ReadHardware(object h, List<SensorReading> data)
        {
            Call(h, "Update");
            foreach (object v in (IEnumerable)Get(h, "Sensors"))
            {
                object value = Get(v, "Value");
                data.Add(new SensorReading { Hardware = Get(h, "HardwareType").ToString(), HardwareName = Get(h, "Name").ToString(),
                    HardwareId = Get(h, "Identifier").ToString(), Kind = Get(v, "SensorType").ToString(),
                    Name = Get(v, "Name").ToString(), Id = Get(v, "Identifier").ToString(), Value = value == null ? null : (double?)Convert.ToDouble(value) });
            }
            foreach (object sub in (IEnumerable)Get(h, "SubHardware")) ReadHardware(sub, data);
        }
        private static object Get(object o, string n) { return o.GetType().GetProperty(n).GetValue(o, null); }
        private static void Set(object o, string n, object v) { o.GetType().GetProperty(n).SetValue(o, v, null); }
        private static void Call(object o, string n) { o.GetType().GetMethod(n, Type.EmptyTypes).Invoke(o, null); }
        private static Exception Root(Exception e) { return e.InnerException == null ? e : Root(e.InnerException); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stopDevices.Set();
            object owner = Interlocked.Exchange(ref computer, null);
            var worker = deviceWorker;
            if (worker == null || worker.Join(2000)) { Close(owner); stopDevices.Dispose(); }
            else ThreadPool.QueueUserWorkItem(delegate { worker.Join(); Close(owner); stopDevices.Dispose(); });
        }
        private static void Close(object owner)
        { if (owner != null) try { Call(owner, "Close"); } catch (Exception ex) { Log.Write(ex); } }
    }
    // PawnIO device protocol; the signed ACPI module restricts port access to 0x62/0x66.
    internal interface IEcPort : IDisposable
    {
        byte Read(int port); void Write(int port, byte value);
    }
    public sealed class PawnPort : IEcPort
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint size, byte[] output, uint outSize, out uint returned, IntPtr overlapped);
        private SafeFileHandle handle;
        public PawnPort()
        {
            // Validate components before opening the driver or attempting a CLR bind.
            var assembly = HardwareComponents.LoadLibrary();
            handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 3, 3, IntPtr.Zero, 3, 128, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "请先配置硬件组件并切换到管理员模式"); }
            try
            {
                string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("LpcACPIEC.bin", StringComparison.OrdinalIgnoreCase));
                byte[] module;
                using (var s = assembly.GetManifestResourceStream(resource)) using (var m = new MemoryStream()) { s.CopyTo(m); module = m.ToArray(); }
                Io(0xA1B22084u, module, 0);
            }
            catch { handle.Dispose(); throw; }
        }
        private byte[] Io(uint code, byte[] input, int size)
        {
            var output = new byte[size]; uint count;
            if (!DeviceIoControl(handle, code, input, (uint)input.Length, output, (uint)size, out count, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (count != size) throw new IOException("硬件响应长度不匹配");
            return output;
        }
        private byte[] Execute(string function, long[] args, int length)
        {
            var buffer = new byte[32 + args.Length * 8];
            Encoding.ASCII.GetBytes(function).CopyTo(buffer, 0); Buffer.BlockCopy(args, 0, buffer, 32, args.Length * 8);
            return Io(0xA1B22104u, buffer, length);
        }
        public byte Read(int port) { return (byte)BitConverter.ToInt64(Execute("ioctl_pio_read", new long[] { port }, 8), 0); }
        public void Write(int port, byte value) { Execute("ioctl_pio_write", new long[] { port, value }, 0); }
        public void Dispose() { if (handle != null) handle.Dispose(); }
    }
    public interface IFanDevice : IDisposable
    {
        int ReadLevel(); int? ReadRpm(); void SetLevel(int level);
    }
    public sealed class FanTelemetry
    {
        public int Level;
        public int? Rpm;
    }
    public interface IFanTelemetryDevice
    {
        FanTelemetry ReadTelemetry();
    }
    public sealed class ThinkPadFan : IFanDevice, IFanTelemetryDevice
    {
        private readonly IEcPort port;
        private readonly Mutex mutex;
        private Stopwatch transaction;
        private bool compatibilityReadLogged;
        public ThinkPadFan()
        {
            if (!Machine.Supported) throw new InvalidOperationException("风扇控制适配 X1 Carbon Gen 8 · i5-10210U / i5-10310U");
            mutex = new Mutex(false, @"Global\Access_EC");
            try { port = new PawnPort(); } catch { mutex.Dispose(); throw; }
        }
        // Isolated fake-port path used by protocol tests; never opens a real EC.
        internal ThinkPadFan(IEcPort testPort)
        {
            if (testPort == null) throw new ArgumentNullException("testPort");
            port = testPort;
            mutex = new Mutex(false, "Local\\CarbonControl.EcTest-" + Guid.NewGuid().ToString("N"));
        }
        private T Locked<T>(Func<T> operation)
        {
            for (int attempt = 0; ; attempt++)
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(150); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new TimeoutException("风扇接口忙碌");
                    transaction = Stopwatch.StartNew();
                    return operation();
                }
                catch (TimeoutException) { if (attempt >= 1) throw; }
                finally { if (acquired) mutex.ReleaseMutex(); }
                // Release between bounded retries so Windows/other monitoring tools can finish.
                Thread.Sleep(20);
            }
        }
        private void Wait(int mask, bool set)
        {
            var timer = Stopwatch.StartNew();
            while (((port.Read(0x66) & mask) != 0) != set)
            { if (timer.ElapsedMilliseconds >= 100 || transaction.ElapsedMilliseconds >= 450) throw new TimeoutException("风扇接口响应超时"); Thread.Sleep(1); }
        }
        private byte ReadRegister(byte address)
        {
            // Do not consume a response that may belong to the Windows ACPI driver.
            Wait(3, false); port.Write(0x66, 0x80);
            Wait(2, false); port.Write(0x62, address);
            Wait(2, false);
            var outputWait = Stopwatch.StartNew();
            while ((port.Read(0x66) & 1) == 0 && outputWait.ElapsedMilliseconds < 20)
            { if (transaction.ElapsedMilliseconds >= 450) throw new TimeoutException("风扇接口响应超时"); Thread.Sleep(1); }
            // Some ECs do not expose a useful OBF transition. TPFanCtrl2 waits for
            // IBF clear before reading; LHM also has an IBF-clear fallback.
            // The command/address handshake must still complete, and SetLevel
            // must verify the actual returned register byte against its target.
            Wait(2, false);
            if ((port.Read(0x66) & 1) == 0 && !compatibilityReadLogged)
            { compatibilityReadLogged = true; Log.Write("EC: using IBF-clear read compatibility (OBF not asserted)"); }
            return port.Read(0x62);
        }
        private void WriteMode(byte value)
        {
            Wait(3, false); port.Write(0x66, 0x81);
            Wait(2, false); port.Write(0x62, 0x2F);
            Wait(2, false); port.Write(0x62, value); Wait(2, false);
        }
        public int ReadLevel() { return Locked(() => (int)ReadRegister(0x2F)); }
        public int? ReadRpm()
        {
            return Locked<int?>(() => { int low = ReadRegister(0x84); int rpm = low | (ReadRegister(0x85) << 8); return rpm == 65535 ? (int?)null : rpm; });
        }
        public FanTelemetry ReadTelemetry()
        {
            return Locked(() =>
            {
                int level = ReadRegister(0x2F);
                int low = ReadRegister(0x84); int rpm = low | (ReadRegister(0x85) << 8);
                return new FanTelemetry { Level = level, Rpm = rpm == 65535 ? (int?)null : rpm };
            });
        }
        public void SetLevel(int level)
        {
            if ((level < 0 || level > 7) && level != 128) throw new ArgumentOutOfRangeException("level");
            Locked(() =>
            {
                WriteMode((byte)level); int result = ReadRegister(0x2F);
                bool ok = level == 128 ? (result & 128) != 0 : (result & 0xC7) == level;
                if (!ok) throw new IOException("风扇档位未确认，请重试");
                return 0;
            });
        }
        public void Dispose() { port.Dispose(); mutex.Dispose(); }
    }
}
