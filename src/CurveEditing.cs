using System;
using System.Collections.Generic;

namespace CarbonControl
{
    // The editable curve is a draft until the user saves it. History contains
    // whole gestures, so a drag can be undone with one command.
    public sealed class CurveDraft
    {
        private sealed class State
        {
            public List<CurvePoint> Points;
            public int Selected;
        }

        private const int HistoryLimit = 100;
        private List<CurvePoint> points;
        private List<CurvePoint> saved;
        private readonly List<State> undo = new List<State>();
        private readonly List<State> redo = new List<State>();
        private int selected = -1;
        private int editDepth;
        private State editStart;

        public event Action Changed;

        public CurveDraft() { Load(FanPolicy.Preset(FanMode.Balanced)); }

        public IList<CurvePoint> Points { get { return Copy(points).AsReadOnly(); } }
        public int SelectedIndex { get { return selected; } }
        public bool IsDirty { get { return !Same(points, saved); } }
        public bool CanUndo { get { return editDepth == 0 && undo.Count != 0; } }
        public bool CanRedo { get { return editDepth == 0 && redo.Count != 0; } }

        public void Load(IList<CurvePoint> curve)
        {
            FanPolicy.Validate(curve);
            points = Copy(curve);
            saved = Copy(curve);
            selected = 0;
            undo.Clear(); redo.Clear(); editDepth = 0; editStart = null;
            Notify();
        }

        public void Select(int index)
        {
            if (index < -1 || index >= points.Count) throw new ArgumentOutOfRangeException("index");
            if (selected == index) return;
            selected = index; Notify();
        }

        public void BeginEdit()
        {
            if (editDepth == 0) editStart = Capture();
            editDepth++;
        }

        public void EndEdit()
        {
            if (editDepth == 0) return;
            editDepth--;
            if (editDepth != 0) return;
            if (!Same(editStart.Points, points))
            {
                Push(undo, editStart);
                redo.Clear();
            }
            editStart = null;
            Notify();
        }

        public bool MoveSelected(int temperature, int level)
        {
            if (selected < 0) return false;
            int firstTemperature = selected == 0 ? 25 : points[selected - 1].Temperature + 1;
            int lastTemperature = selected == points.Count - 1 ? 90 : points[selected + 1].Temperature - 1;
            int firstLevel = selected == 0 ? 0 : points[selected - 1].Level;
            int lastLevel = selected == points.Count - 1 ? 7 : points[selected + 1].Level;
            temperature = Clamp(temperature, firstTemperature, lastTemperature);
            level = selected == points.Count - 1 ? 7 : Clamp(level, firstLevel, lastLevel);
            if (temperature == points[selected].Temperature && level == points[selected].Level) return false;
            var next = Copy(points);
            next[selected] = new CurvePoint(temperature, level);
            Change(next, selected);
            return true;
        }

        public bool Add(int temperature, int level)
        {
            temperature = Clamp(temperature, 25, 90);
            int index = 0;
            while (index < points.Count && points[index].Temperature < temperature) index++;
            if (index < points.Count && points[index].Temperature == temperature)
            {
                Select(index);
                return false;
            }
            if (points.Count >= 12) return false;
            int firstLevel = index == 0 ? 0 : points[index - 1].Level;
            int lastLevel = index == points.Count ? 7 : points[index].Level;
            level = index == points.Count ? 7 : Clamp(level, firstLevel, lastLevel);
            var next = Copy(points);
            next.Insert(index, new CurvePoint(temperature, level));
            Change(next, index);
            return true;
        }

        public bool DeleteSelected()
        {
            if (selected < 0 || selected == points.Count - 1 || points.Count <= 2) return false;
            var next = Copy(points);
            next.RemoveAt(selected);
            Change(next, Math.Min(selected, next.Count - 1));
            return true;
        }

        public void Undo()
        {
            FinishEdit();
            if (undo.Count == 0) return;
            Push(redo, Capture());
            Restore(Pop(undo));
            Notify();
        }

        public void Redo()
        {
            FinishEdit();
            if (redo.Count == 0) return;
            Push(undo, Capture());
            Restore(Pop(redo));
            Notify();
        }

        public void MarkSaved()
        {
            FinishEdit();
            saved = Copy(points);
            Notify();
        }

        private void Change(List<CurvePoint> next, int selection)
        {
            FanPolicy.Validate(next);
            if (editDepth == 0)
            {
                Push(undo, Capture());
                redo.Clear();
            }
            points = next;
            selected = selection;
            Notify();
        }

        private void FinishEdit()
        {
            if (editDepth == 0) return;
            editDepth = 1;
            EndEdit();
        }

        private State Capture() { return new State { Points = Copy(points), Selected = selected }; }
        private void Restore(State state) { points = Copy(state.Points); selected = state.Selected; }
        private static State Pop(List<State> history)
        {
            int last = history.Count - 1;
            State state = history[last]; history.RemoveAt(last); return state;
        }
        private static void Push(List<State> history, State state)
        {
            history.Add(state);
            if (history.Count > HistoryLimit) history.RemoveAt(0);
        }
        private static int Clamp(int value, int minimum, int maximum) { return Math.Max(minimum, Math.Min(maximum, value)); }
        private static List<CurvePoint> Copy(IList<CurvePoint> source)
        {
            var copy = new List<CurvePoint>(source.Count);
            foreach (var point in source) copy.Add(new CurvePoint(point.Temperature, point.Level));
            return copy;
        }
        private static bool Same(IList<CurvePoint> a, IList<CurvePoint> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Temperature != b[i].Temperature || a[i].Level != b[i].Level) return false;
            return true;
        }
        private void Notify() { var changed = Changed; if (changed != null) changed(); }
    }
}
