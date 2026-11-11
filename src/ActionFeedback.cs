using System;

namespace CarbonControl
{
    public enum ActionFeedbackKind { Pending, Success, Error }

    public sealed class ActionFeedbackState
    {
        private readonly object gate = new object();
        private long version;
        private string text = "";
        private ActionFeedbackKind kind;
        private DateTime expiresAt;

        public long Version { get { lock (gate) return version; } }
        public string Text { get { lock (gate) return text; } }
        public ActionFeedbackKind Kind { get { lock (gate) return kind; } }
        public DateTime ExpiresAt { get { lock (gate) return expiresAt; } }

        public long Begin(string message, DateTime now)
        {
            lock (gate)
            {
                version++; text = message ?? ""; kind = ActionFeedbackKind.Pending;
                expiresAt = DateTime.MaxValue;
                return version;
            }
        }
        public bool Complete(long actionVersion, bool success, string message, DateTime now)
        {
            lock (gate)
            {
                if (actionVersion == 0 || actionVersion != version) return false;
                text = message ?? "";
                kind = success ? ActionFeedbackKind.Success : ActionFeedbackKind.Error;
                expiresAt = now.AddSeconds(success ? 8 : 12);
                return true;
            }
        }
        public long Notify(bool success, string message, DateTime now)
        {
            lock (gate)
            {
                long actionVersion = Begin(message, now);
                Complete(actionVersion, success, message, now);
                return actionVersion;
            }
        }
        public bool Visible(DateTime now)
        {
            lock (gate)
                return version != 0 && (kind == ActionFeedbackKind.Pending || now < expiresAt);
        }
    }
}
