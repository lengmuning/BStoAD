namespace Bstoad.Worker.Gui;

internal sealed record DayStat(DateTime Day, int Created, int Moved, int Disabled, int Failures);

/// <summary>按天分组的柱状图（新建 / 移动 / 禁用 / 失败）。</summary>
internal sealed class StatsChart : Control
{
    private static readonly (string Name, Color Color)[] Series =
    {
        ("新建", Color.FromArgb(46, 134, 222)),
        ("移动", Color.FromArgb(120, 170, 60)),
        ("禁用", Color.FromArgb(240, 150, 30)),
        ("失败", Color.FromArgb(220, 60, 60)),
    };

    private List<DayStat> _data = new();

    public StatsChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
    }

    public void SetData(List<DayStat> data)
    {
        _data = data;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        using var textBrush = new SolidBrush(Color.FromArgb(80, 80, 80));
        using var gridPen = new Pen(Color.FromArgb(230, 230, 230));

        // 图例
        float lx = 60;
        foreach (var (name, color) in Series)
        {
            using var b = new SolidBrush(color);
            g.FillRectangle(b, lx, 8, 12, 12);
            g.DrawString(name, Font, textBrush, lx + 16, 6);
            lx += 70;
        }

        if (_data.Count == 0)
        {
            g.DrawString("暂无数据（只统计正式运行；可勾选「包含干跑」查看）", Font, textBrush, 60, Height / 2f - 10);
            return;
        }

        var plot = new RectangleF(50, 32, Width - 70, Height - 62);
        var max = Math.Max(1, _data.Max(d => Math.Max(Math.Max(d.Created, d.Moved), Math.Max(d.Disabled, d.Failures))));
        var niceMax = NiceCeiling(max);

        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - plot.Height * i / 4;
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            var label = (niceMax * i / 4.0).ToString("0.#");
            var sz = g.MeasureString(label, Font);
            g.DrawString(label, Font, textBrush, plot.Left - sz.Width - 4, y - sz.Height / 2);
        }

        var groupW = plot.Width / _data.Count;
        var barW = Math.Max(2, Math.Min(18, (groupW - 6) / Series.Length));
        for (var i = 0; i < _data.Count; i++)
        {
            var d = _data[i];
            var values = new[] { d.Created, d.Moved, d.Disabled, d.Failures };
            var gx = plot.Left + groupW * i + (groupW - barW * Series.Length) / 2;
            for (var s = 0; s < Series.Length; s++)
            {
                var h = plot.Height * values[s] / niceMax;
                using var b = new SolidBrush(Series[s].Color);
                g.FillRectangle(b, gx + barW * s, plot.Bottom - h, barW - 1, h);
            }
            // 天数多时隔几天标一次日期
            var step = _data.Count > 16 ? 3 : _data.Count > 8 ? 2 : 1;
            if (i % step == 0 || i == _data.Count - 1)
            {
                var label = d.Day.ToString("MM-dd");
                var sz = g.MeasureString(label, Font);
                g.DrawString(label, Font, textBrush, plot.Left + groupW * i + (groupW - sz.Width) / 2, plot.Bottom + 4);
            }
        }
    }

    private static int NiceCeiling(int v)
    {
        var mag = (int)Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 4, 5, 10 })
            if (m * mag >= v) return Math.Max(4, m * mag);
        return v;
    }
}
