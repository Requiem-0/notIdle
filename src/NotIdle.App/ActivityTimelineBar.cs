using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NotIdle.Core;

namespace NotIdle.App;

/// <summary>
/// A sleek, color-coded horizontal timeline rendering the recent history of activity states.
/// Visualizes the honest distinction between genuine interaction and keep-awake periods.
/// </summary>
internal sealed class ActivityTimelineBar : Control
{
    private IReadOnlyList<ActivityBucket> _buckets = [];

    public ActivityTimelineBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Height = 16;
        Width = 450;
        BackColor = Theme.Surface;
    }

    public void SetHistory(IReadOnlyList<ActivityBucket> buckets)
    {
        _buckets = buckets;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);

        using (GraphicsPath path = Theme.RoundedRect(bounds, 3))
        {
            using var bgBrush = new SolidBrush(Color.FromArgb(0xEA, 0xEE, 0xF2));
            e.Graphics.FillPath(bgBrush, path);

            if (_buckets.Count > 0)
            {
                e.Graphics.SetClip(path);
                float step = (float)Width / Math.Max(1, _buckets.Count);

                for (int i = 0; i < _buckets.Count; i++)
                {
                    Color color = Theme.ColorFor(_buckets[i].State);
                    using var brush = new SolidBrush(color);
                    float x = i * step;
                    e.Graphics.FillRectangle(brush, x, 0, step + 0.5f, Height);
                }
                e.Graphics.ResetClip();
            }

            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawPath(pen, path);
        }
    }
}
