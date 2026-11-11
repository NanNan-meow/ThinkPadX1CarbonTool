using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Serialization;

namespace CarbonControl
{
    public enum PerformanceMode { System, LowTemperature, Balanced, Performance }

    public sealed class PowerPlanValues
    {
        public uint AcBoost, DcBoost, AcFrequency, DcFrequency;
        public PowerPlanValues Clone() { return (PowerPlanValues)MemberwiseClone(); }
        public bool Same(PowerPlanValues other)
        { return other != null && AcBoost == other.AcBoost && DcBoost == other.DcBoost && AcFrequency == other.AcFrequency && DcFrequency == other.DcFrequency; }
    }
    public interface IPowerPlanBackend
    {
        Guid GetActiveScheme();
        PowerPlanValues Read(Guid scheme);
        Guid Duplicate(Guid source);
        void Write(Guid scheme, PowerPlanValues values);
        void Activate(Guid scheme);
        void Rename(Guid scheme, string name);
        void Delete(Guid scheme);
    }

    public sealed class PerformanceRecoveryJournal
    {
        public string OriginalScheme { get; set; }
        public string[] OwnedSchemes { get; set; }
    }

    public sealed class PerformanceController : IDisposable
    {
        private readonly IPowerPlanBackend backend;
        private readonly string journalPath;
        private PerformanceRecoveryJournal session;
        private bool disposed;
        public PerformanceMode Mode { get; private set; }
        public string Status { get; private set; }
        public PerformanceController() : this(new WindowsPowerPlanBackend(), Path.Combine(Log.DirectoryPath, "performance-recovery.xml")) { }
        public PerformanceController(IPowerPlanBackend powerBackend, string recoveryPath)
        {
            if (powerBackend == null) throw new ArgumentNullException("powerBackend");
            if (string.IsNullOrWhiteSpace(recoveryPath)) throw new ArgumentException("Missing recovery path", "recoveryPath");
            backend = powerBackend; journalPath = Path.GetFullPath(recoveryPath);
            Mode = PerformanceMode.System; Status = "使用系统电源设置";
        }
        public static PowerPlanValues ValuesFor(PerformanceMode mode)
        {
            if (mode == PerformanceMode.System || !Enum.IsDefined(typeof(PerformanceMode), mode)) throw new ArgumentOutOfRangeException("mode");
            uint boost = mode == PerformanceMode.LowTemperature ? 0u : mode == PerformanceMode.Performance ? 2u : 1u;
            return new PowerPlanValues { AcBoost = boost, DcBoost = boost, AcFrequency = 0, DcFrequency = 0 };
        }
        public void RefreshActiveMode()
        {
            if (disposed) throw new ObjectDisposedException("PerformanceController");
            if (session != null && Mode != PerformanceMode.System && !Owned(session).Contains(backend.GetActiveScheme()))
            { Mode = PerformanceMode.System; Status = "使用系统电源设置"; }
        }
        public void Apply(PerformanceMode mode)
        {
            if (disposed) throw new ObjectDisposedException("PerformanceController");
            if (!Enum.IsDefined(typeof(PerformanceMode), mode)) throw new ArgumentOutOfRangeException("mode");
            if (mode == PerformanceMode.System) { Restore(); return; }
            if (session == null && File.Exists(journalPath)) RecoverPreviousSession();
            Guid previousActive = backend.GetActiveScheme();
            if (session != null && !Owned(session).Contains(previousActive))
            {
                // The user selected another plan outside this app. Start from that choice.
                CleanupInactive(session, previousActive); SaveOrClearSession();
                if (session != null) throw new IOException("旧电源方案尚未清理，请先恢复系统模式。");
                Mode = PerformanceMode.System;
            }
            PowerPlanValues desired = ValuesFor(mode);
            if (session != null && Mode == mode && backend.Read(previousActive).Same(desired))
            { Status = Label(mode) + " · 电源设置已确认"; return; }
            Guid original = session == null ? previousActive : Original(session);
            // Check support before creating or activating an application-owned plan.
            backend.Read(original);
            Guid created = backend.Duplicate(original);
            PerformanceRecoveryJournal prior = Copy(session);
            PerformanceMode priorMode = Mode;
            var pending = new PerformanceRecoveryJournal { OriginalScheme = original.ToString("D"),
                OwnedSchemes = Owned(session).Concat(new[] { created }).Select(x => x.ToString("D")).ToArray() };
            bool recorded = false;
            try
            {
                WriteJournal(pending); recorded = true;
                backend.Rename(created, "Carbon Control · " + Label(mode));
                backend.Write(created, desired);
                if (!backend.Read(created).Same(desired)) throw new IOException("处理器电源设置未确认。");
                // Do not replace a new external selection made while the clone was configured.
                if (backend.GetActiveScheme() != previousActive) throw new IOException("系统电源方案已变化，请重新选择模式。");
                backend.Activate(created);
                if (backend.GetActiveScheme() != created) throw new IOException("系统未切换到所选性能模式。");
                if (!backend.Read(created).Same(desired)) throw new IOException("处理器电源设置在切换后发生变化。");
                session = pending; Mode = mode;
            }
            catch (Exception applyError)
            {
                try
                {
                    if (backend.GetActiveScheme() == created)
                    {
                        backend.Activate(previousActive);
                        if (backend.GetActiveScheme() != previousActive) throw new IOException("原电源方案未恢复。");
                    }
                    backend.Delete(created);
                    session = prior; Mode = priorMode;
                    if (recorded) SaveOrClearSession();
                }
                catch (Exception restoreError)
                {
                    session = pending;
                    Status = "电源方案恢复待完成";
                    throw new AggregateException("性能模式未应用，恢复信息已保留。", applyError, restoreError);
                }
                Status = "性能模式未应用：" + applyError.Message;
                throw;
            }
            CleanupInactive(session, created);
            // The pre-activation journal already includes the confirmed active clone. If its
            // cleanup update fails, keep the confirmed mode and recover from that prior record.
            try { SaveOrClearSession(); } catch (Exception ex) { Log.Write(ex); }
            Status = Label(mode) + " · 电源设置已确认";
        }
        public void RecoverPreviousSession()
        {
            if (disposed) throw new ObjectDisposedException("PerformanceController");
            if (session == null && File.Exists(journalPath))
            {
                using (var file = File.OpenRead(journalPath))
                    session = (PerformanceRecoveryJournal)new XmlSerializer(typeof(PerformanceRecoveryJournal)).Deserialize(file);
                Validate(session);
            }
            if (session == null) return;
            Restore();
            Status = "已恢复上次运行前的电源设置";
        }
        private void Restore()
        {
            if (session == null && File.Exists(journalPath))
            {
                using (var file = File.OpenRead(journalPath)) session = (PerformanceRecoveryJournal)new XmlSerializer(typeof(PerformanceRecoveryJournal)).Deserialize(file);
                Validate(session);
            }
            if (session == null) { Mode = PerformanceMode.System; Status = "使用系统电源设置"; return; }
            Guid active = backend.GetActiveScheme();
            if (Owned(session).Contains(active))
            {
                backend.Activate(Original(session));
                active = backend.GetActiveScheme();
                if (active != Original(session)) throw new IOException("原电源方案未恢复，恢复信息已保留。");
            }
            CleanupInactive(session, active);
            Mode = PerformanceMode.System;
            try { SaveOrClearSession(); } catch (Exception ex) { Log.Write(ex); }
            Status = session == null ? "已恢复系统电源设置" : "系统电源已恢复，临时方案等待清理";
        }
        private void CleanupInactive(PerformanceRecoveryJournal journal, Guid active)
        {
            var remaining = new List<Guid>();
            foreach (Guid scheme in Owned(journal))
            {
                if (scheme == active) { remaining.Add(scheme); continue; }
                try { backend.Delete(scheme); } catch (Exception ex) { remaining.Add(scheme); Log.Write(ex); }
            }
            journal.OwnedSchemes = remaining.Select(x => x.ToString("D")).ToArray();
        }
        private void SaveOrClearSession()
        {
            if (session == null || Owned(session).Length == 0)
            {
                if (File.Exists(journalPath)) File.Delete(journalPath);
                session = null;
            }
            else WriteJournal(session);
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string destination, uint flags);
        private void WriteJournal(PerformanceRecoveryJournal value)
        {
            Validate(value);
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath));
            string temp = journalPath + ".tmp";
            using (var file = File.Create(temp)) new XmlSerializer(typeof(PerformanceRecoveryJournal)).Serialize(file, value);
            if (!MoveFileEx(temp, journalPath, 1 | 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        private static Guid Original(PerformanceRecoveryJournal journal) { return Guid.Parse(journal.OriginalScheme); }
        private static Guid[] Owned(PerformanceRecoveryJournal journal)
        { return journal == null || journal.OwnedSchemes == null ? new Guid[0] : journal.OwnedSchemes.Select(Guid.Parse).Distinct().ToArray(); }
        private static PerformanceRecoveryJournal Copy(PerformanceRecoveryJournal journal)
        { return journal == null ? null : new PerformanceRecoveryJournal { OriginalScheme = journal.OriginalScheme, OwnedSchemes = journal.OwnedSchemes.ToArray() }; }
        private static void Validate(PerformanceRecoveryJournal journal)
        {
            if (journal == null || Original(journal) == Guid.Empty || Owned(journal).Any(x => x == Guid.Empty || x == Original(journal)))
                throw new InvalidDataException("电源恢复记录无效。");
        }
        private static string Label(PerformanceMode mode)
        { return mode == PerformanceMode.LowTemperature ? "低温模式" : mode == PerformanceMode.Performance ? "性能模式" : "均衡模式"; }
        public static string Probe()
        {
            var native = new WindowsPowerPlanBackend(); Guid active = native.GetActiveScheme(); PowerPlanValues values = native.Read(active);
            return "Active scheme: " + active + Environment.NewLine + "Turbo AC/DC: " + values.AcBoost + "/" + values.DcBoost + Environment.NewLine +
                "Maximum frequency AC/DC (MHz, 0=system): " + values.AcFrequency + "/" + values.DcFrequency;
        }
        public void Dispose()
        {
            if (disposed) return;
            try { Restore(); } catch (Exception ex) { Status = "电源方案恢复待完成"; Log.Write(ex); }
            disposed = true;
        }
    }

    // Windows power-policy APIs; GUIDs are from Microsoft's WinNT.h.
    // https://learn.microsoft.com/windows/win32/api/powrprof/nf-powrprof-powerduplicatescheme
    public sealed class WindowsPowerPlanBackend : IPowerPlanBackend
    {
        private static readonly Guid Processor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        private static readonly Guid Boost = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
        private static readonly Guid Frequency = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("powrprof.dll")] private static extern uint PowerDuplicateScheme(IntPtr root, ref Guid source, out IntPtr target);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerDeleteScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] name, uint length);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        private static void Check(uint error, string operation)
        { if (error != 0) throw new Win32Exception((int)error, "电源设置" + operation + "失败：" + new Win32Exception((int)error).Message); }
        public Guid GetActiveScheme()
        {
            IntPtr memory = IntPtr.Zero;
            try { Check(PowerGetActiveScheme(IntPtr.Zero, out memory), "读取"); return (Guid)Marshal.PtrToStructure(memory, typeof(Guid)); }
            finally { if (memory != IntPtr.Zero) LocalFree(memory); }
        }
        public Guid Duplicate(Guid source)
        {
            IntPtr memory = IntPtr.Zero;
            try { Check(PowerDuplicateScheme(IntPtr.Zero, ref source, out memory), "创建"); return (Guid)Marshal.PtrToStructure(memory, typeof(Guid)); }
            finally { if (memory != IntPtr.Zero) LocalFree(memory); }
        }
        public PowerPlanValues Read(Guid scheme)
        {
            Guid group = Processor, boost = Boost, frequency = Frequency; var value = new PowerPlanValues();
            Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref group, ref boost, out value.AcBoost), "读取睿频");
            Check(PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref group, ref boost, out value.DcBoost), "读取电池睿频");
            Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref group, ref frequency, out value.AcFrequency), "读取最高频率");
            Check(PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref group, ref frequency, out value.DcFrequency), "读取电池最高频率");
            return value;
        }
        public void Write(Guid scheme, PowerPlanValues values)
        {
            Guid group = Processor, boost = Boost, frequency = Frequency;
            Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref group, ref boost, values.AcBoost), "写入睿频");
            Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref group, ref boost, values.DcBoost), "写入电池睿频");
            Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref group, ref frequency, values.AcFrequency), "写入最高频率");
            Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref group, ref frequency, values.DcFrequency), "写入电池最高频率");
        }
        public void Activate(Guid scheme) { Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme), "应用"); }
        public void Rename(Guid scheme, string name)
        { byte[] bytes = Encoding.Unicode.GetBytes(name + "\0"); Check(PowerWriteFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, bytes, (uint)bytes.Length), "命名"); }
        public void Delete(Guid scheme)
        {
            uint error = PowerDeleteScheme(IntPtr.Zero, ref scheme);
            if (error != 2) Check(error, "清理"); // An already-removed owned plan needs no further cleanup.
        }
    }
}
