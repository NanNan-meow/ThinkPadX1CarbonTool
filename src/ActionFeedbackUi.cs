using System;
using System.Windows;
using System.Windows.Controls;

namespace CarbonControl
{
    public sealed partial class MainUi
    {
        private readonly ActionFeedbackState actionFeedback = new ActionFeedbackState();
        private long performanceFeedbackVersion;
        private PendingFanAction pendingFanAction;
        private sealed class PendingFanAction
        {
            internal long Token;
            internal int Revision;
            internal FanMode Mode;
            internal DateTime Started;
            internal string Success;
            internal PerformanceMode? Performance;
        }
        private long BeginFeedback(string message)
        { long token = actionFeedback.Begin(message, DateTime.UtcNow); RenderFeedback(); return token; }
        private void NotifyFeedback(bool success, string message)
        { actionFeedback.Notify(success, message, DateTime.UtcNow); RenderFeedback(); }
        private void SavePreference(Action update, string message)
        {
            try { update(); Save(); T("SettingsMessage", message); NotifyFeedback(true, message); }
            catch (Exception ex) { Log.Write(ex); T("SettingsMessage", "设置未保存 · " + ex.Message); NotifyFeedback(false, "设置未保存 · " + ex.Message); }
        }
        private void FinishFeedback(long token, bool success, string message)
        { actionFeedback.Complete(token, success, message, DateTime.UtcNow); RenderFeedback(); }
        private void TrackFanAction(FanMode mode, bool performanceRequest)
        {
            long token = performanceRequest ? performanceFeedbackVersion : BeginFeedback("正在应用" + ModeLabels[(int)mode] + "，等待风扇读回…");
            if (token == 0 || token != actionFeedback.Version || actionFeedback.Kind != ActionFeedbackKind.Pending) return;
            pendingFanAction = new PendingFanAction { Token = token, Mode = mode, Revision = engine == null ? 0 : engine.ConfigurationRevision,
                Started = DateTime.UtcNow, Performance = performanceRequest ? (PerformanceMode?)performanceUiMode : null,
                Success = performanceRequest ? PerformanceProfiles.Get(performanceUiMode).Name + "模式已应用" : ModeLabels[(int)mode] + "已应用" };
        }
        private void UpdateFanAction(int revision, FanMode mode, int? level, bool confirmed, string status, DateTime now)
        {
            var action = pendingFanAction;
            if (action == null) { RenderFeedback(); return; }
            if (action.Token != actionFeedback.Version || actionFeedback.Kind != ActionFeedbackKind.Pending) { pendingFanAction = null; RenderFeedback(); return; }
            bool validLevel = level.HasValue && (mode == FanMode.Bios ? (level.Value & 128) != 0 : (level.Value & 0xC0) == 0);
            if (action.Performance.HasValue && action.Performance.Value != performanceUiMode)
            {
                actionFeedback.Complete(action.Token, false, "电源模式已改变 · 之前的操作已取消", now); pendingFanAction = null; RenderFeedback(); return;
            }
            if (confirmed && validLevel && revision >= action.Revision && mode == action.Mode)
            {
                if (action.Performance.HasValue && !preview && (performanceController == null || !performanceController.IsModeActive(action.Performance.Value)))
                {
                    actionFeedback.Complete(action.Token, false, "模式设置已变化 · 正在刷新当前读回", now);
                    pendingFanAction = null; performanceFeedbackVersion = 0; pendingPerformanceFan = null;
                    if (performanceController != null) performanceUiMode = performanceController.Mode;
                    RequestCpuPolicyRefresh(); UpdatePerformanceStatus(); RenderFeedback(); return;
                }
                string actual = mode == FanMode.Bios ? "系统自动" : level.HasValue ? "当前 " + (level.Value & 7) + " 档" : status;
                actionFeedback.Complete(action.Token, true, action.Success + " · " + actual + " · 读回已确认", now);
                pendingFanAction = null;
            }
            else if (now - action.Started >= TimeSpan.FromSeconds(10))
            {
                actionFeedback.Complete(action.Token, false, "风扇设置未确认 · " + status, now); pendingFanAction = null;
            }
            RenderFeedback();
        }
        private void RenderFeedback()
        {
            var bar = C<Border>("ActionFeedbackBar"); if (bar == null) return;
            bar.Visibility = actionFeedback.Visible(DateTime.UtcNow) ? Visibility.Visible : Visibility.Collapsed;
            bar.SetResourceReference(Border.BackgroundProperty, actionFeedback.Kind == ActionFeedbackKind.Error ? "AccentSoft" : "Card");
            bar.SetResourceReference(Border.BorderBrushProperty, actionFeedback.Kind == ActionFeedbackKind.Success ? "Green" : actionFeedback.Kind == ActionFeedbackKind.Error ? "Accent" : "Line");
            T("ActionFeedbackText", actionFeedback.Text);
            C<TextBlock>("ActionFeedbackText").SetResourceReference(TextBlock.ForegroundProperty,
                actionFeedback.Kind == ActionFeedbackKind.Success ? "Green" : actionFeedback.Kind == ActionFeedbackKind.Error ? "Accent" : "Text");
        }
    }
}
