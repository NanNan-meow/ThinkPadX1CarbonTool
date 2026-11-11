using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CarbonControl
{
    public sealed class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
            {
                string file = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hardware", new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            if (args.Length == 3 && args[0] == "--watchdog") { Watchdog.Run(int.Parse(args[1]), args[2]); return 0; }
            if (args.Length == 4 && args[0] == "--power-watchdog") { PhysicalVerification.WatchPower(int.Parse(args[1]), args[2], args[3]); return 0; }
            if (args.Length > 0 && args[0] == "--self-test") return Tests.Run(args.Length > 1 ? args[1] : "test-results.txt");
            if (args.Length == 2 && args[0] == "--diagnose") return Diagnostics.Write(args[1]);
            if (args.Length == 2 && args[0] == "--physical-test") return PhysicalVerification.Run(args[1]);
            if (args.Length == 2 && args[0] == "--check-components") return Diagnostics.CheckComponents(args[1]);
            if (args.Length == 2 && args[0] == "--ui-test")
            {
                try { var testApp = new Application(); using (var testUi = new MainUi(true)) testUi.TestUi(args[1]); return 0; }
                catch (Exception ex) { File.WriteAllText(args[1], ex.ToString(), Encoding.UTF8); return 1; }
            }
            int parentArgument = Array.IndexOf(args, "--wait-parent");
            if (parentArgument >= 0 && parentArgument + 1 < args.Length)
            { try { using (var parent = Process.GetProcessById(int.Parse(args[parentArgument + 1]))) parent.WaitForExit(15000); } catch (ArgumentException) { } }
            if (args.Length > 1 && args[0] == "--render-preview")
            {
                var previewApp = new Application(); var preview = new MainUi(true); preview.RenderPreviews(args[1]); preview.Dispose(); return 0;
            }
            bool first;
            using (var mutex = new Mutex(true, "Local\\CarbonControl.Main", out first))
            {
                if (!first) { try { using (var show = EventWaitHandle.OpenExisting("Local\\CarbonControl.Show")) show.Set(); } catch { } return 0; }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                MainUi ui = null;
                try
                {
                    ui = new MainUi(false);
                    app.DispatcherUnhandledException += (s, e) => { Log.Write(e.Exception); if (ui != null) ui.Dispose(); app.Shutdown(1); e.Handled = true; };
                    ui.Run(args.Contains("--tray"));
                    if (args.Contains("--settings")) ui.OpenSettings();
                    app.Run(); return 0;
                }
                catch (Exception ex) { Log.Write(ex); MessageBox.Show(ex.Message, "Carbon Control"); return 1; }
                finally { if (ui != null) ui.Dispose(); }
            }
        }
    }
    public sealed partial class MainUi : IDisposable
    {
        private readonly Window window;
        private readonly WindowFrame windowFrame;
        private readonly bool preview;
        private readonly string settingsPath;
        private Settings settings;
        private Engine engine;
        private Forms.NotifyIcon tray;
        private Forms.ToolStripMenuItem fullSpeedTrayItem;
        private DispatcherTimer timer;
        private EventWaitHandle showEvent;
        private bool exiting, updating, disposed;
        private string activePage = "Monitor", appliedTheme;
        private int tickCount;
        private bool isFullSpeed, changingSlot, changingStartupPolicy;
        private StartupPolicyGate startupPolicyGate = new StartupPolicyGate();
        private FanMode selectedMode = FanMode.Bios, previousFullSpeedMode = FanMode.Bios;
        private int previousFullSpeedLevel = 4, appliedManualLevel = 4;
        private readonly List<double?> history = new List<double?>();
        private readonly CurveDraft curveDraft = new CurveDraft();
        private CurveChart curveChart;
        private static readonly string[] ModeLabels = { "系统自动", "安静", "均衡", "强冷", "自定义", "手动" };
        public MainUi(bool isPreview)
        {
            preview = isPreview;
            settingsPath = System.IO.Path.Combine(Log.DirectoryPath, "settings.xml");
            try { settings = preview ? new Settings() : Settings.Read(settingsPath); }
            catch (Exception ex) { Log.Write(ex); settings = new Settings(); }
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) window = (Window)XamlReader.Load(s);
            windowFrame = new WindowFrame(window);
            window.Icon = MakeWindowIcon();
            MakeCurveEditor(); BindButtons(); LoadCurve(settings.Curve); RefreshCurveSlots(); ApplyTheme(); Navigate("Monitor");
            InitializeDeviceUi(); InitializeModeDetails(); InitializeWindowCommands();
            Highlight("ModeBios", true);
            UpdateFullSpeedButtons();
            C<Slider>("ManualSlider").Value = settings.ManualLevel;
            appliedManualLevel = settings.ManualLevel;
            C<Slider>("ManualSlider").ValueChanged += (s, e) => { T("ManualLabel", ((int)e.NewValue) + " 档"); };
            C<CheckBox>("TrayCheck").IsChecked = settings.CloseToTray;
            C<CheckBox>("MinimizedCheck").IsChecked = settings.StartMinimized;
            C<CheckBox>("StartupCheck").IsChecked = !preview && StartupTask.IsEnabled();
            C<CheckBox>("TrayCheck").Click += (s, e) => SavePreference(() => settings.CloseToTray = C<CheckBox>("TrayCheck").IsChecked == true, "关闭窗口后的托盘设置已保存");
            C<CheckBox>("MinimizedCheck").Click += (s, e) => SavePreference(() => settings.StartMinimized = C<CheckBox>("MinimizedCheck").IsChecked == true, "启动时进入托盘的设置已保存");
            C<CheckBox>("StartupCheck").Click += async (s, e) =>
            {
                if (updating || preview) return;
                bool enabled = C<CheckBox>("StartupCheck").IsChecked == true;
                long feedback = BeginFeedback("正在设置开机启动…"); C<CheckBox>("StartupCheck").IsEnabled = false;
                try
                {
                    await Task.Run(() => StartupTask.Set(enabled)); bool actual = StartupTask.IsEnabled();
                    string message = actual == enabled ? enabled ? "已设置开机启动" : "已关闭开机启动" : "开机启动设置未确认";
                    T("SettingsMessage", message); FinishFeedback(feedback, actual == enabled, message);
                }
                catch (Exception ex) { Log.Write(ex); T("SettingsMessage", "开机启动未设置 · 需要管理员权限"); FinishFeedback(feedback, false, "开机启动未设置 · 需要管理员权限"); }
                finally { updating = true; C<CheckBox>("StartupCheck").IsChecked = StartupTask.IsEnabled(); updating = false; C<CheckBox>("StartupCheck").IsEnabled = true; }
            };
            C<Canvas>("HistoryChart").SizeChanged += (s, e) => DrawHistory();
            window.Closing += (s, e) => { if (!exiting && settings.CloseToTray && !preview) { e.Cancel = true; window.Hide(); } else if (!preview) Exit(); };
            window.StateChanged += (s, e) => { if (window.WindowState == WindowState.Minimized && !preview) window.Hide(); };
            T("MachineLabel", preview ? "ThinkPad X1 Carbon Gen 8 · Intel Core i5-10210U" : Machine.Model + " · " + Machine.Cpu);
            if (!preview) T("ModelCaption", Machine.Supported ? "Gen 8 · " + (Machine.Cpu.Contains("10310U") ? "i5-10310U" : "i5-10210U") : "目标 · X1 Carbon Gen 8");
            if (!preview) { SystemEvents.PowerModeChanged += OnPower; Application.Current.SessionEnding += OnSessionEnding; }
        }
        private TControl C<TControl>(string name) where TControl : class { return window.FindName(name) as TControl; }
        private void T(string name, string text) { C<TextBlock>(name).Text = text; }
        private void Click(string name, Action action) { C<Button>(name).Click += (s, e) => { try { action(); } catch (Exception ex) { Log.Write(ex); T("SettingsMessage", ex.Message); T("CurveMessage", ex.Message); NotifyFeedback(false, "操作未完成 · " + ex.Message); } }; }
        private void BindButtons()
        {
            foreach (string p in new[] { "Monitor", "Performance", "Fan", "Battery", "Storage", "Settings" }) { string page = p; Click("Nav" + p, () => Navigate(page)); }
            foreach (FanMode m in new[] { FanMode.Bios, FanMode.Quiet, FanMode.Balanced, FanMode.Cool, FanMode.Custom })
            { FanMode captured = m; Click("Mode" + m, () => SelectMode(captured)); }
            Click("RestoreBios", () => SelectMode(FanMode.Bios));
            Click("ApplyManual", () => SelectMode(FanMode.Manual));
            Click("IncreaseFan", () =>
            {
                int level = engine != null && engine.FanLevel.HasValue ? engine.FanLevel.Value & 7 : (int)C<Slider>("ManualSlider").Value;
                C<Slider>("ManualSlider").Value = Math.Min(7, level + 1); SelectMode(FanMode.Manual);
            });
            Click("BoostFan", () => SetFullSpeed());
            Click("QuickFullSpeed", ToggleFullSpeed);
            Click("SaveCurve", ApplyDraft);
            Click("ResetCurve", () => { LoadCurve(FanPolicy.Preset(FanMode.Balanced)); T("CurveMessage", "已载入均衡曲线"); });
            Click("UndoCurve", () => curveDraft.Undo()); Click("RedoCurve", () => curveDraft.Redo());
            Click("DeleteCurvePoint", () => curveDraft.DeleteSelected());
            Click("AddCurvePoint", AddCurvePoint);
            Click("UpdateCurvePoint", () => CommitCurvePoint());
            Click("SaveCurveSlot", SaveCurveSlot); Click("LoadCurveSlot", LoadCurveSlot);
            Click("ApplyCurveSlot", () => { if (LoadCurveSlotCore()) ApplyDraft(); });
            C<ComboBox>("CurveSlotPicker").SelectionChanged += (s, e) =>
            {
                if (changingSlot || C<ComboBox>("CurveSlotPicker").SelectedIndex < 0) return;
                SavePreference(() => { settings.SelectedCurveSlot = C<ComboBox>("CurveSlotPicker").SelectedIndex; UpdateSelectedSlot(); }, "曲线存档已选中 · 载入后可编辑");
            };
            C<ComboBox>("StartupPolicyPicker").SelectionChanged += (s, e) =>
            {
                if (changingStartupPolicy || C<ComboBox>("StartupPolicyPicker").SelectedIndex < 0) return;
                SavePreference(() => settings.StartupPolicy = (StartupFanPolicy)C<ComboBox>("StartupPolicyPicker").SelectedIndex, "启动风扇策略已保存");
            };
            foreach (string theme in new[] { "System", "Light", "Dark" })
            { string chosen = theme; Click("Theme" + theme, () => { settings.Theme = chosen; ApplyTheme(); Save(); NotifyFeedback(true, chosen == "Light" ? "已切换浅色模式" : chosen == "Dark" ? "已切换深色模式" : "已设为跟随系统主题"); }); }
            Click("Elevate", () =>
            {
                if (IsAdmin()) { T("SettingsMessage", "管理员模式已启用"); NotifyFeedback(true, "管理员模式已启用"); return; }
                BeginFeedback("正在切换管理员模式…");
                RestartApplication(true);
            });
            Click("Reconnect", () => { T("SettingsMessage", "正在重启…"); BeginFeedback("正在重新连接硬件并重启…"); RestartApplication(false); });
            C<Button>("InstallHardware").Click += async (s, e) =>
            {
                if (preview) return;
                C<Button>("InstallHardware").IsEnabled = false; T("SettingsMessage", "正在配置硬件组件…");
                long feedback = BeginFeedback("正在配置硬件组件…");
                try
                {
                    string script = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setup-hardware.ps1");
                    var start = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                    using (var process = Process.Start(start))
                    {
                        await Task.Run(() => process.WaitForExit());
                        if (process.ExitCode == 0)
                        {
                            T("SettingsMessage", "组件已安装 · 正在重启");
                            FinishFeedback(feedback, true, "硬件组件已安装 · 正在重启");
                            RestartApplication(!IsAdmin());
                        }
                        else { T("SettingsMessage", "组件配置未完成 · setup-hardware.log"); FinishFeedback(feedback, false, "组件配置未完成 · 查看 setup-hardware.log"); }
                    }
                }
                catch (Exception ex) { T("SettingsMessage", ex.Message); Log.Write(ex); FinishFeedback(feedback, false, "组件配置未完成 · " + ex.Message); }
                finally { C<Button>("InstallHardware").IsEnabled = true; }
            };
            Click("ExportReport", ExportReport);
        }
        private static string ExePath { get { return Process.GetCurrentProcess().MainModule.FileName; } }
        private void RestartApplication(bool elevate)
        {
            if (preview) { T("SettingsMessage", "正在重启…"); return; }
            string arguments = "--wait-parent " + Process.GetCurrentProcess().Id + " --settings";
            var start = new ProcessStartInfo(ExePath, arguments) { UseShellExecute = true };
            if (elevate) start.Verb = "runas";
            var child = Process.Start(start);
            if (child == null) throw new IOException("应用重启失败");
            child.Dispose(); Exit();
        }
        private static bool IsAdmin() { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        private void OnPower(object sender, PowerModeChangedEventArgs e) { if (engine != null && e.Mode != PowerModes.StatusChange) engine.Suspend(e.Mode == PowerModes.Suspend); }
        private void OnSessionEnding(object sender, System.Windows.SessionEndingCancelEventArgs e) { exiting = true; Dispose(); }
        private void Save() { if (!preview) settings.Save(settingsPath); }
        public void Run(bool minimized)
        {
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CarbonControl.Show");
            tray = new Forms.NotifyIcon { Text = "Carbon Control", Icon = TrayIcon(), Visible = true };
            tray.DoubleClick += (s, e) => Show();
            tray.ContextMenuStrip = CreateTrayMenu();
            // Keep an already enabled logon task pointed at this installed version.
            if (IsAdmin() && StartupTask.IsEnabled())
            { try { StartupTask.Set(true); } catch (Exception ex) { Log.Write(ex); } }
            RestartEngine();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (s, e) => Tick(); timer.Start();
            if (!minimized && !settings.StartMinimized) window.Show();
        }
        private Forms.ContextMenuStrip CreateTrayMenu()
        {
            var menu = new Forms.ContextMenuStrip(); menu.Items.Add("打开 Carbon Control", null, (s, e) => Show()); menu.Items.Add(new Forms.ToolStripSeparator());
            foreach (FanMode m in Enum.GetValues(typeof(FanMode)))
            { FanMode captured = m; menu.Items.Add(ModeLabels[(int)m], null, (s, e) => window.Dispatcher.Invoke(() => SelectMode(captured))); }
            fullSpeedTrayItem = new Forms.ToolStripMenuItem("全速 · 7 档", null, (s, e) => window.Dispatcher.Invoke(ToggleFullSpeed));
            menu.Items.Add(fullSpeedTrayItem);
            menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出", null, (s, e) => window.Dispatcher.Invoke(Exit));
            UpdateFullSpeedButtons(); return menu;
        }
        private void RestartEngine()
        {
            if (preview) return;
            if (engine != null) { engine.Dispose(); if (!engine.Stopped) throw new IOException("正在结束上一轮连接"); }
            engine = new Engine(); engine.Configure(FanMode.Bios, settings.ManualLevel, settings.Curve); engine.Start(); SelectMode(FanMode.Bios, false, false, true);
        }
        private void Show() { window.Dispatcher.Invoke(() => { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }); }
        public void OpenSettings() { Navigate("Settings"); Show(); }
        private bool SelectMode(FanMode mode, bool refreshDraft = true, bool fullSpeedRequest = false, bool startupRequest = false, bool performanceRequest = false)
        {
            settings.ManualLevel = (int)C<Slider>("ManualSlider").Value; Save();
            if (refreshDraft && mode != FanMode.Manual && mode != FanMode.Bios)
                LoadCurve(mode == FanMode.Custom ? (IList<CurvePoint>)settings.Curve : FanPolicy.Preset(mode));
            if (engine != null && !engine.FanReady && mode != FanMode.Bios)
            { T("CurveMessage", "未应用 · " + engine.FanStatus); T("SettingsMessage", engine.FanStatus); if (!startupRequest) NotifyFeedback(false, "风扇策略未应用 · " + engine.FanStatus); return false; }
            if (engine != null) engine.Configure(mode, settings.ManualLevel, settings.Curve);
            if (!startupRequest) startupPolicyGate.Cancel();
            if (!performanceRequest) { pendingPerformanceFan = null; performanceFeedbackVersion = 0; }
            selectedMode = mode; isFullSpeed = fullSpeedRequest; UpdateFullSpeedButtons();
            if (mode == FanMode.Manual) appliedManualLevel = settings.ManualLevel;
            UpdatePerformanceStatus();
            foreach (FanMode m in new[] { FanMode.Bios, FanMode.Quiet, FanMode.Balanced, FanMode.Cool, FanMode.Custom }) Highlight("Mode" + m, m == mode);
            Highlight("ApplyManual", mode == FanMode.Manual);
            T("PolicyDescription", fullSpeedRequest ? "固定 7 档 · 再次点击返回" : mode == FanMode.Bios ? "系统自动调速" : mode == FanMode.Manual ? "手动档位" : ModeLabels[(int)mode] + " · 按 CPU 温度调速");
            if (!startupRequest) TrackFanAction(mode, performanceRequest);
            return true;
        }
        private void ToggleFullSpeed()
        {
            if (!isFullSpeed) { SetFullSpeed(); return; }
            C<Slider>("ManualSlider").Value = previousFullSpeedLevel;
            SelectMode(previousFullSpeedMode, false);
        }
        private void SetFullSpeed()
        {
            if (isFullSpeed) return;
            var previousMode = selectedMode; int previousLevel = selectedMode == FanMode.Manual ? appliedManualLevel : (int)C<Slider>("ManualSlider").Value;
            C<Slider>("ManualSlider").Value = 7;
            if (SelectMode(FanMode.Manual, false, true))
            { previousFullSpeedMode = previousMode; previousFullSpeedLevel = previousLevel; UpdateFullSpeedButtons(); }
            else C<Slider>("ManualSlider").Value = previousLevel;
        }
        private void UpdateFullSpeedButtons()
        {
            string label = isFullSpeed ? "返回" + ModeLabels[(int)previousFullSpeedMode] : "全速 · 7 档";
            foreach (string name in new[] { "QuickFullSpeed" })
            { var button = C<Button>(name); button.Content = label; Highlight(name, isFullSpeed); }
            if (fullSpeedTrayItem != null) { fullSpeedTrayItem.Checked = isFullSpeed; fullSpeedTrayItem.Text = isFullSpeed ? "返回" + ModeLabels[(int)previousFullSpeedMode] : "全速 · 7 档"; }
        }
        private void ApplyDraft()
        {
            if (!CommitCurvePoint()) return;
            var points = curveDraft.Points; FanPolicy.Validate(points);
            settings.Curve = points.ToArray(); Save(); curveDraft.MarkSaved();
            bool applied = SelectMode(FanMode.Custom, false);
            T("CurveMessage", applied ? "曲线已保存" : "曲线已保存 · 尚未应用");
        }
        private void RefreshCurveSlots()
        {
            var picker = C<ComboBox>("CurveSlotPicker"); changingSlot = true;
            picker.Items.Clear();
            for (int i = 0; i < settings.CurveSlots.Length; i++)
            {
                var slot = settings.CurveSlots[i];
                picker.Items.Add(new ComboBoxItem { Content = (i + 1) + " · " + slot.Name + (slot.HasCurve ? "" : "（空）"),
                    Foreground = Brush("Text"), Background = Brush("Input") });
            }
            picker.SelectedIndex = settings.SelectedCurveSlot; changingSlot = false; UpdateSelectedSlot(); RefreshStartupPolicies();
        }
        private void RefreshStartupPolicies()
        {
            var picker = C<ComboBox>("StartupPolicyPicker"); changingStartupPolicy = true; picker.Items.Clear();
            foreach (StartupFanPolicy policy in Enum.GetValues(typeof(StartupFanPolicy)))
            {
                int index = (int)policy - (int)StartupFanPolicy.Slot1;
                bool enabled = index < 0 || settings.CurveSlots[index].HasCurve;
                picker.Items.Add(new ComboBoxItem { Content = StartupPolicyLabel(policy) + (enabled ? "" : "（空）"), IsEnabled = enabled,
                    Foreground = Brush("Text"), Background = Brush("Input") });
            }
            picker.SelectedIndex = (int)settings.StartupPolicy; changingStartupPolicy = false;
        }
        private string StartupPolicyLabel(StartupFanPolicy policy)
        {
            string[] labels = { "系统自动", "安静", "均衡", "强冷", "自定义（当前保存）" };
            int index = (int)policy;
            return index < labels.Length ? labels[index] : "存档 " + (index - labels.Length + 1) + " · " + settings.CurveSlots[index - labels.Length].Name;
        }
        private void TryApplyStartupPolicy(Snapshot sample, bool fanReady, bool fanConfirmed, DateTime now)
        {
            if (!startupPolicyGate.TryTake(fanReady, fanConfirmed, sample, now)) return;
            try
            {
                CurvePoint[] points; var mode = StartupPolicyResolver.Resolve(settings, out points);
                if (mode == FanMode.Custom) settings.Curve = points;
                if (SelectMode(mode, !curveDraft.IsDirty, false, true))
                    T("SettingsMessage", "已加载启动策略：" + StartupPolicyLabel(settings.StartupPolicy));
            }
            catch (Exception ex) { Log.Write(ex); T("SettingsMessage", "启动策略未应用：" + ex.Message); }
        }
        private void UpdateSelectedSlot()
        {
            var slot = settings.CurveSlots[settings.SelectedCurveSlot];
            C<TextBox>("CurveSlotName").Text = slot.Name;
            C<Button>("LoadCurveSlot").IsEnabled = slot.HasCurve; C<Button>("ApplyCurveSlot").IsEnabled = slot.HasCurve;
        }
        private void SaveCurveSlot()
        {
            if (!CommitCurvePoint()) return;
            int index = settings.SelectedCurveSlot;
            settings.SaveCurveSlot(index, C<TextBox>("CurveSlotName").Text, curveDraft.Points);
            Save(); curveDraft.MarkSaved(); RefreshCurveSlots();
            T("CurveMessage", "已保存到「" + settings.CurveSlots[index].Name + "」");
            NotifyFeedback(true, "曲线已保存到「" + settings.CurveSlots[index].Name + "」");
        }
        private bool LoadCurveSlotCore()
        {
            int index = settings.SelectedCurveSlot;
            LoadCurve(settings.LoadCurveSlot(index));
            T("CurveMessage", "已载入「" + settings.CurveSlots[index].Name + "」");
            NotifyFeedback(true, "曲线已载入编辑区，尚未应用");
            return true;
        }
        private void LoadCurveSlot() { LoadCurveSlotCore(); }
        private void Tick()
        {
            if (showEvent.WaitOne(0)) Show();
            var sample = engine.Current;
            bool fresh = (DateTime.UtcNow - sample.Timestamp).TotalSeconds <= 5;
            RefreshPerformanceMode();
            TryApplyStartupPolicy(sample, engine.FanReady, engine.FanConfirmed, DateTime.UtcNow);
            TryApplyPerformanceFan(sample, engine.FanReady, engine.FanConfirmed, DateTime.UtcNow);
            Present(fresh ? sample : new Snapshot { Status = "正在读取传感器" });
            PresentDevices(fresh ? sample : new Snapshot());
            curveChart.CurrentTemperature = fresh ? sample.CpuTemperature : null;
            history.Add(fresh ? sample.CpuTemperature : null); if (history.Count > 60) history.RemoveAt(0);
            DrawHistory();
            string rpm = engine.FanRpm.HasValue ? engine.FanRpm.Value.ToString("N0") : "—";
            T("FanSummary", "风扇  " + rpm + " RPM"); T("FanActual", rpm + " RPM"); T("FanSummaryMode", engine.FanStatus);
            T("FanControlStatus", engine.FanStatus);
            var confirmation = engine.ReadFanConfirmation();
            UpdateFanAction(confirmation.Revision, confirmation.Mode, confirmation.Level,
                confirmation.Ready && confirmation.Confirmed, confirmation.Status, DateTime.UtcNow);
            UpdatePerformanceStatus();
            T("ConnectionLabel", fresh ? sample.Status : "等待传感器数据"); T("HardwareStatus", sample.Status + "\n" + engine.FanStatus);
            T("TimeLabel", DateTime.Now.ToString("HH:mm:ss"));
            if (tray != null) tray.Text = "Carbon Control · " + Format(fresh ? sample.CpuTemperature : null, "0", "°C") + " · " + rpm + " RPM";
            if (++tickCount % 3 == 0) ApplyTheme();
        }
        private void Present(Snapshot s)
        {
            T("CpuTemp", Format(s.CpuTemperature, "0", "°C")); T("CpuClock", Format(s.CpuClock, "0.00", "GHz")); T("CpuPower", Format(s.CpuPower, "0.0", "W"));
            T("CpuLoad", Format(s.CpuLoad, "0", "%")); T("GpuLoad", Format(s.GpuLoad, "0", "%")); T("GpuPower", Format(s.GpuPower, "0.0", "W"));
            PresentCpuLimits(s);
        }
        private static string Format(double? v, string format, string unit) { return (SensorMap.Valid(v) ? v.Value.ToString(format, CultureInfo.InvariantCulture) : "—") + " " + unit; }
        private void Navigate(string page)
        {
            activePage = page;
            foreach (string p in new[] { "Monitor", "Performance", "Fan", "Battery", "Storage", "Settings" }) { C<StackPanel>(p + "Page").Visibility = p == page ? Visibility.Visible : Visibility.Collapsed; Highlight("Nav" + p, p == page); }
            T("PageTitle", page == "Monitor" ? "监控总览" : page == "Performance" ? "性能模式" : page == "Fan" ? "风扇策略" : page == "Battery" ? "电池" : page == "Storage" ? "SSD" : "设置");
            T("PageSubtitle", page == "Monitor" ? "CPU、GPU 与风扇实时监控。" : page == "Performance" ? "选择模式、查看设置与实际读回。" : page == "Fan" ? "预设、手动档位与自定义曲线。" : page == "Battery" ? "电量、容量与充放电功率。" : page == "Storage" ? "温度、寿命与累计读写。" : "外观、启动与硬件连接。");
            C<ScrollViewer>("PageScroll").ScrollToTop();
            RenderFeedback();
        }
        private void Highlight(string name, bool selected)
        { var b = C<Button>(name); b.SetResourceReference(Control.BackgroundProperty, selected ? "AccentSoft" : name.StartsWith("Nav") ? "Sidebar" : "Input"); b.SetResourceReference(Control.ForegroundProperty, selected ? "Accent" : "Text"); }
        private void ApplyTheme()
        {
            bool light = settings.Theme == "Light" || (settings.Theme == "System" && Machine.Value(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme") != "0");
            string key = light ? "Light" : "Dark";
            if (appliedTheme != key)
            {
                string[] names = { "Bg", "Sidebar", "Card", "Input", "Line", "Text", "Muted", "Accent", "AccentSoft", "Green" };
                string[] colors = light ? new[] { "#F5F6F8", "#ECEEF1", "#FFFFFF", "#F1F3F5", "#E1E4E8", "#20242A", "#69727D", "#C53749", "#FCE9ED", "#218260" } : new[] { "#101214", "#151719", "#1B1E21", "#25292D", "#303439", "#F1F3F4", "#9AA2AB", "#F16470", "#382128", "#70CCA7" };
                for (int i = 0; i < names.Length; i++) window.Resources[names[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
                appliedTheme = key; DrawHistory(); DrawCurve();
                DrawBatteryPower();
                RefreshCurveSlots();
            }
            foreach (string theme in new[] { "System", "Light", "Dark" }) Highlight("Theme" + theme, settings.Theme == theme);
            windowFrame.Apply(light);
        }
        private void MakeCurveEditor()
        {
            curveChart = new CurveChart(curveDraft);
            C<Grid>("CurveChartHost").Children.Add(curveChart);
            curveDraft.Changed += UpdateCurveEditor;
            foreach (string name in new[] { "SelectedTemperature", "SelectedLevel" })
            {
                C<TextBox>(name).KeyDown += (s, e) =>
                { if (e.Key == Key.Enter) { CommitCurvePoint(); e.Handled = true; } };
            }
        }
        private void LoadCurve(IList<CurvePoint> curve)
        { curveDraft.Load(curve); }
        private void UpdateCurveEditor()
        {
            var points = curveDraft.Points; int i = curveDraft.SelectedIndex;
            if (i >= 0 && i < points.Count)
            {
                T("SelectedNodeLabel", "节点 " + (i + 1));
                C<TextBox>("SelectedTemperature").Text = points[i].Temperature.ToString();
                C<TextBox>("SelectedLevel").Text = points[i].Level.ToString();
                C<TextBox>("SelectedLevel").IsReadOnly = i == points.Count - 1;
            }
            T("CurveDraftLabel", points.Count + " 个节点" + (curveDraft.IsDirty ? " · 已修改" : ""));
            C<Button>("UndoCurve").IsEnabled = curveDraft.CanUndo; C<Button>("RedoCurve").IsEnabled = curveDraft.CanRedo;
            C<Button>("AddCurvePoint").IsEnabled = points.Count < 12;
            C<Button>("DeleteCurvePoint").IsEnabled = points.Count > 2 && i >= 0 && i < points.Count - 1;
            T("CurveMessage", curveDraft.IsDirty ? "曲线已修改 · 保存并应用后生效" : "选中节点可输入数值调整");
        }
        private bool CommitCurvePoint()
        {
            int t, l;
            if (!int.TryParse(C<TextBox>("SelectedTemperature").Text, out t) || !int.TryParse(C<TextBox>("SelectedLevel").Text, out l))
            { T("CurveMessage", "温度和档位需为整数"); NotifyFeedback(false, "温度和档位需为整数"); return false; }
            curveDraft.MoveSelected(t, l); UpdateCurveEditor(); return true;
        }
        private void AddCurvePoint()
        {
            var p = curveDraft.Points; int start = 25, end = p[0].Temperature;
            for (int i = 1; i < p.Count; i++) if (p[i].Temperature - p[i - 1].Temperature > end - start)
            { start = p[i - 1].Temperature; end = p[i].Temperature; }
            if (90 - p[p.Count - 1].Temperature > end - start) { start = p[p.Count - 1].Temperature; end = 90; }
            int t = (start + end) / 2, level = p[0].Level;
            foreach (var point in p) { if (point.Temperature <= t) level = point.Level; else break; }
            curveDraft.Add(t, level);
        }
        private Brush Brush(string key) { return (Brush)window.Resources[key]; }
        private void Line(Canvas c, double x1, double y1, double x2, double y2, string brush, double width)
        { c.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = Brush(brush), StrokeThickness = width }); }
        private void DrawHistory()
        {
            var c = C<Canvas>("HistoryChart"); if (c == null || c.ActualWidth < 1) return; c.Children.Clear(); double w = c.ActualWidth - 28, h = c.ActualHeight - 10;
            foreach (int t in new[] { 40, 60, 80, 100 }) { double y = h - (t - 20) / 80.0 * h + 4; Line(c, 0, y, w, y, "Line", 1); var label = new TextBlock { Text = t.ToString(), FontSize = 9, Foreground = Brush("Muted") }; Canvas.SetLeft(label, w + 7); Canvas.SetTop(label, y - 6); c.Children.Add(label); }
            for (int i = 1; i < history.Count; i++) if (history[i - 1].HasValue && history[i].HasValue)
                Line(c, (60 - history.Count + i - 1) * w / 59, h - (Math.Max(20, Math.Min(100, history[i - 1].Value)) - 20) / 80 * h + 4,
                    (60 - history.Count + i) * w / 59, h - (Math.Max(20, Math.Min(100, history[i].Value)) - 20) / 80 * h + 4, "Accent", 2);
        }
        private void DrawCurve()
        { if (curveChart != null) curveChart.InvalidateVisual(); }
        private void ExportReport()
        {
            var dialog = new SaveFileDialog { FileName = "CarbonControl-检测报告.txt", Filter = "文本文件|*.txt" };
            if (dialog.ShowDialog(window) != true) return;
            var s = engine == null ? new Snapshot() : engine.Current;
            var b = new StringBuilder(); b.AppendLine("Carbon Control " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + " · " + DateTime.Now.ToString("s")); b.AppendLine("Model: " + Machine.Model); b.AppendLine("CPU: " + Machine.Cpu); b.AppendLine("Administrator: " + IsAdmin()); b.AppendLine(s.Status);
            if (engine != null) b.AppendLine("Fan: " + engine.FanStatus + "; RPM: " + engine.FanRpm + "; EC mode: " + engine.FanLevel);
            foreach (var v in s.Sensors) b.AppendLine(v.Id + " | " + v.Kind + " | " + v.Name + " | " + v.Value);
            AppendDeviceReport(b, s);
            AppendCpuLimits(b, s);
            File.WriteAllText(dialog.FileName, b.ToString(), Encoding.UTF8); T("SettingsMessage", "检测报告已导出");
            NotifyFeedback(true, "检测报告已导出");
        }
        private static System.Drawing.Icon TrayIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("carbon.ico")) return new System.Drawing.Icon(stream);
        }
        private static BitmapSource MakeWindowIcon()
        { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("carbon.ico")) { var d = new IconBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); return d.Frames[0]; } }
        public void RenderPreviews(string directory)
        {
            Directory.CreateDirectory(directory);
            // Render the content directly: an unshown Window has Hidden visibility.
            var root = (Grid)window.Content; window.Content = null; root.Resources = window.Resources;
            Present(new Snapshot { CpuTemperature = 54, CpuClock = 2.68, CpuPower = 12.4, CpuLoad = 28, GpuLoad = 16, GpuPower = 1.8,
                CpuLimits = new CpuLimitReading { LongPowerW = 15, ShortPowerW = 44, TimeWindowSeconds = 28, LongEnabled = true, ShortEnabled = true,
                    ThermalThrottling = false, PowerThrottling = false, Source = "封装 MSR" },
                CpuPolicy = new CpuPolicyReading { BoostMode = 2, MaximumFrequencyMhz = 0, OnAcPower = true, Timestamp = DateTime.UtcNow } });
            for (int i = 0; i < 60; i++) batteryPowerHistory.Add(-7.4 + Math.Sin(i * 0.24) * 1.6 + Math.Cos(i * 0.53) * 0.5);
            PresentDevices(DevicePreview());
            T("FanSummary", "风扇  3,280 RPM"); T("FanSummaryMode", "均衡 · 2 档"); T("FanActual", "3,280 RPM"); T("ConnectionLabel", "界面预览 · 示例数据"); T("HardwareStatus", "界面预览 · 示例数据");
            T("FanControlStatus", "已确认 · 2 档");
            T("LiveLabel", "●  界面预览");
            curveChart.CurrentTemperature = 54; curveDraft.Select(2);
            settings.SaveCurveSlot(0, "日常办公", curveDraft.Points); RefreshCurveSlots();
            for (int i = 0; i < 60; i++) history.Add(51 + Math.Sin(i * .22) * 5 + Math.Sin(i * .71) * 2);
            foreach (string theme in new[] { "Dark", "Light" })
            {
                settings.Theme = theme; ApplyTheme();
                foreach (string page in new[] { "Monitor", "Performance", "Fan", "Battery", "Storage", "Settings" })
                {
                    Navigate(page); root.Background = Brush("Bg"); root.Measure(new Size(1080, 790)); root.Arrange(new Rect(0, 0, 1080, 790)); root.UpdateLayout(); DrawHistory(); DrawCurve(); root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(1080, 790, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var f = File.Create(System.IO.Path.Combine(directory, page + "-" + theme + ".png"))) png.Save(f);
                    if (page == "Performance")
                    {
                        C<Expander>("ModeDetailsExpander").IsExpanded = true; root.Measure(new Size(1080, 790)); root.Arrange(new Rect(0, 0, 1080, 790)); root.UpdateLayout();
                        var detailed = new RenderTargetBitmap(1080, 790, 96, 96, PixelFormats.Pbgra32); detailed.Render(root);
                        var detailPng = new PngBitmapEncoder(); detailPng.Frames.Add(BitmapFrame.Create(detailed));
                        using (var f = File.Create(System.IO.Path.Combine(directory, "Performance-Details-" + theme + ".png"))) detailPng.Save(f);
                        C<Expander>("ModeDetailsExpander").IsExpanded = false;
                    }
                }
            }
        }
        public void TestUi(string output)
        {
            var root = (Grid)window.Content; window.Content = null; root.Resources = window.Resources;
            Action<string> press = name => C<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var startupSample = new Snapshot { CpuTemperature = 60, Timestamp = DateTime.UtcNow };
            if (settings.StartupPolicy != StartupFanPolicy.Balanced || C<ComboBox>("StartupPolicyPicker").SelectedIndex != (int)StartupFanPolicy.Balanced) throw new Exception("Default startup strategy");
            TryApplyStartupPolicy(startupSample, false, false, startupSample.Timestamp);
            if (selectedMode != FanMode.Bios || !startupPolicyGate.Pending) throw new Exception("Startup waits for hardware");
            TryApplyStartupPolicy(startupSample, true, false, startupSample.Timestamp);
            if (selectedMode != FanMode.Bios || !startupPolicyGate.Pending) throw new Exception("Startup waits for confirmed fan connection");
            TryApplyStartupPolicy(startupSample, true, true, startupSample.Timestamp);
            if (selectedMode != FanMode.Balanced || startupPolicyGate.Pending) throw new Exception("Startup automatically applies the default strategy");
            C<ComboBox>("StartupPolicyPicker").SelectedIndex = (int)StartupFanPolicy.Quiet;
            if (settings.StartupPolicy != StartupFanPolicy.Quiet || selectedMode != FanMode.Balanced) throw new Exception("Changing startup preference preserves the current strategy");
            startupPolicyGate = new StartupPolicyGate(); press("ModeCool");
            TryApplyStartupPolicy(startupSample, true, true, startupSample.Timestamp);
            if (selectedMode != FanMode.Cool || startupPolicyGate.Pending) throw new Exception("Manual selection cancels pending automatic startup");
            press("NavFan"); if (activePage != "Fan" || C<StackPanel>("FanPage").Visibility != Visibility.Visible) throw new Exception("Fan navigation");
            press("ThemeLight"); if (appliedTheme != "Light") throw new Exception("Light theme");
            press("ThemeDark"); if (appliedTheme != "Dark") throw new Exception("Dark theme");
            press("ModeCool"); if (curveDraft.Points[1].Temperature != 40) throw new Exception("Cool curve");
            press("BoostFan"); if (C<Slider>("ManualSlider").Value != 7) throw new Exception("Boost level");
            press("ResetCurve"); C<TextBox>("SelectedTemperature").Text = "abc"; press("SaveCurve");
            if (!C<TextBlock>("CurveMessage").Text.Contains("整数")) throw new Exception("Invalid curve handling");
            press("ResetCurve"); root.Measure(new Size(1080, 790)); root.Arrange(new Rect(0, 0, 1080, 790)); root.UpdateLayout();
            if (curveChart.ActualWidth < 500 || curveChart.ActualHeight < 170) throw new Exception("Curve chart layout");
            curveChart.BeginPointer(curveChart.PointFor(48, 1), false);
            curveChart.MovePointer(curveChart.PointFor(52, 2)); curveChart.EndPointer();
            if (curveDraft.Points[1].Temperature != 52 || curveDraft.Points[1].Level != 2) throw new Exception("Chart drag updates values");
            press("UndoCurve"); if (curveDraft.Points[1].Temperature != 48) throw new Exception("Undo drag");
            press("RedoCurve"); if (curveDraft.Points[1].Temperature != 52) throw new Exception("Redo drag");
            curveChart.BeginPointer(curveChart.PointFor(63, 3), false);
            if (curveDraft.Points.Count != 7 || C<TextBox>("SelectedTemperature").Text != "63") throw new Exception("Click adds node");
            curveChart.BeginPointer(curveChart.PointFor(63, 3), true);
            if (curveDraft.Points.Count != 6) throw new Exception("Right-click deletes node");
            press("ResetCurve"); curveDraft.Select(2); C<TextBox>("SelectedTemperature").Text = "60"; C<TextBox>("SelectedLevel").Text = "3"; press("UpdateCurvePoint");
            if (curveDraft.Points[2].Temperature != 60 || curveDraft.Points[2].Level != 3) throw new Exception("Numeric editing");
            curveDraft.Add(40, 0); curveDraft.Add(52, 1); curveDraft.Add(73, 4);
            press("SaveCurve"); if (settings.Curve.Length != 9 || curveDraft.IsDirty) throw new Exception("Save variable-length curve");
            C<TextBox>("CurveSlotName").Text = "日常办公"; press("SaveCurveSlot");
            if (!settings.CurveSlots[0].HasCurve || settings.CurveSlots[0].Name != "日常办公" || settings.CurveSlots[0].Curve.Length != 9) throw new Exception("Save named curve slot");
            C<ComboBox>("StartupPolicyPicker").SelectedIndex = (int)StartupFanPolicy.Slot1;
            startupPolicyGate = new StartupPolicyGate(); TryApplyStartupPolicy(startupSample, true, true, startupSample.Timestamp);
            if (selectedMode != FanMode.Custom || settings.Curve.Length != 9) throw new Exception("Saved curve slot used as startup strategy");
            C<ComboBox>("StartupPolicyPicker").SelectedIndex = (int)StartupFanPolicy.Balanced;
            press("ModeQuiet"); press("LoadCurveSlot");
            if (curveDraft.Points.Count != 9 || selectedMode != FanMode.Quiet) throw new Exception("Loading slot edits draft without changing running policy");
            press("ApplyCurveSlot"); if (selectedMode != FanMode.Custom || settings.Curve.Length != 9) throw new Exception("Apply saved slot");
            C<ComboBox>("CurveSlotPicker").SelectedIndex = 1;
            if (C<Button>("LoadCurveSlot").IsEnabled || C<Button>("ApplyCurveSlot").IsEnabled) throw new Exception("Empty slot controls");
            C<TextBox>("CurveSlotName").Text = "长时间工作"; press("SaveCurveSlot");
            if (!settings.CurveSlots[1].HasCurve || settings.CurveSlots[0].Name != "日常办公") throw new Exception("Independent second curve slot");
            C<ComboBox>("CurveSlotPicker").SelectedIndex = 0; C<TextBox>("CurveSlotName").Text = "办公改版"; press("SaveCurveSlot");
            if (settings.CurveSlots[0].Name != "办公改版") throw new Exception("Overwrite and rename slot");
            press("ModeBalanced"); curveDraft.Select(2); curveDraft.MoveSelected(60, 3);
            string draftBeforeBoost = string.Join(";", curveDraft.Points.Select(x => x.Temperature + ":" + x.Level));
            press("QuickFullSpeed"); if (!isFullSpeed || selectedMode != FanMode.Manual || C<Slider>("ManualSlider").Value != 7) throw new Exception("Top full-speed shortcut");
            press("QuickFullSpeed"); if (isFullSpeed || selectedMode != FanMode.Balanced) throw new Exception("Full speed returns to previous curve policy");
            if (draftBeforeBoost != string.Join(";", curveDraft.Points.Select(x => x.Temperature + ":" + x.Level))) throw new Exception("Full speed preserves edited curve");
            C<Slider>("ManualSlider").Value = 3; press("ApplyManual"); C<Slider>("ManualSlider").Value = 5;
            using (var trayMenu = CreateTrayMenu())
            {
                fullSpeedTrayItem.PerformClick(); if (!isFullSpeed || !fullSpeedTrayItem.Checked) throw new Exception("Tray full-speed shortcut");
                fullSpeedTrayItem.PerformClick(); if (isFullSpeed || selectedMode != FanMode.Manual || C<Slider>("ManualSlider").Value != 3) throw new Exception("Tray restores previous manual level");
            }
            fullSpeedTrayItem = null;
            curveDraft.Select(curveDraft.Points.Count - 1);
            if (C<Button>("DeleteCurvePoint").IsEnabled || !C<TextBox>("SelectedLevel").IsReadOnly) throw new Exception("Final node controls");
            press("ResetCurve"); press("SaveCurve"); if (settings.Curve[1].Temperature != 48) throw new Exception("Save custom curve");
            root.Measure(new Size(960, 700)); root.Arrange(new Rect(0, 0, 960, 700)); root.UpdateLayout();
            if (curveChart.ActualWidth < 450) throw new Exception("Minimum width chart layout");
            var scrollBar = FindVisual<System.Windows.Controls.Primitives.ScrollBar>(C<ScrollViewer>("PageScroll"));
            if (scrollBar == null) throw new Exception("Scroll bar visual");
            scrollBar.ApplyTemplate();
            if (scrollBar.Template.FindName("ScrollThumb", scrollBar) == null) throw new Exception("Custom scroll bar template");
            press("NavMonitor"); Present(new Snapshot()); if (C<TextBlock>("CpuTemp").Text != "— °C") throw new Exception("Missing sensor display");
            press("NavSettings"); root.Measure(new Size(960, 700)); root.Arrange(new Rect(0, 0, 960, 700)); root.UpdateLayout();
            TestDeviceUi(root, press); TestCpuLimitUi(); TestModeInteractionUi(root, press);
            press("Reconnect");
            if (C<TextBlock>("SettingsMessage").Text != "正在重启…") throw new Exception("Reconnect progress status");
            File.WriteAllText(output, "PASS: startup strategy and manual override, named slots, fan/full-speed shortcuts, curve editing, reconnect status, battery/SSD pages, read-only monitor, dedicated performance page and mode definitions, fresh CPU policy readback, revision-verified setting feedback, themed client title bar, minimum-size layout.\r\n");
        }
        private static TVisual FindVisual<TVisual>(DependencyObject parent) where TVisual : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i); var match = child as TVisual;
                if (match != null) return match;
                var nested = FindVisual<TVisual>(child); if (nested != null) return nested;
            }
            return null;
        }
        private void Exit() { if (exiting) return; exiting = true; Dispose(); Application.Current.Shutdown(); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (timer != null) timer.Stop();
            windowFrame.Dispose();
            if (performanceController != null) { try { performanceController.Dispose(); } catch (Exception ex) { Log.Write(ex); } }
            if (engine != null) engine.Dispose();
            if (tray != null) { tray.Visible = false; tray.Icon.Dispose(); tray.Dispose(); }
            if (showEvent != null) showEvent.Dispose();
            if (!preview) { SystemEvents.PowerModeChanged -= OnPower; Application.Current.SessionEnding -= OnSessionEnding; }
        }
    }
}
