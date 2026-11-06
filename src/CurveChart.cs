using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace CarbonControl
{
    public sealed class CurveChart : FrameworkElement
    {
        private readonly CurveDraft draft;
        private bool dragging;
        private int hovered = -1;
        private double? currentTemperature;
        public CurveChart(CurveDraft model)
        {
            draft = model; Focusable = true; ClipToBounds = true; MinHeight = 170;
            AutomationProperties.SetName(this, "风扇曲线编辑器，左右调整温度，上下调整档位，Delete 删除节点");
            draft.Changed += InvalidateVisual;
            Cursor = Cursors.Cross;
            SizeChanged += (s, e) => InvalidateVisual();
        }
        public double? CurrentTemperature
        {
            get { return currentTemperature; }
            set { if (currentTemperature != value) { currentTemperature = value; InvalidateVisual(); } }
        }
        internal Rect Plot
        { get { return new Rect(42, 30, Math.Max(1, ActualWidth - 64), Math.Max(1, ActualHeight - 65)); } }
        internal Point PointFor(int temperature, int level)
        { var p = Plot; return new Point(p.Left + (temperature - 25) / 70.0 * p.Width, p.Bottom - level / 7.0 * p.Height); }
        private void ValueAt(Point location, out int temperature, out int level)
        {
            var p = Plot;
            temperature = (int)Math.Round(25 + (location.X - p.Left) / p.Width * 70, MidpointRounding.AwayFromZero);
            level = (int)Math.Round((p.Bottom - location.Y) / p.Height * 7, MidpointRounding.AwayFromZero);
        }
        private Brush Color(string key) { return TryFindResource(key) as Brush ?? Brushes.Gray; }
        private void Text(DrawingContext dc, string value, double x, double y, string color, double size, bool centered)
        {
            var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI, Microsoft YaHei UI"), size, Color(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(centered ? x - text.Width / 2 : x, y));
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            var p = Plot; var points = draft.Points;
            if (points.Count == 0) return;
            for (int i = 0; i <= 7; i++)
            {
                double y = PointFor(25, i).Y;
                dc.DrawLine(new Pen(Color("Line"), 1), new Point(p.Left, y), new Point(p.Right, y));
                Text(dc, i.ToString(), p.Left - 19, y - 8, "Muted", 11, true);
            }
            foreach (int t in new[] { 25, 35, 45, 55, 65, 75, 85, 95 })
            {
                double x = PointFor(t, 0).X;
                dc.DrawLine(new Pen(Color("Line"), .65), new Point(x, p.Top), new Point(x, p.Bottom));
                Text(dc, t.ToString(), x, p.Bottom + 10, "Muted", 10, true);
            }
            Text(dc, "档位", 0, 5, "Muted", 11, false);
            Text(dc, "温度 °C", p.Right - 42, ActualHeight - 14, "Muted", 10, false);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                Point last = PointFor(25, points[0].Level);
                g.BeginFigure(last, false, false);
                foreach (var point in points)
                {
                    Point next = PointFor(point.Temperature, point.Level);
                    g.LineTo(new Point(next.X, last.Y), true, false); g.LineTo(next, true, false); last = next;
                }
                g.LineTo(new Point(p.Right, last.Y), true, false);
            }
            dc.DrawGeometry(null, new Pen(Color("Accent"), 2.5) { LineJoin = PenLineJoin.Round }, geometry);
            if (SensorMap.Valid(currentTemperature))
            {
                double t = Math.Max(25, Math.Min(95, currentTemperature.Value));
                double x = p.Left + (t - 25) / 70 * p.Width;
                var marker = new Pen(Color("Green"), 1) { DashStyle = DashStyles.Dash };
                dc.DrawLine(marker, new Point(x, p.Top), new Point(x, p.Bottom));
                Text(dc, "当前 " + currentTemperature.Value.ToString("0") + "°C", Math.Max(p.Left + 35, Math.Min(p.Right - 35, x)), 3, "Green", 11, true);
            }
            for (int i = 0; i < points.Count; i++)
            {
                Point at = PointFor(points[i].Temperature, points[i].Level);
                bool selected = i == draft.SelectedIndex;
                if (selected || hovered == i)
                {
                    var halo = Color("Accent").Clone(); halo.Opacity = .16;
                    dc.DrawEllipse(halo, null, at, selected ? 13 : 11, selected ? 13 : 11);
                }
                dc.DrawEllipse(selected ? Color("Card") : Color("Accent"), new Pen(Color("Accent"), selected ? 3 : 1), at, selected ? 6 : 4.5, selected ? 6 : 4.5);
            }
            if (draft.SelectedIndex >= 0 && draft.SelectedIndex < points.Count)
            {
                var selected = points[draft.SelectedIndex]; Point at = PointFor(selected.Temperature, selected.Level);
                string value = selected.Temperature + "°C  ·  " + selected.Level + " 档";
                double bx = Math.Max(p.Left, Math.Min(p.Right - 108, at.X - 54));
                double by = at.Y < p.Top + 30 ? at.Y + 18 : at.Y - 40;
                dc.DrawRoundedRectangle(Color("Input"), new Pen(Color("Line"), 1), new Rect(bx, by, 108, 27), 5, 5);
                Text(dc, value, bx + 54, by + 5, "Text", 11, true);
            }
        }
        private int Hit(Point position)
        {
            var points = draft.Points; int nearest = -1; double distance = 16 * 16;
            for (int i = 0; i < points.Count; i++)
            {
                Point p = PointFor(points[i].Temperature, points[i].Level);
                double d = (p.X - position.X) * (p.X - position.X) + (p.Y - position.Y) * (p.Y - position.Y);
                if (d <= distance) { distance = d; nearest = i; }
            }
            return nearest;
        }
        internal bool BeginPointer(Point position, bool remove)
        {
            int index = Hit(position);
            if (remove) { if (index >= 0) { draft.Select(index); draft.DeleteSelected(); } return false; }
            if (!Plot.Contains(position) && index < 0) return false;
            if (index >= 0) { draft.Select(index); draft.BeginEdit(); dragging = true; }
            else
            {
                int t, l; ValueAt(position, out t, out l); draft.Add(t, l);
            }
            return dragging;
        }
        internal void MovePointer(Point position)
        {
            if (!dragging) return;
            int t, l; ValueAt(position, out t, out l); draft.MoveSelected(t, l);
        }
        internal void EndPointer()
        { if (dragging) { dragging = false; draft.EndEdit(); } }
        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Right) return;
            Focus(); if (BeginPointer(e.GetPosition(this), e.ChangedButton == MouseButton.Right)) CaptureMouse(); e.Handled = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point at = e.GetPosition(this); MovePointer(at);
            int hit = Hit(at); if (hit != hovered) { hovered = hit; InvalidateVisual(); }
            Cursor = dragging ? Cursors.SizeAll : hit >= 0 ? Cursors.Hand : Cursors.Cross;
        }
        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e); if (e.ChangedButton != MouseButton.Left) return;
            MovePointer(e.GetPosition(this)); EndPointer(); if (IsMouseCaptured) ReleaseMouseCapture(); e.Handled = true;
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) { EndPointer(); base.OnLostMouseCapture(e); }
        protected override void OnMouseLeave(MouseEventArgs e) { hovered = -1; InvalidateVisual(); base.OnMouseLeave(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Z) { draft.Undo(); e.Handled = true; return; }
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Y) { draft.Redo(); e.Handled = true; return; }
            if (e.Key == Key.Delete || e.Key == Key.Back) { draft.DeleteSelected(); e.Handled = true; return; }
            int index = draft.SelectedIndex; var points = draft.Points;
            if (index < 0 || index >= points.Count) return;
            int t = points[index].Temperature, l = points[index].Level;
            int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5 : 1;
            if (e.Key == Key.Left) t -= step; else if (e.Key == Key.Right) t += step;
            else if (e.Key == Key.Up) l++; else if (e.Key == Key.Down) l--; else return;
            draft.MoveSelected(t, l); e.Handled = true;
        }
    }
}
