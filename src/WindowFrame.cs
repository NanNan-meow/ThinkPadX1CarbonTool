using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CarbonControl
{
    // DWM colors: https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
    // Windows 10 theme preference and attribute-19 fallback follow Microsoft's ZoomIt implementation:
    // https://github.com/microsoft/PowerToys/blob/main/src/modules/ZoomIt/ZoomIt/Utility.cpp
    public sealed class WindowFrame : IDisposable
    {
        private readonly Window window;
        private HwndSource source;
        private DispatcherOperation pending;
        private bool light, hasTheme, applying, disposed, nativeThemeSet;
        private bool previousLight;

        public WindowFrame(Window target)
        {
            if (target == null) throw new ArgumentNullException("target");
            window = target;
            window.SourceInitialized += OnSourceInitialized;
            window.Activated += OnActivation;
            window.Deactivated += OnActivation;
        }
        public void Apply(bool useLightTheme)
        {
            if (disposed) return;
            window.Dispatcher.VerifyAccess();
            light = useLightTheme; hasTheme = true;
            Attach(); Paint();
        }
        private void OnSourceInitialized(object sender, EventArgs e) { Attach(); Paint(); }
        private void OnActivation(object sender, EventArgs e) { QueueUpdate(); }
        private void Attach()
        {
            if (source != null || disposed) return;
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            source = HwndSource.FromHwnd(hwnd);
            if (source != null) source.AddHook(OnMessage);
        }
        private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Leave non-client handling to Windows: buttons, dragging, resize and system menu.
            if (message == 0x0006 || message == 0x0086 || message == 0x001A || message == 0x031A || message == 0x031E)
                QueueUpdate();
            return IntPtr.Zero;
        }
        private void QueueUpdate()
        {
            if (disposed || applying || !hasTheme || pending != null || window.Dispatcher.HasShutdownStarted) return;
            pending = window.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(delegate
            {
                pending = null;
                if (!disposed) { Attach(); Paint(); }
            }));
        }
        private void Paint()
        {
            if (disposed || applying || !hasTheme || source == null || source.IsDisposed) return;
            applying = true;
            try
            {
                IntPtr hwnd = source.Handle;
                bool themeChanged = !nativeThemeSet || previousLight != light;
                if (themeChanged)
                {
                    // Set once per palette change. These process/window preferences let Windows 10
                    // use the app-selected theme even when the system selects the opposite one.
                    nativeThemeSet = true; previousLight = light;
                    NativeDarkTheme.Apply(hwnd, light);
                }
                FramePalette colors = Colors(window.Resources, light);
                ApplyAttributes((attribute, value) => SetAttribute(hwnd, attribute, value), light, colors);
                if (themeChanged) SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
                RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, 0x0401);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            finally { applying = false; }
        }
        internal sealed class FramePalette
        {
            internal int Border, Caption, Text;
        }
        internal static FramePalette Colors(ResourceDictionary resources, bool useLightTheme)
        {
            return new FramePalette {
                Border = ResourceColor(resources, "Line", useLightTheme ? 0xE8E4E1 : 0x393430),
                Caption = ResourceColor(resources, "Bg", useLightTheme ? 0xF8F6F5 : 0x141210),
                Text = ResourceColor(resources, "Text", useLightTheme ? 0x2A2420 : 0xF4F3F1)
            };
        }
        internal static int ColorRef(Color color) { return color.R | color.G << 8 | color.B << 16; }
        private static int ResourceColor(ResourceDictionary resources, string key, int fallback)
        { var brush = resources[key] as SolidColorBrush; return brush == null ? fallback : ColorRef(brush.Color); }
        internal static void ApplyAttributes(Func<int, int, int> set, bool useLightTheme, FramePalette colors)
        {
            int dark = useLightTheme ? 0 : 1;
            if (set(20, dark) < 0) set(19, dark);
            // On Windows 11, explicit colors override the default active/inactive colors.
            // Windows 10 returns E_INVALIDARG for these three attributes and keeps the native theme.
            set(34, colors.Border); set(35, colors.Caption); set(36, colors.Text);
        }
        private static int SetAttribute(IntPtr hwnd, int attribute, int value)
        { return DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (pending != null) { pending.Abort(); pending = null; }
            window.SourceInitialized -= OnSourceInitialized;
            window.Activated -= OnActivation; window.Deactivated -= OnActivation;
            if (source != null && !source.IsDisposed) source.RemoveHook(OnMessage);
            source = null;
        }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

        private static class NativeDarkTheme
        {
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int PreferredMode(int mode);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            [return: MarshalAs(UnmanagedType.I1)] private delegate bool AllowDark(IntPtr hwnd, [MarshalAs(UnmanagedType.I1)] bool allow);
            private static readonly PreferredMode preferred;
            private static readonly AllowDark allow;
            static NativeDarkTheme()
            {
                try
                {
                    var version = Environment.OSVersion.Version;
                    if (version.Major < 10 || version.Build < 17763) return;
                    IntPtr library = LoadLibraryEx("uxtheme.dll", IntPtr.Zero, 0x00000800);
                    if (library == IntPtr.Zero) return;
                    IntPtr perWindow = GetProcAddress(library, new IntPtr(133));
                    if (perWindow != IntPtr.Zero) allow = (AllowDark)Marshal.GetDelegateForFunctionPointer(perWindow, typeof(AllowDark));
                    if (version.Build >= 18362)
                    {
                        IntPtr perApp = GetProcAddress(library, new IntPtr(135));
                        if (perApp != IntPtr.Zero) preferred = (PreferredMode)Marshal.GetDelegateForFunctionPointer(perApp, typeof(PreferredMode));
                    }
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
            internal static void Apply(IntPtr hwnd, bool useLightTheme)
            {
                if (preferred != null) preferred(useLightTheme ? 3 : 2);
                if (allow != null) allow(hwnd, !useLightTheme);
            }
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryExW")]
            private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
            [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);
        }
    }
}
