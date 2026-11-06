using System;
using System.Collections.Generic;

namespace CarbonControl
{
    public static class CurveEditingTests
    {
        private static int count;
        private static void Assert(bool condition, string name)
        {
            count++;
            if (!condition) throw new Exception("Curve editor test failed: " + name);
        }
        private static CurvePoint[] ThreePoints()
        {
            return new[] { new CurvePoint(30, 0), new CurvePoint(55, 3), new CurvePoint(85, 7) };
        }
        private static string Values(IList<CurvePoint> points)
        {
            string value = "";
            foreach (var p in points) value += p.Temperature + ":" + p.Level + ";";
            return value;
        }

        public static int Run()
        {
            count = 0;
            var draft = new CurveDraft();
            var original = ThreePoints();
            draft.Load(original);
            Assert(!draft.IsDirty && !draft.CanUndo && !draft.CanRedo, "loaded baseline");
            original[0].Temperature = 29;
            Assert(draft.Points[0].Temperature == 30, "input does not alias model");
            var exposed = draft.Points;
            exposed[0].Temperature = 26;
            Assert(draft.Points[0].Temperature == 30, "output does not alias model");

            int events = 0;
            draft.Changed += () => events++;
            draft.Select(1);
            Assert(events == 1 && !draft.IsDirty && !draft.CanUndo, "selection has no undo entry");
            draft.MoveSelected(100, 20);
            Assert(draft.Points[1].Temperature == 84 && draft.Points[1].Level == 7, "drag clamps to next node");
            draft.MoveSelected(-100, -20);
            Assert(draft.Points[1].Temperature == 31 && draft.Points[1].Level == 0, "drag clamps to previous node");
            draft.Select(2); draft.MoveSelected(200, 0);
            Assert(draft.Points[2].Temperature == 90 && draft.Points[2].Level == 7, "endpoint is maximum by 90 degrees");
            draft.Select(0); draft.MoveSelected(0, 7);
            Assert(draft.Points[0].Temperature == 25 && draft.Points[0].Level == 0, "first node limits");
            FanPolicy.Validate(draft.Points);

            draft.Load(ThreePoints()); draft.Select(1);
            string before = Values(draft.Points);
            draft.BeginEdit();
            for (int t = 56; t <= 70; t++) draft.MoveSelected(t, 4);
            Assert(!draft.CanUndo && draft.IsDirty, "active drag groups history");
            draft.EndEdit(); string after = Values(draft.Points);
            Assert(draft.CanUndo && before != after, "finished drag creates history");
            draft.Undo();
            Assert(Values(draft.Points) == before && !draft.CanUndo && !draft.IsDirty && draft.CanRedo, "one undo restores full drag");
            draft.Redo();
            Assert(Values(draft.Points) == after && draft.IsDirty && !draft.CanRedo, "one redo restores full drag");
            draft.MarkSaved();
            Assert(!draft.IsDirty && draft.CanUndo, "saving keeps history");
            draft.Undo(); Assert(draft.IsDirty, "undo after save differs from baseline");
            draft.Redo(); Assert(!draft.IsDirty, "redo reaches saved baseline");
            draft.Undo(); draft.MoveSelected(62, 2);
            Assert(!draft.CanRedo, "new edit clears redo branch");

            draft.Load(ThreePoints()); draft.Select(1); draft.BeginEdit();
            draft.MoveSelected(60, 4); draft.MoveSelected(55, 3); draft.EndEdit();
            Assert(!draft.IsDirty && !draft.CanUndo, "round trip drag creates no history");
            draft.BeginEdit(); draft.BeginEdit(); draft.MoveSelected(60, 4); draft.EndEdit();
            Assert(!draft.CanUndo, "nested edit remains grouped");
            draft.EndEdit(); draft.Undo(); Assert(!draft.IsDirty && !draft.CanUndo, "nested edit one undo");
            draft.BeginEdit(); draft.MoveSelected(61, 4); draft.Undo();
            Assert(!draft.IsDirty && draft.CanRedo, "undo completes active gesture");

            draft.Load(ThreePoints());
            Assert(draft.Add(45, 6), "insert interior node");
            Assert(draft.Points.Count == 4 && draft.SelectedIndex == 1 && draft.Points[1].Temperature == 45 && draft.Points[1].Level == 3, "insert sorted and level-clamped");
            Assert(!draft.Add(55, 1) && draft.SelectedIndex == 2 && draft.Points.Count == 4, "duplicate selects existing node");
            draft.Undo();
            Assert(draft.Points.Count == 3 && !draft.IsDirty, "insert undo");
            draft.Redo();
            Assert(draft.Points.Count == 4 && draft.SelectedIndex == 2, "insert redo preserves selection before undo");
            Assert(draft.Add(200, -20) && draft.Points[draft.Points.Count - 1].Temperature == 90 && draft.Points[draft.Points.Count - 1].Level == 7, "add final endpoint");
            Assert(!draft.DeleteSelected(), "final endpoint cannot be deleted");
            draft.Select(0); Assert(draft.DeleteSelected() && draft.SelectedIndex == 0, "delete first node");
            draft.Undo(); Assert(draft.Points[0].Temperature == 30 && draft.SelectedIndex == 0, "undo delete");
            draft.Load(new[] { new CurvePoint(25, 0), new CurvePoint(90, 7) });
            Assert(!draft.DeleteSelected(), "minimum two nodes retained");
            Assert(draft.Add(-200, 7) == false && draft.SelectedIndex == 0, "clamped duplicate minimum");
            for (int t = 26; t < 36; t++) Assert(draft.Add(t, 0), "add up to capacity " + t);
            Assert(draft.Points.Count == 12 && !draft.Add(50, 4), "maximum node count");
            Assert(!draft.Add(28, 0) && draft.SelectedIndex == 3, "duplicate selection works at capacity");

            draft.Load(new[] { new CurvePoint(25, 0), new CurvePoint(26, 1), new CurvePoint(27, 7) });
            draft.Select(1); draft.MoveSelected(100, 20);
            Assert(draft.Points[1].Temperature == 26 && draft.Points[1].Level == 7, "adjacent temperatures stay distinct");
            draft.Select(-1);
            Assert(!draft.MoveSelected(50, 3) && !draft.DeleteSelected(), "no selected node");
            bool invalid = false;
            try { draft.Load(new[] { new CurvePoint(30, 1), new CurvePoint(80, 4) }); }
            catch (ArgumentException) { invalid = true; }
            Assert(invalid && draft.Points.Count == 3, "invalid load preserves draft");

            // Mixed edits explore boundaries while checking policy validity after each edit.
            draft.Load(ThreePoints()); var random = new Random(7126);
            for (int i = 0; i < 150; i++)
            {
                draft.Select(random.Next(draft.Points.Count));
                switch (i % 5)
                {
                    case 0: draft.MoveSelected(random.Next(-20, 130), random.Next(-3, 11)); break;
                    case 1: draft.Add(random.Next(20, 100), random.Next(-3, 11)); break;
                    case 2: draft.DeleteSelected(); break;
                    case 3: draft.Undo(); break;
                    case 4: draft.Redo(); break;
                }
                FanPolicy.Validate(draft.Points);
                Assert(draft.SelectedIndex >= -1 && draft.SelectedIndex < draft.Points.Count, "mixed edit selection " + i);
            }
            Assert(events > 50, "changes notify subscribers");
            return count;
        }
    }
}
