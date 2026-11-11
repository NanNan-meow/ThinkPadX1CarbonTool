using System;

namespace CarbonControl
{
    public static class ActionFeedbackTests
    {
        private static int count;
        private static void Assert(bool value, string label)
        { count++; if (!value) throw new Exception("Action feedback: " + label); }
        public static int Run()
        {
            count = 0;
            DateTime now = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
            var state = new ActionFeedbackState();
            Assert(state.Version == 0 && !state.Visible(now), "no feedback before the first action");
            Assert(!state.Complete(0, true, "unsolicited", now) && state.Version == 0 && !state.Visible(now), "completion without an action is ignored");
            long first = state.Begin("应用低温模式", now);
            Assert(first == 1 && state.Version == first && state.Text == "应用低温模式" && state.Kind == ActionFeedbackKind.Pending, "begin exposes pending action text and version");
            Assert(state.ExpiresAt == DateTime.MaxValue && state.Visible(now.AddDays(30)) && state.Visible(DateTime.MaxValue), "pending remains visible until a result arrives");
            Assert(state.Complete(first, true, "低温模式已应用", now.AddSeconds(20)), "latest action can complete");
            Assert(state.Kind == ActionFeedbackKind.Success && state.Text == "低温模式已应用" && state.ExpiresAt == now.AddSeconds(28), "success lifetime starts at completion rather than begin");
            Assert(state.Visible(now.AddSeconds(27.999)), "success stays visible through its eight second interval");
            Assert(!state.Visible(now.AddSeconds(28)), "success hides exactly at its deadline");

            long second = state.Begin("应用性能模式", now.AddMinutes(1));
            Assert(second > first && state.Visible(now.AddHours(1)) && state.Kind == ActionFeedbackKind.Pending, "a new action replaces expired success with pending feedback");
            Assert(!state.Complete(first, false, "旧操作失败", now.AddMinutes(1)), "an old error cannot overwrite a newer pending action");
            Assert(state.Version == second && state.Text == "应用性能模式" && state.Kind == ActionFeedbackKind.Pending && state.ExpiresAt == DateTime.MaxValue,
                "rejected completion leaves every pending field intact");
            Assert(state.Complete(second, false, "电源设置未应用", now.AddMinutes(1).AddSeconds(3)), "latest action accepts failure");
            Assert(state.Kind == ActionFeedbackKind.Error && state.ExpiresAt == now.AddMinutes(1).AddSeconds(15), "error remains available for twelve seconds from completion");
            Assert(state.Visible(now.AddMinutes(1).AddSeconds(14.999)) && !state.Visible(now.AddMinutes(1).AddSeconds(15)), "error visibility has the twelve second deadline");

            long third = state.Begin("保存曲线", now.AddMinutes(2));
            long fourth = state.Begin("应用新曲线", now.AddMinutes(2).AddMilliseconds(50));
            Assert(fourth > third && third > second, "rapid operations receive ordered unique versions");
            Assert(state.Complete(fourth, true, "新曲线已应用", now.AddMinutes(2).AddSeconds(1)), "newest of rapid operations completes");
            DateTime deadline = state.ExpiresAt;
            Assert(!state.Complete(third, true, "旧曲线已保存", now.AddMinutes(3)) && !state.Complete(second, false, "旧失败", now.AddMinutes(3)),
                "late acknowledgements cannot revive or replace newer feedback");
            Assert(state.Text == "新曲线已应用" && state.Kind == ActionFeedbackKind.Success && state.ExpiresAt == deadline && !state.Visible(now.AddMinutes(3)),
                "rejected late acknowledgements preserve the newest terminal result and expiry");

            long instant = state.Notify(true, "主题已切换", now.AddMinutes(4));
            Assert(instant > fourth && state.Version == instant && state.Kind == ActionFeedbackKind.Success && state.Text == "主题已切换", "instant settings use a fresh completed action");
            Assert(state.Visible(now.AddMinutes(4).AddSeconds(7)) && !state.Visible(now.AddMinutes(4).AddSeconds(8)), "instant success uses the same eight second lifetime");
            long failed = state.Notify(false, "存档未保存", now.AddMinutes(5));
            Assert(failed > instant && state.Kind == ActionFeedbackKind.Error && state.ExpiresAt == now.AddMinutes(5).AddSeconds(12), "instant failure uses the error lifetime");
            Assert(!state.Complete(instant, true, "旧主题反馈", now.AddMinutes(6)) && state.Text == "存档未保存" && state.Version == failed,
                "instant settings retain the same old-token protection");
            return count;
        }
    }
}
