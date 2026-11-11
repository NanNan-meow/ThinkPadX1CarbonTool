using System;
using System.Windows;

namespace CarbonControl
{
    public sealed partial class MainUi
    {
        private void InitializeWindowCommands()
        {
            Click("WindowMinimize", () => SystemCommands.MinimizeWindow(window));
            Click("WindowMaximize", () => { if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window); else SystemCommands.MaximizeWindow(window); });
            Click("WindowClose", () => SystemCommands.CloseWindow(window));
            window.StateChanged += (s, e) => UpdateWindowState();
            UpdateWindowState();
        }
        private void UpdateWindowState()
        {
            bool maximized = window.WindowState == WindowState.Maximized;
            T("WindowMaximizeGlyph", maximized ? "\uE923" : "\uE922");
            C<System.Windows.Controls.Button>("WindowMaximize").ToolTip = maximized ? "还原" : "最大化";
        }
    }
}
