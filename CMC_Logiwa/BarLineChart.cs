using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CMC_Logiwa
{
    /// <summary>One column of a <see cref="BarLineChart"/>.</summary>
    public sealed class ChartPoint
    {
        public string Label { get; set; }          // under the bar
        public double Bar { get; set; }            // bar height (left axis)
        public double? Line { get; set; }          // optional line value (right axis); null = gap
        public double Extra { get; set; }          // optional second, stacked bar part (e.g. problems), drawn in ExtraBrush on top
        public string Tooltip { get; set; }
    }

    /// <summary>A small bar chart with an optional line (second axis) drawn on top. No external library.</summary>
    public sealed class BarLineChart : FrameworkElement
    {
        private List<ChartPoint> _points = new List<ChartPoint>();
        private int _hover = -1;

        // Plot area, remembered for hit testing.
        private Rect _plot;

        public Brush BarBrush { get; set; } = Brushes.SteelBlue;
        public Brush ExtraBrush { get; set; } = Brushes.Firebrick;
        public Brush LineBrush { get; set; } = Brushes.DarkOrange;
        public Brush GridBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xE2, 0xE5, 0xEB));
        public Brush TextBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x75));
        public Brush HoverBrush { get; set; } = new SolidColorBrush(Color.FromArgb(0x22, 0x25, 0x63, 0xEB));

        /// <summary>Unit shown after right-axis numbers, e.g. " s".</summary>
        public string LineUnit { get; set; } = "";
        public string EmptyText { get; set; } = "No data for this period.";

        public void SetData(IEnumerable<ChartPoint> points)
        {
            _points = new List<ChartPoint>(points ?? new ChartPoint[0]);
            _hover = -1;
            ToolTip = null;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // so the whole area gets mouse events
            if (w < 60 || h < 60) return;

            bool anyData = false;
            foreach (var p in _points) if (p.Bar + p.Extra > 0 || p.Line.HasValue) { anyData = true; break; }
            if (_points.Count == 0 || !anyData)
            {
                var ft = Text(EmptyText, 13, TextBrush);
                dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
                _plot = Rect.Empty;
                return;
            }

            bool hasLine = false;
            double maxBar = 0, maxLine = 0;
            foreach (var p in _points)
            {
                maxBar = Math.Max(maxBar, p.Bar + p.Extra);
                if (p.Line.HasValue) { hasLine = true; maxLine = Math.Max(maxLine, p.Line.Value); }
            }
            double barTop = NiceMax(maxBar);
            double lineTop = NiceMax(maxLine);

            const double left = 44, bottom = 26, top = 10;
            double right = hasLine ? 52 : 10;
            _plot = new Rect(left, top, Math.Max(10, w - left - right), Math.Max(10, h - top - bottom));

            // grid + left axis
            var gridPen = new Pen(GridBrush, 1);
            for (int i = 0; i <= 4; i++)
            {
                double y = _plot.Bottom - _plot.Height * i / 4;
                dc.DrawLine(gridPen, new Point(_plot.Left, y), new Point(_plot.Right, y));
                var ft = Text(Format(barTop * i / 4), 11, TextBrush);
                dc.DrawText(ft, new Point(_plot.Left - 6 - ft.Width, y - ft.Height / 2));
                if (hasLine)
                {
                    var ft2 = Text(Format(lineTop * i / 4) + LineUnit, 11, LineBrush);
                    dc.DrawText(ft2, new Point(_plot.Right + 6, y - ft2.Height / 2));
                }
            }

            // bars
            int n = _points.Count;
            double slot = _plot.Width / n;
            double barW = Math.Max(1, Math.Min(48, slot * 0.7));
            for (int i = 0; i < n; i++)
            {
                var p = _points[i];
                double x = _plot.Left + slot * i + (slot - barW) / 2;

                if (i == _hover)
                    dc.DrawRectangle(HoverBrush, null, new Rect(_plot.Left + slot * i, _plot.Top, slot, _plot.Height));

                double hBar = p.Bar / barTop * _plot.Height;
                double hExtra = p.Extra / barTop * _plot.Height;
                if (hBar > 0) dc.DrawRectangle(BarBrush, null, new Rect(x, _plot.Bottom - hBar, barW, hBar));
                if (hExtra > 0) dc.DrawRectangle(ExtraBrush, null, new Rect(x, _plot.Bottom - hBar - hExtra, barW, hExtra));
            }

            // x labels: thin out so they never overlap
            int every = 1;
            double labelW = 0;
            foreach (var p in _points) labelW = Math.Max(labelW, Text(p.Label ?? "", 11, TextBrush).Width);
            while (slot * every < labelW + 8 && every < n) every++;
            for (int i = 0; i < n; i += every)
            {
                var ft = Text(_points[i].Label ?? "", 11, TextBrush);
                dc.DrawText(ft, new Point(_plot.Left + slot * i + (slot - ft.Width) / 2, _plot.Bottom + 5));
            }

            // line
            if (hasLine)
            {
                var pen = new Pen(LineBrush, 2) { LineJoin = PenLineJoin.Round };
                Point? prev = null;
                for (int i = 0; i < n; i++)
                {
                    if (!_points[i].Line.HasValue) { prev = null; continue; }
                    var pt = new Point(_plot.Left + slot * i + slot / 2, _plot.Bottom - _points[i].Line.Value / lineTop * _plot.Height);
                    if (prev.HasValue) dc.DrawLine(pen, prev.Value, pt);
                    dc.DrawEllipse(LineBrush, null, pt, 3, 3);
                    prev = pt;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_plot.IsEmpty || _points.Count == 0) return;
            Point p = e.GetPosition(this);
            int idx = -1;
            if (_plot.Contains(p)) idx = Math.Min(_points.Count - 1, (int)((p.X - _plot.Left) / (_plot.Width / _points.Count)));
            if (idx == _hover) return;
            _hover = idx;
            ToolTip = idx >= 0 ? _points[idx].Tooltip : null;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover == -1) return;
            _hover = -1;
            ToolTip = null;
            InvalidateVisual();
        }

        private FormattedText Text(string s, double size, Brush brush) =>
            new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        private static string Format(double v) => v >= 100 || Math.Abs(v - Math.Round(v)) < 0.001 ? v.ToString("0") : v.ToString("0.#");

        /// <summary>Rounds a maximum up to 1, 2, 5 x 10^n so the axis labels are tidy.</summary>
        private static double NiceMax(double max)
        {
            if (max <= 0) return 1;
            double pow = Math.Pow(10, Math.Floor(Math.Log10(max)));
            double f = max / pow;
            double nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 4 ? 4 : f <= 5 ? 5 : 10;
            return nice * pow;
        }
    }
}
